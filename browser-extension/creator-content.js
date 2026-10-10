function cleanCreatorName(value) {
  if (!value) return null;
  const normalized = value.replace(/\s+/g, " ").trim();
  return normalized ? normalized.slice(0, 200) : null;
}

function creatorFromChannelUrl() {
  const parts = location.pathname.split("/").filter(Boolean);
  const decodePart = value => {
    try { return decodeURIComponent(value); } catch { return value; }
  };
  if (parts[0]?.startsWith("@")) return cleanCreatorName(decodePart(parts[0]));
  if (["channel", "c", "user"].includes(parts[0]) && parts[1])
    return cleanCreatorName(decodePart(parts[1]));
  return null;
}

function findYouTubeCreator() {
  const selectors = [
    "ytd-video-owner-renderer #channel-name a",
    "ytd-video-owner-renderer ytd-channel-name a",
    "#owner #channel-name a",
    "ytd-reel-player-overlay-renderer #channel-name a",
    "ytd-reel-player-overlay-renderer ytd-channel-name a",
    "yt-page-header-renderer h1",
    "ytd-c4-tabbed-header-renderer #channel-name"
  ];
  for (const selector of selectors) {
    const element = document.querySelector(selector);
    const name = cleanCreatorName(element?.textContent || element?.getAttribute("aria-label"));
    if (name) return name;
  }

  return cleanCreatorName(document.querySelector('meta[itemprop="author"]')?.content) || creatorFromChannelUrl();
}

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message?.type !== "get-creator-info") return false;
  sendResponse({ creatorName: findYouTubeCreator() });
  return false;
});

let publishTimer;
function notifyCreatorChanged() {
  clearTimeout(publishTimer);
  publishTimer = setTimeout(() => {
    chrome.runtime.sendMessage({
      type: "creator-info-changed",
      creatorName: findYouTubeCreator(),
      url: location.href
    }).catch(() => { /* The extension may be reloading. */ });
  }, 500);
}

document.addEventListener("yt-navigate-finish", notifyCreatorChanged);
document.addEventListener("visibilitychange", () => {
  if (!document.hidden) notifyCreatorChanged();
});
notifyCreatorChanged();
