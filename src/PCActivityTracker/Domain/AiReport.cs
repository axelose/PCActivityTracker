namespace PCActivityTracker.Domain;

public enum AiReportType
{
    Daily,
    Weekly
}

public sealed record AiReport(
    long Id,
    AiReportType Type,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    DateTimeOffset GeneratedAt,
    string ModelName,
    string Content,
    int PromptVersion);
