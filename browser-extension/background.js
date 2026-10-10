const NATIVE_HOST = "jp.local.pcactivitytracker.browser";
let publishGeneration = 0;
let nativePort = null;
let nativeRequestQueue = Promise.resolve();

async function restrictLocalStorageAccess() {
  try {
    // The pairing token is needed only by extension pages and the service worker.
    // Do not expose extension storage to the YouTube content script.
    await chrome.storage.local.setAccessLevel({ accessLevel: "TRUSTED_CONTEXTS" });
  } catch {
    // Older Chromium versions without setAccessLevel continue to work; page
    // scripts still cannot enter the content script's isolated world.
  }
}

function toBase64Url(bytes) {
  let binary = "";
  for (const value of bytes) binary += String.fromCharCode(value);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
}

function equalText(left, right) {
  if (typeof left !== "string" || left.length !== right.length) return false;
  let difference = 0;
  for (let index = 0; index < left.length; index++)
    difference |= left.charCodeAt(index) ^ right.charCodeAt(index);
  return difference === 0;
}

async function authenticateDesktop(bridgeToken) {
  try {
    const nonceBytes = crypto.getRandomValues(new Uint8Array(16));
    const nonce = toBase64Url(nonceBytes);
    const key = await crypto.subtle.importKey(
      "raw", new TextEncoder().encode(bridgeToken), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
    const expected = toBase64Url(new Uint8Array(await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(nonce))));
    const result = await sendNative({ type: "challenge", nonce });
    return equalText(result?.proof, expected);
  } catch {
    return false;
  }
}

function connectNativeHost() {
  if (nativePort) return nativePort;
  const port = chrome.runtime.connectNative(NATIVE_HOST);
  port.onDisconnect.addListener(() => { if (nativePort === port) nativePort = null; });
  nativePort = port;
  return port;
}

function sendNative(message) {
  const execute = () => new Promise((resolve, reject) => {
    const port = connectNativeHost();
    const onMessage = response => {
      port.onMessage.removeListener(onMessage);
      port.onDisconnect.removeListener(onDisconnect);
      resolve(response);
    };
    const onDisconnect = () => {
      port.onMessage.removeListener(onMessage);
      reject(new Error(chrome.runtime.lastError?.message || "Native Messaging host disconnected."));
    };
    port.onMessage.addListener(onMessage);
    port.onDisconnect.addListener(onDisconnect);
    try { port.postMessage(message); }
    catch (error) {
      port.onMessage.removeListener(onMessage);
      port.onDisconnect.removeListener(onDisconnect);
      reject(error);
    }
  });
  const request = nativeRequestQueue.then(execute, execute);
  nativeRequestQueue = request.catch(() => undefined);
  return request;
}

function browserName() {
  const brands = (navigator.userAgentData?.brands || []).map(item => item.brand).join(" ");
  if (navigator.brave) return "Brave";
  return /Edge/i.test(brands) || navigator.userAgent.includes("Edg/") ? "Edge" : "Chrome";
}

function decodePathPart(value) {
  try { return decodeURIComponent(value); } catch { return value; }
}

function creatorFromTwitchUrl(rawUrl) {
  try {
    const url = new URL(rawUrl);
    if (!/(^|\.)twitch\.tv$/i.test(url.hostname)) return null;
    const first = url.pathname.split("/").filter(Boolean)[0];
    if (!first) return null;
    const reserved = new Set([
      "bits", "browse", "communities", "directory", "downloads", "drops", "following",
      "friends", "inventory", "jobs", "login", "messages", "moderator", "p", "prime",
      "products", "search", "settings", "signup", "store", "subscriptions", "team",
      "turbo", "u", "videos", "wallet"
    ]);
    return reserved.has(first.toLowerCase()) ? null : decodePathPart(first).slice(0, 200);
  } catch {
    return null;
  }
}

function creatorFromYouTubeUrl(rawUrl) {
  try {
    const url = new URL(rawUrl);
    if (!/(^|\.)youtube\.com$/i.test(url.hostname)) return null;
    const parts = url.pathname.split("/").filter(Boolean).map(decodePathPart);
    if (parts[0]?.startsWith("@")) return parts[0].slice(0, 200);
    if (["channel", "c", "user"].includes(parts[0]) && parts[1]) return parts[1].slice(0, 200);
  } catch { /* The URL is validated separately before publishing. */ }
  return null;
}

async function creatorNameForTab(tab) {
  if (!tab?.url) return null;
  const twitchCreator = creatorFromTwitchUrl(tab.url);
  if (twitchCreator) return twitchCreator;

  const youtubeFallback = creatorFromYouTubeUrl(tab.url);
  if (!/(^|\.)youtube\.com$/i.test(new URL(tab.url).hostname)) return youtubeFallback;
  try {
    const response = await chrome.tabs.sendMessage(tab.id, { type: "get-creator-info" });
    return response?.creatorName || youtubeFallback;
  } catch {
    // An already-open tab may not have the content script until it is reloaded.
    return youtubeFallback;
  }
}

