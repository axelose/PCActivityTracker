namespace PCActivityTracker.Domain;

public sealed class ActivitySession
{
    public long Id { get; set; }
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset EndTime { get; set; }
    public long DurationSeconds => Math.Max(0, (long)(EndTime - StartTime).TotalSeconds);
    public ActivityType ActivityType { get; set; }
    public string AppName { get; set; } = "Unknown";
    public string ProcessName { get; set; } = string.Empty;
    public string? WindowTitle { get; set; }
    public string? ExecutableName { get; set; }
    public string? BrowserName { get; set; }
    public string? Url { get; set; }
    public string? Domain { get; set; }
    public string? PageTitle { get; set; }
    public string? CreatorName { get; set; }
    public string? CreatorId { get; set; }
    public bool IsIdle { get; set; }
    public string Category { get; set; } = "Other";

    public bool IsSameActivity(ActivitySession other) =>
        ActivityType == other.ActivityType &&
        IsIdle == other.IsIdle &&
        string.Equals(AppName, other.AppName, StringComparison.Ordinal) &&
        string.Equals(ProcessName, other.ProcessName, StringComparison.Ordinal) &&
        string.Equals(WindowTitle, other.WindowTitle, StringComparison.Ordinal) &&
        string.Equals(BrowserName, other.BrowserName, StringComparison.Ordinal) &&
        string.Equals(Url, other.Url, StringComparison.Ordinal) &&
        string.Equals(Domain, other.Domain, StringComparison.Ordinal) &&
        string.Equals(PageTitle, other.PageTitle, StringComparison.Ordinal) &&
        string.Equals(CreatorName, other.CreatorName, StringComparison.Ordinal) &&
        string.Equals(CreatorId, other.CreatorId, StringComparison.Ordinal) &&
        string.Equals(Category, other.Category, StringComparison.Ordinal);
}

public enum ActivityType
{
    Application,
    Website,
    Idle
}
