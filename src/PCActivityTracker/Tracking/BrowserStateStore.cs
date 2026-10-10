using PCActivityTracker.Domain;

namespace PCActivityTracker.Tracking;

public sealed class BrowserStateStore
{
    private readonly object _gate = new();
    private BrowserActivity? _latest;
    private readonly Dictionary<string, BrowserActivity> _byBrowser = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _clearedAt = new(StringComparer.OrdinalIgnoreCase);

    public void Update(BrowserActivity activity)
    {
        lock (_gate)
        {
            if (_clearedAt.TryGetValue(activity.Browser, out var clearedAt) && clearedAt > activity.ObservedAt)
                return;
            if (_byBrowser.TryGetValue(activity.Browser, out var current) && current.ObservedAt > activity.ObservedAt)
                return;
            _latest = activity;
            _byBrowser[activity.Browser] = activity;
            _clearedAt.Remove(activity.Browser);
        }
    }

    public void Clear(string browser, DateTimeOffset observedAt)
    {
        lock (_gate)
        {
            if (_clearedAt.TryGetValue(browser, out var existingClear) && existingClear > observedAt)
                return;
            if (_byBrowser.TryGetValue(browser, out var current) && current.ObservedAt > observedAt)
                return;
            _byBrowser.Remove(browser);
            _clearedAt[browser] = observedAt;
            if (_latest?.Browser.Equals(browser, StringComparison.OrdinalIgnoreCase) == true &&
                _latest.ObservedAt <= observedAt)
                _latest = null;
        }
    }

    public BrowserActivity? GetFresh(string processName, string windowTitle, DateTimeOffset now)
    {
        lock (_gate)
        {
            var expected = processName.Equals("msedge", StringComparison.OrdinalIgnoreCase) ? "Edge" :
                processName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ? "Chrome" :
                processName.Equals("brave", StringComparison.OrdinalIgnoreCase) ? "Brave" : string.Empty;
            if (expected.Length == 0) return null;

            if (_clearedAt.TryGetValue(expected, out var clearedAt) && now - clearedAt <= TimeSpan.FromSeconds(90))
                return null;
            if (_byBrowser.TryGetValue(expected, out var exact) && IsFresh(exact, now) && TitleMatches(exact, windowTitle))
                return exact;
            if (_latest is null || !IsFresh(_latest, now)) return null;

            // Edge extension service workers can expose a Chromium-only user agent.
            // Accept that state only when its page title matches the foreground browser window.
            if (TitleMatches(_latest, windowTitle))
                return _latest with { Browser = expected };

            return null;
        }
    }

    private static bool IsFresh(BrowserActivity activity, DateTimeOffset now) =>
        now - activity.ObservedAt <= TimeSpan.FromSeconds(90);

    private static bool TitleMatches(BrowserActivity activity, string windowTitle) =>
        !string.IsNullOrWhiteSpace(activity.PageTitle) && !string.IsNullOrWhiteSpace(windowTitle) &&
        windowTitle.Contains(activity.PageTitle, StringComparison.OrdinalIgnoreCase);
}
