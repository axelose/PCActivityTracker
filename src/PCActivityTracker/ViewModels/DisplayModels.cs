namespace PCActivityTracker.ViewModels;

public sealed record TimelineItem(string TimeRange, string Title, string Subtitle, string Duration, bool IsIdle, bool IsWebsite);
public sealed record RankingItem(int Rank, string Name, string Duration, double BarWidth, string? Identity = null)
{
    public string FilterValue => Identity ?? Name;
}
public sealed record DailyItem(string Day, string Duration, string IdleDuration, double BarWidth);
public sealed record ExclusionItem(long Id, string Type, string Value);
public sealed record HeatmapDayItem(string Day, IReadOnlyList<HeatmapCellItem> Hours);
public sealed record HeatmapCellItem(int Hour, double Opacity, string ToolTip);
public sealed record BackupItem(string Name, string Created, string Size, string FullPath, bool IsAutomatic);
public sealed record AnalysisMetricItem(string Name, string Value);
public sealed record ComparisonItem(string Scope, string Name, string Previous, string Current, string Change, bool IsIncrease);
public sealed record CategoryRuleItem(long Id, string Target, string Pattern, string Category,
    string MatchMode, string Color, int Priority);
public sealed record ExplorerTimelineItem(long AnnotationId, DateTimeOffset Start, DateTimeOffset End,
    DateTimeOffset? AnnotationStart, DateTimeOffset? AnnotationEnd, string TimeRange, string Title,
    string Subtitle, string Duration, string Category, string CategoryReason, string Project,
    string Task, string Note, bool IsIdle, bool IsWebsite, bool IsFocus, bool IsSwitch, int ActivityCount);
