namespace PCActivityTracker.Domain;

/// <summary>
/// User-owned metadata layered over automatically captured sessions. Raw activity
/// rows remain immutable so every correction can be removed safely.
/// </summary>
public sealed class ActivityAnnotation
{
    public long Id { get; set; }
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset EndTime { get; set; }
    public string ProjectName { get; set; } = string.Empty;
    public string TaskName { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public string CategoryOverride { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
