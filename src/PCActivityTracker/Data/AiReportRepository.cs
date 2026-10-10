using PCActivityTracker.Domain;

namespace PCActivityTracker.Data;

public sealed class AiReportRepository
{
    private readonly SqliteDatabase _database;
    public AiReportRepository(SqliteDatabase database) => _database = database;

    public AiReport? Get(AiReportType type, DateTimeOffset periodStart)
    {
        var row = _database.Query("""
            SELECT id, report_type, period_start_utc_ms, period_end_utc_ms,
                   generated_utc_ms, model_name, content, prompt_version
            FROM ai_reports
            WHERE report_type=?1 AND period_start_utc_ms=?2
            LIMIT 1;
            """, type.ToString(), periodStart).SingleOrDefault();
        return row is null ? null : Map(row);
    }

    public AiReport Save(AiReportType type, DateTimeOffset start, DateTimeOffset end,
        string modelName, string content, int promptVersion)
    {
        var generatedAt = DateTimeOffset.UtcNow;
        _database.Execute("""
            INSERT INTO ai_reports (
                report_type, period_start_utc_ms, period_end_utc_ms, generated_utc_ms,
                model_name, content, prompt_version)
            VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)
            ON CONFLICT(report_type, period_start_utc_ms) DO UPDATE SET
                period_end_utc_ms=excluded.period_end_utc_ms,
                generated_utc_ms=excluded.generated_utc_ms,
                model_name=excluded.model_name,
                content=excluded.content,
                prompt_version=excluded.prompt_version;
            """, type.ToString(), start, end, generatedAt, modelName, content, promptVersion);
        return Get(type, start) ?? throw new InvalidOperationException("AI report could not be saved.");
    }

    private static AiReport Map(SqliteRow row)
    {
        _ = Enum.TryParse<AiReportType>(row.String("report_type"), out var type);
        return new AiReport(
            row.Int64("id"), type,
            DateTimeOffset.FromUnixTimeMilliseconds(row.Int64("period_start_utc_ms")),
            DateTimeOffset.FromUnixTimeMilliseconds(row.Int64("period_end_utc_ms")),
            DateTimeOffset.FromUnixTimeMilliseconds(row.Int64("generated_utc_ms")),
            row.String("model_name") ?? string.Empty,
            row.String("content") ?? string.Empty,
            (int)row.Int64("prompt_version"));
    }
}
