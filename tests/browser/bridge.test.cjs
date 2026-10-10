const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const crypto = require("node:crypto");
const extension = path.resolve(__dirname, "../../browser-extension");
const tick = () => new Promise(resolve => setImmediate(resolve));
class Event {
  listeners = new Set();
  addListener = listener => this.listeners.add(listener);
  removeListener = listener => this.listeners.delete(listener);
  emit = (...args) => { for (const listener of [...this.listeners]) listener(...args); };
}

async function bridge() {
  const state = {
    settings: { bridgeToken: "", bridgePaused: false }, messages: [], writes: [], ports: [],
    tab: { id: 1, active: true, incognito: false, url: "https://example.com/", title: "Example" },
    mode: "ok", content: async () => null
  };
  const chrome = {
    runtime: {
      onInstalled: new Event(), onStartup: new Event(), onMessage: new Event(),
      getManifest: () => ({ version: "3.2.4" }), openOptionsPage() {},
      connectNative() {
        const port = { onMessage: new Event(), onDisconnect: new Event(), disconnected: false };
        port.disconnect = () => { port.disconnected = true; port.onDisconnect.emit(); };
        port.postMessage = message => {
          if (state.mode === "throw") throw new Error("closed");
          state.messages.push(message);
          if (state.mode === "never") return;
          queueMicrotask(() => {
            if (state.mode === "disconnect") return port.disconnect();
            const response = message.type === "challenge"
              ? { proof: crypto.createHmac("sha256", state.settings.bridgeToken).update(message.nonce).digest("base64url") }
              : { ok: true };
            port.onMessage.emit(response);
          });
        };
        state.ports.push(port);
        return port;
      }
    },
    storage: {
      local: {
        get: async () => ({ ...state.settings }), setAccessLevel: async () => {},
        set: async data => { state.writes.push(data); }
      }, onChanged: new Event()
    },
    tabs: { query: async options => {
        assert.equal(options.active, true);
        assert.equal(options.lastFocusedWindow, true);
        return state.tab ? [{ ...state.tab }] : [];
      }, sendMessage: (...args) => state.content(...args),
      onActivated: new Event(), onUpdated: new Event() },
    windows: { WINDOW_ID_NONE: -1, onFocusChanged: new Event() },
    alarms: { create() {}, onAlarm: new Event() },
    action: { setBadgeText: async () => {}, setBadgeBackgroundColor: async () => {} }
  };
  const context = vm.createContext({
    chrome, crypto: crypto.webcrypto, TextEncoder, Uint8Array, URL,
    btoa: text => Buffer.from(text, "binary").toString("base64"),
    navigator: { userAgent: "Chrome" }, clearTimeout,
    // Exercise the production deadlines without waiting five seconds per test.
    setTimeout: (callback, milliseconds) => setTimeout(callback, milliseconds >= 1000 ? 35 : milliseconds)
  });
  context.importScripts = file => vm.runInContext(fs.readFileSync(path.join(extension, file), "utf8"), context);
  vm.runInContext(fs.readFileSync(path.join(extension, "background.js"), "utf8"), context);
  await tick(); await tick();
  state.settings.bridgeToken = "test-only-pairing-token";
  return { state, chrome, evaluate: code => vm.runInContext(code, context) };
}

test("native timeout resets the port and allows the next request", async () => {
  const { state, evaluate } = await bridge();
  state.mode = "never";
  await assert.rejects(evaluate('sendNative({type:"probe"})'), /timed out/);
  assert.equal(state.ports[0].disconnected, true);
  assert.equal(state.ports[0].onMessage.listeners.size, 0);
  state.mode = "ok";
  assert.equal((await evaluate('sendNative({type:"probe"})')).ok, true);
  assert.equal(state.ports.length, 2);
});

for (const mode of ["disconnect", "throw"]) test("native " + mode + " releases queue", async () => {
  const { state, evaluate } = await bridge();
  state.mode = mode;
  await assert.rejects(evaluate('sendNative({type:"probe"})'));
  state.mode = "ok";
  assert.equal((await evaluate('sendNative({type:"probe"})')).ok, true);
});

