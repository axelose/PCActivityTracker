importScripts("creator-identity.js");
const NATIVE_HOST = "jp.local.pcactivitytracker.browser";
let publishGeneration = 0;
let nativePort = null;
let nativeRequestQueue = Promise.resolve();
const NATIVE_RESPONSE_TIMEOUT_MS = 5000;
const CREATOR_RESPONSE_TIMEOUT_MS = 1000;
let publishPending = false;
let publishWork = null;

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
    let finished = false;
    let timer;
    const finish = (error, response) => {
      if (finished) return;
      finished = true;
      clearTimeout(timer);
      port.onMessage.removeListener(onMessage);
      port.onDisconnect.removeListener(onDisconnect);
      if (error) {
        // Discard this port: a late response must never answer the next request.
        if (nativePort === port) nativePort = null;
        try { port.disconnect(); } catch { /* Already disconnected. */ }
        reject(error);
      } else resolve(response);
    };
    const onMessage = response => {
      finish(null, response);
    };
    const onDisconnect = () => {
      finish(new Error(chrome.runtime.lastError?.message || "Native Messaging host disconnected."));
    };
    port.onMessage.addListener(onMessage);
    port.onDisconnect.addListener(onDisconnect);
    timer = setTimeout(() => finish(new Error("Native Messaging response timed out.")), NATIVE_RESPONSE_TIMEOUT_MS);
    try { port.postMessage(message); }
    catch (error) { finish(error); }
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


async function creatorNameForTab(tab) {
  if (!tab?.url) return null;
  const fallback = CreatorIdentity.fromUrl(tab.url);
  if (!["youtube.com", "www.youtube.com", "m.youtube.com"].includes(new URL(tab.url).hostname.toLowerCase())) return fallback;
  try {
    const response = await withTimeout(chrome.tabs.sendMessage(tab.id, { type: "get-creator-info" }), CREATOR_RESPONSE_TIMEOUT_MS);
    return response?.pageUrl === tab.url && response?.creatorName ? response : fallback;
  } catch {
    // An already-open tab may not have the content script until it is reloaded.
    return fallback;
  }
}

function withTimeout(work, milliseconds) {
  let timer;
  return Promise.race([work, new Promise((_, reject) => {
    timer = setTimeout(() => reject(new Error("Content script response timed out.")), milliseconds);
  })]).finally(() => clearTimeout(timer));
}

function publishActiveTab() {
  ++publishGeneration;
  publishPending = true;
  // Only one sender and one pending refresh. No URLs/titles are queued or persisted.
  if (!publishWork) publishWork = (async () => {
    let result;
    do {
      publishPending = false;
      try { result = await publishLatestTab(publishGeneration); }
      catch { result = { ok: false, message: "ブラウザ情報を取得できません。" }; }
    } while (publishPending);
    return result;
  })().finally(() => { publishWork = null; });
  return publishWork;
}

async function publishLatestTab(generation) {
  const { bridgeToken = "", bridgePaused = false } = await chrome.storage.local.get(["bridgeToken", "bridgePaused"]);
  if (!bridgeToken) return { ok: false, message: "連携トークンが未設定です。" };

  if (!await authenticateDesktop(bridgeToken)) {
    if (generation !== publishGeneration) return { ok: false, message: "最新状態を確認中です。" };
    const result = { ok: false, message: "デスクトップアプリを安全に認証できません。" };
    await chrome.storage.local.set({ bridgeStatus: result.message, bridgeStatusAt: Date.now() });
    await chrome.action.setBadgeText({ text: "!" });
    await chrome.action.setBadgeBackgroundColor({ color: "#C33B54" });
    return result;
  }

  const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
  const isWebTab = Boolean(!bridgePaused && tab && !tab.incognito && tab.url && /^https?:\/\//i.test(tab.url));
  const creator = isWebTab ? await creatorNameForTab(tab) : null;
  if (generation !== publishGeneration)
    return { ok: true, message: "より新しいタブ情報を送信中です。" };

  // Revalidate after asynchronous DOM lookup, including pause/token revocation.
  const [currentTab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
  const currentSettings = await chrome.storage.local.get(["bridgeToken", "bridgePaused"]);
  if (generation !== publishGeneration || currentTab?.id !== tab?.id || currentTab?.url !== tab?.url ||
      Boolean(currentTab?.incognito) !== Boolean(tab?.incognito) ||
      (currentSettings.bridgeToken || "") !== bridgeToken || Boolean(currentSettings.bridgePaused) !== Boolean(bridgePaused)) {
    publishPending = true;
    return { ok: true, message: "最新のタブ状態を確認中です。" };
  }

  try {
    const response = await sendNative({
      type: "activeTab",
      token: bridgeToken,
      payload: {
        browser: browserName(),
        url: isWebTab ? tab.url : null,
        title: isWebTab ? (tab.title || "") : "",
        creatorName: creator?.creatorName || null,
        creatorId: creator?.creatorId || null,
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
