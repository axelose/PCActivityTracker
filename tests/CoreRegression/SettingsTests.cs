using PCActivityTracker.Domain;
using PCActivityTracker.Services;
using PCActivityTracker.Tracking;

namespace CoreRegression;
internal static class SettingsTests
{
    internal static void Run()
    {
        using var fixture = new Fixture();
        var now = DateTimeOffset.UtcNow;
        var configuration = fixture.Settings.Current;
        configuration.RecordCreatorNames = false;
        configuration.RecordingEnabled = true;
        fixture.Settings.Save(configuration);
        var snapshot = new ForegroundSnapshot(now, 0, "Chrome", "chrome", "Title", "chrome.exe", false,
            new BrowserActivity("Chrome", "https://youtube.com/watch", "youtube.com", "Title", "Private Creator", now, false,
                "youtube:channel:UC" + new string('a', 22)));
        using var sessionLock = new SessionLockMonitor();
        var tracker = new ActivityTrackerService(new ForegroundActivityProvider(new IdleDetector(), new BrowserStateStore()),
            fixture.Activities, fixture.Settings, sessionLock);
        // Invoke the tracker using synthetic snapshots only: never start its polling loop.
        tracker.ProcessSnapshot(snapshot, configuration);
        tracker.ProcessSnapshot(snapshot with { ObservedAt = now.AddSeconds(10) }, configuration);
        tracker.DisposeAsync().AsTask().GetAwaiter().GetResult();
        var saved = fixture.Activities.GetRange(now.AddSeconds(-1), now.AddMinutes(1));
        Check.That(saved.Count > 0 && saved.All(item => item.CreatorName is null && item.CreatorId is null),
            "Disabling creator recording leaked a name or identifier.");
    }
}
