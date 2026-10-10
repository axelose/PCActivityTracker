// Pure local URL parsing; no API calls or privileged page access.
const CreatorIdentity = (() => {
  const reserved = new Set(["bits","browse","communities","directory","downloads","drops","following","friends","inventory","jobs","login","messages","moderator","p","prime","products","search","settings","signup","store","subscriptions","team","turbo","u","videos","wallet","clip","clips"]);
  function fromUrl(rawUrl) {
    try {
      const url = new URL(rawUrl);
      if (!/^https?:$/.test(url.protocol)) return null;
      const parts = url.pathname.split("/").filter(Boolean).map(decodeURIComponent);
      if (["twitch.tv", "www.twitch.tv", "m.twitch.tv"].includes(url.hostname.toLowerCase())) {
        const login = parts[0]?.toLowerCase();
        if (login && /^[a-z0-9_]{1,25}$/.test(login) && !reserved.has(login))
          return { creatorName: login, creatorId: "twitch:login:" + login };
      }
      if (["youtube.com", "www.youtube.com", "m.youtube.com"].includes(url.hostname.toLowerCase())) {
        if (parts[0] === "channel" && /^UC[A-Za-z0-9_-]{22}$/.test(parts[1] || ""))
          return { creatorName: parts[1], creatorId: "youtube:channel:" + parts[1] };
        if (parts[0]?.startsWith("@") && parts[0].length > 1 && parts[0].length <= 101 && !/[\s/\x00-\x1f]/u.test(parts[0]))
          return { creatorName: parts[0], creatorId: "youtube:handle:" + parts[0].normalize("NFC").toLowerCase() };
        if (["c", "user"].includes(parts[0]) && parts[1]) return { creatorName: parts[1].slice(0, 200), creatorId: null };
      }
    } catch { /* Malformed or unsupported URLs have no inferred creator. */ }
    return null;
  }
  return { fromUrl };
})();