test("rapid changes coalesce into the latest tab instead of queuing history", async () => {
  const { state, evaluate } = await bridge();
  const work = evaluate("publishActiveTab()");
  for (let id = 2; id < 100; id++) {
    state.tab = { ...state.tab, id, url: "https://example.com/" + id };
    evaluate("publishActiveTab()");
  }
  await work;
  const activities = state.messages.filter(message => message.type === "activeTab");
  assert.equal(activities.length, 1);
  assert.equal(activities[0].payload.url, "https://example.com/99");
  assert.equal(state.writes.some(data => "url" in data || "title" in data || "creatorName" in data), false);
});

for (const kind of ["incognito", "paused", "internal", "no-tab"]) test(kind + " sends no page fields", async () => {
  const { state, evaluate } = await bridge();
  if (kind === "incognito") state.tab.incognito = true;
  if (kind === "paused") state.settings.bridgePaused = true;
  if (kind === "internal") state.tab.url = "chrome://extensions";
  if (kind === "no-tab") state.tab = null;
  await evaluate("publishActiveTab()");
  const payload = state.messages.find(message => message.type === "activeTab").payload;
  assert.equal(payload.active, false);
  assert.equal(payload.url, null);
  assert.equal(payload.title, "");
  assert.equal(payload.creatorName, null);
  assert.equal(payload.creatorId, null);
});

test("lost content-script response has a deadline and channel URL fallback", async () => {
  const { state, evaluate } = await bridge();
  state.tab.url = "https://www.youtube.com/@Example";
  state.content = () => new Promise(() => {});
  await evaluate("publishActiveTab()");
  const payload = state.messages.find(message => message.type === "activeTab").payload;
  assert.equal(payload.creatorId, "youtube:handle:@example");
});

test("tab switch during creator lookup cannot publish the previous page", async () => {
  const { state, evaluate } = await bridge();
  state.tab.url = "https://www.youtube.com/watch?v=old";
  let resolveContent;
  state.content = () => new Promise(resolve => { resolveContent = resolve; });
  const work = evaluate("publishActiveTab()");
  for (let i = 0; !resolveContent && i < 20; i++) await tick();
  assert.ok(resolveContent);
  state.tab = { id: 2, active: true, incognito: false, url: "https://www.twitch.tv/new_streamer", title: "New" };
  evaluate("publishActiveTab()");
  resolveContent({ creatorName: "Old", pageUrl: "https://www.youtube.com/watch?v=old" });
  await work;
  const activities = state.messages.filter(message => message.type === "activeTab");
  assert.equal(activities.length, 1);
  assert.equal(activities[0].payload.creatorId, "twitch:login:new_streamer");
});

test("pause during content lookup revalidates before sending", async () => {
  const { state, evaluate } = await bridge();
  state.tab.url = "https://www.youtube.com/watch?v=test";
  let resolveContent;
  state.content = () => new Promise(resolve => { resolveContent = resolve; });
  const work = evaluate("publishActiveTab()");
  for (let i = 0; !resolveContent && i < 20; i++) await tick();
  state.settings.bridgePaused = true;
  resolveContent({ creatorName: "Example", pageUrl: state.tab.url });
  await work;
  const activities = state.messages.filter(message => message.type === "activeTab");
  assert.equal(activities.length, 1);
  assert.equal(activities[0].payload.url, null);
});

test("app restart is retried only with current tab on the next heartbeat", async () => {
  const { state, evaluate } = await bridge();
  state.mode = "disconnect";
  assert.equal((await evaluate("publishActiveTab()")).ok, false);
  state.tab.url = "https://example.com/new";
  state.mode = "ok";
  assert.equal((await evaluate("publishActiveTab()")).ok, true);
  assert.equal(state.messages.filter(message => message.type === "activeTab").length, 1);
});

test("stale SPA response cannot attach a creator to another video", async () => {
  const { state, evaluate } = await bridge();
  state.tab.url = "https://www.youtube.com/watch?v=new";
  state.content = async () => ({ creatorName: "Wrong", creatorId: "youtube:handle:@wrong", pageUrl: "https://www.youtube.com/watch?v=old" });
  await evaluate("publishActiveTab()");
  assert.equal(state.messages.find(message => message.type === "activeTab").payload.creatorName, null);
});

