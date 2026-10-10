namespace PCActivityTracker.Domain;

public sealed record BrowserActivity(
    string Browser,
    string Url,
    string Domain,
    string PageTitle,
    string? CreatorName,
    DateTimeOffset ObservedAt,
    bool Incognito);

public sealed record ForegroundSnapshot(
    DateTimeOffset ObservedAt,
    uint ProcessId,
    string AppName,
    string ProcessName,
    string WindowTitle,
    string ExecutableName,
    bool IsIdle,
    BrowserActivity? BrowserActivity);