async function publishActiveTab() {
  const generation = ++publishGeneration;
  const { bridgeToken = "", bridgePaused = false } = await chrome.storage.local.get(["bridgeToken", "bridgePaused"]);
  if (!bridgeToken) return { ok: false, message: "連携トークンが未設定です。" };

  if (!await authenticateDesktop(bridgeToken)) {
    const result = { ok: false, message: "デスクトップアプリを安全に認証できません。" };
    await chrome.storage.local.set({ bridgeStatus: result.message, bridgeStatusAt: Date.now() });
    await chrome.action.setBadgeText({ text: "!" });
    await chrome.action.setBadgeBackgroundColor({ color: "#C33B54" });
    return result;
  }

  const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
  const isWebTab = Boolean(!bridgePaused && tab && !tab.incognito && tab.url && /^https?:\/\//i.test(tab.url));
  const creatorName = isWebTab ? await creatorNameForTab(tab) : null;
  if (generation !== publishGeneration)
    return { ok: true, message: "より新しいタブ情報を送信中です。" };

  try {
    const response = await sendNative({
      type: "activeTab",
      token: bridgeToken,
      payload: {
        browser: browserName(),
        url: isWebTab ? tab.url : null,
        title: isWebTab ? (tab.title || "") : "",
        creatorName,
        extensionVersion: chrome.runtime.getManifest().version,
        observedAtUnixMs: Date.now(),
        incognito: Boolean(tab?.incognito),
        active: isWebTab
      }
    });
    if (generation !== publishGeneration)
      return { ok: true, message: "より新しいタブ情報を送信しました。" };
    const result = response?.ok
      ? { ok: true, message: bridgePaused ? "一時停止中（サイト情報は記録しません）" : isWebTab ? "接続済み: サイト情報を送信しました" : "接続済み: Webタブなし（記録なし）" }
      : { ok: false, message: response?.error === "unauthorized" ? "トークンが一致しません。" : response?.message || "Native Messaging送信エラー" };
    await chrome.storage.local.set({ bridgeStatus: result.message, bridgeStatusAt: Date.now() });
    await chrome.action.setBadgeText({ text: result.ok ? (bridgePaused ? "Ⅱ" : "") : "!" });
    await chrome.action.setBadgeBackgroundColor({ color: "#C33B54" });
    return result;
  } catch (error) {
    if (generation !== publishGeneration)
      return { ok: true, message: "より新しいタブ情報を送信しました。" };
    // The desktop app may be closed. No retry queue is kept for privacy.
    const result = { ok: false, message: "デスクトップアプリへ接続できません。" };
    await chrome.storage.local.set({ bridgeStatus: result.message, bridgeStatusAt: Date.now() });
    await chrome.action.setBadgeText({ text: "!" });
    await chrome.action.setBadgeBackgroundColor({ color: "#C33B54" });
    return result;
  }
}

function ensureHeartbeat() {
  chrome.alarms.create("active-tab-heartbeat", { when: Date.now() + 1000, periodInMinutes: 0.5 });
}

chrome.runtime.onInstalled.addListener(() => {
  restrictLocalStorageAccess();
  chrome.runtime.openOptionsPage();
  ensureHeartbeat();
});
chrome.runtime.onStartup.addListener(() => { ensureHeartbeat(); publishActiveTab(); });
chrome.alarms.onAlarm.addListener(alarm => { if (alarm.name === "active-tab-heartbeat") publishActiveTab(); });
chrome.tabs.onActivated.addListener(publishActiveTab);
chrome.tabs.onUpdated.addListener((_tabId, changeInfo, tab) => {
  if (tab.active && (changeInfo.url || changeInfo.title || changeInfo.status === "complete")) publishActiveTab();
});
chrome.windows.onFocusChanged.addListener(windowId => { if (windowId !== chrome.windows.WINDOW_ID_NONE) publishActiveTab(); });
chrome.storage.onChanged.addListener((changes, area) => {
  if (area === "local" && (changes.bridgeToken || changes.bridgePaused)) publishActiveTab();
});
chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message?.type === "publish-active-tab") {
    publishActiveTab().then(sendResponse);
    return true;
  }
  if (message?.type === "get-bridge-status") {
    chrome.storage.local.get(["bridgeStatus", "bridgeStatusAt", "bridgePaused"]).then(sendResponse);
    return true;
  }
  if (message?.type === "creator-info-changed" && sender.tab?.active) {
    publishActiveTab();
  }
  return false;
});

ensureHeartbeat();
restrictLocalStorageAccess();
publishActiveTab();