test("last-focused window event refreshes only that window's active tab", async () => {
  const { state, chrome, evaluate } = await bridge();
  state.tab = { ...state.tab, id: 20, windowId: 2, url: "https://example.com/window-two" };
  chrome.windows.onFocusChanged.emit(2);
  await evaluate("publishActiveTab()");
  assert.equal(state.messages.filter(message => message.type === "activeTab").at(-1).payload.url,
    "https://example.com/window-two");
});

test("Edge and Brave retain their browser namespace", async () => {
  for (const browser of ["Edge", "Brave"]) {
    const { state, evaluate } = await bridge();
    evaluate(browser === "Edge" ? 'navigator.userAgent = "Edg/100"' : 'navigator.brave = {}');
    await evaluate("publishActiveTab()");
    assert.equal(state.messages.find(message => message.type === "activeTab").payload.browser, browser);
  }
});

function creatorPage(url, elements = {}) {
  const events = {};
  const context = vm.createContext({
    URL, location: { href: url }, clearTimeout, setTimeout: () => 1,
    document: { hidden: false, querySelector: selector => elements[selector] || null, addEventListener: (name, callback) => { events[name] = callback; } },
    chrome: { runtime: { onMessage: new Event(), sendMessage: async () => {} } }
  });
  vm.runInContext(fs.readFileSync(path.join(extension, "creator-identity.js"), "utf8"), context);
  vm.runInContext(fs.readFileSync(path.join(extension, "creator-content.js"), "utf8"), context);
  return code => vm.runInContext(code, context);
}
const element = (text, href) => ({ textContent: text, getAttribute: name => name === "href" ? href : null });
test("YouTube owner anchor records display name separately from stable channel ID", () => {
  const id = "UC" + "a".repeat(22);
  const evaluate = creatorPage("https://www.youtube.com/watch?v=test", {
    "ytd-video-owner-renderer #channel-name a": element("Channel Name", "/channel/" + id)
  });
  assert.equal(evaluate("findYouTubeCreator().creatorId"), "youtube:channel:" + id);
  assert.equal(evaluate("findYouTubeCreator().creatorName"), "Channel Name");
});
test("Shorts uses only the active reel, not a hidden previous creator", () => {
  const evaluate = creatorPage("https://www.youtube.com/shorts/test", {
    "ytd-video-owner-renderer #channel-name a": element("Wrong", "/@wrong"),
    "ytd-reel-video-renderer[is-active] #channel-name a": element("Right", "/@right")
  });
  assert.equal(evaluate("findYouTubeCreator().creatorName"), "Right");
});
test("watch DOM for previous video yields unknown, not stale creator", () => {
  const evaluate = creatorPage("https://www.youtube.com/watch?v=new", {
    "ytd-watch-flexy[video-id]": { getAttribute: () => "old" },
    "ytd-video-owner-renderer #channel-name a": element("Wrong", "/@wrong")
  });
  assert.equal(evaluate("findYouTubeCreator()"), null);
});
test("home page cannot infer a creator from a recommendation or stale owner", () => {
  const evaluate = creatorPage("https://www.youtube.com/", {
    "ytd-video-owner-renderer #channel-name a": element("Wrong", "/@wrong")
  });
  assert.equal(evaluate("findYouTubeCreator()"), null);
});
test("creator URL parsing excludes reserved Twitch routes and lookalike domains", () => {
  const evaluate = creatorPage("https://www.youtube.com/");
  for (const url of ["https://clips.twitch.tv/Someone", "https://www.twitch.tv/directory", "https://www.twitch.tv/videos/123", "https://evil.youtube.com/@someone", "https://youtube.com.evil/@someone", "https://youtube.com/@bad%2Fname", "https://twitch.tv/%ZZ"])
    assert.equal(evaluate("CreatorIdentity.fromUrl(" + JSON.stringify(url) + ")"), null, url);
  assert.equal(evaluate('CreatorIdentity.fromUrl("https://twitch.tv/Streamer/videos").creatorId'), "twitch:login:streamer");
});
