function cleanCreatorName(value) {
  if (typeof value !== "string") return null;
  const normalized = value.replace(/\s+/g, " ").trim();
  return normalized ? normalized.slice(0, 200) : null;
}

function findYouTubeCreator() {
  const url = new URL(location.href);
  const fallback = CreatorIdentity.fromUrl(location.href);
  const shorts = url.pathname.startsWith("/shorts/");
  const watch = url.pathname === "/watch";
  if (!shorts && !watch) return fallback;

  const watchPage = document.querySelector("ytd-watch-flexy[video-id]");
  if (watch && watchPage && watchPage.getAttribute("video-id") !== url.searchParams.get("v")) return null;
  const selectors = shorts ? [
    "ytd-reel-video-renderer[is-active] #channel-name a",
    "ytd-reel-video-renderer[is-active] ytd-channel-name a"
  ] : [
    "ytd-video-owner-renderer #channel-name a",
    "ytd-video-owner-renderer ytd-channel-name a",
    "#owner #channel-name a"
  ];
  for (const selector of selectors) {
    const element = document.querySelector(selector);
    const name = cleanCreatorName(element?.textContent || element?.getAttribute("aria-label"));
    let identity = null;
    try {
      if (element?.getAttribute("href"))
        identity = CreatorIdentity.fromUrl(new URL(element.getAttribute("href"), location.href).href);
    } catch { /* Unexpected DOM link is not a creator identifier. */ }
    if (name && identity) return { ...identity, creatorName: name };
  }
  return fallback;
}

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message?.type !== "get-creator-info") return false;
  sendResponse({ ...findYouTubeCreator(), pageUrl: location.href });
  return false;
});

let publishTimer;
function notifyCreatorChanged() {
  clearTimeout(publishTimer);
  if (document.hidden) return;
  publishTimer = setTimeout(() => {
    // Worker re-reads the active tab; no queued page URL or name is retained.
    chrome.runtime.sendMessage({ type: "creator-info-changed" })
      .catch(() => { /* The extension may be reloading. */ });
  }, 500);
}

document.addEventListener("yt-navigate-finish", notifyCreatorChanged);
document.addEventListener("visibilitychange", notifyCreatorChanged);
notifyCreatorChanged();
