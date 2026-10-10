using PCActivityTracker.Domain;
using PCActivityTracker.Services;
using PCActivityTracker.Tracking;

namespace CoreRegression;
internal static class BrowserStateTests
{
    internal static void Run()
    {
        var id = "youtube:channel:UC" + new string('a', 22);
        Check.That(CreatorIdentityService.Normalize(id, "www.youtube.com") == id, "Valid channel ID rejected.");
        Check.That(CreatorIdentityService.Normalize(id, "twitch.tv") is null, "Cross-site identifier accepted.");
        Check.That(CreatorIdentityService.Normalize("youtube:handle:@Foo", "youtube.com") == "youtube:handle:@foo", "Handle normalization failed.");
        Check.That(CreatorIdentityService.Normalize("youtube:handle:@bad/path", "youtube.com") is null, "Unsafe handle accepted.");
        Check.That(CreatorIdentityService.Normalize("youtube:handle:@" + '\uD800', "youtube.com") is null, "Invalid Unicode identifier not rejected.");
        Check.That(CreatorIdentityService.Normalize("twitch:login:Streamer", "www.twitch.tv") == "twitch:login:streamer", "Twitch login normalization failed.");
        var now = DateTimeOffset.UtcNow;
        var store = new BrowserStateStore();
        store.Update(new BrowserActivity("Chrome", "https://youtube.com/watch", "youtube.com", "New Video", "Name", now, false, id));
        store.Update(new BrowserActivity("Chrome", "https://example.com", "example.com", "Old Video", null, now.AddSeconds(-1), false));
        Check.That(store.GetFresh("chrome", "New Video - Google Chrome", now)?.CreatorId == id, "Stale event replaced current identity.");
        Check.That(store.GetFresh("chrome", "Different Window", now) is null, "Unmatched browser window accepted.");
        Check.That(store.GetFresh("chrome", "New Video", now.AddSeconds(91)) is null, "Expired browser information accepted.");
        store.Clear("Chrome", now.AddSeconds(1));
        store.Update(new BrowserActivity("Chrome", "https://example.com", "example.com", "Old Video", null, now, false));
        Check.That(store.GetFresh("chrome", "Old Video", now.AddSeconds(2)) is null, "Old page survived a clear event.");
    }
}
