using PCActivityTracker.Domain;
using PCActivityTracker.Services;

namespace PCActivityTracker.Data;

public sealed class ActivityRepository
{
    private readonly SqliteDatabase _database;

    public ActivityRepository(SqliteDatabase database) => _database = database;

    public void Save(ActivitySession session)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (session.Id == 0)
        {
            session.Id = _database.Execute("""
                INSERT INTO activity_sessions (
                    start_utc_ms, end_utc_ms, duration_seconds, activity_type,
                    app_name, process_name, window_title, executable_name,
                    browser_name, url, domain, page_title, creator_name, is_idle, category,
                    created_utc_ms, updated_utc_ms, creator_id)
                VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?16, ?17, ?18);
                """,
                session.StartTime, session.EndTime, session.DurationSeconds, session.ActivityType.ToString(),
                session.AppName, session.ProcessName, session.WindowTitle, session.ExecutableName,
                session.BrowserName, session.Url, session.Domain, session.PageTitle, session.CreatorName,
                session.IsIdle, session.Category, now, now, session.CreatorId);
            return;
        }

        _database.Execute("""
            UPDATE activity_sessions SET
                end_utc_ms=?1, duration_seconds=?2, activity_type=?3,
                app_name=?4, process_name=?5, window_title=?6, executable_name=?7,
                browser_name=?8, url=?9, domain=?10, page_title=?11, creator_name=?12,
                is_idle=?13, category=?14, updated_utc_ms=?15, creator_id=?17
            WHERE id=?16;
            """,
            session.EndTime, session.DurationSeconds, session.ActivityType.ToString(),
            session.AppName, session.ProcessName, session.WindowTitle, session.ExecutableName,
            session.BrowserName, session.Url, session.Domain, session.PageTitle, session.CreatorName,
            session.IsIdle, session.Category, now, session.Id, session.CreatorId);
    }

    public IReadOnlyList<ActivitySession> GetRange(DateTimeOffset start, DateTimeOffset end)
    {
        var rows = _database.Query("""
            SELECT id, start_utc_ms, end_utc_ms, activity_type, app_name, process_name,
                   window_title, executable_name, browser_name, url, domain, page_title, creator_name,
                   is_idle, category, creator_id
            FROM activity_sessions
            WHERE end_utc_ms > ?1 AND start_utc_ms < ?2
            ORDER BY start_utc_ms;
            """, start, end);
        return rows.Select(Map).ToList();
    }

    public ActivityTotalsRow GetTotals(DateTimeOffset start, DateTimeOffset end)
    {
        var row = _database.Query("""
            SELECT
                COALESCE(SUM(CASE WHEN is_idle=0 THEN CAST(MAX(0, MIN(end_utc_ms, ?2) - MAX(start_utc_ms, ?1)) / 1000 AS INTEGER) ELSE 0 END), 0) AS pc_seconds,
                COALESCE(SUM(CASE WHEN is_idle=1 THEN CAST(MAX(0, MIN(end_utc_ms, ?2) - MAX(start_utc_ms, ?1)) / 1000 AS INTEGER) ELSE 0 END), 0) AS idle_seconds
            FROM activity_sessions
            WHERE end_utc_ms > ?1 AND start_utc_ms < ?2;
            """, start, end).Single();
        return new ActivityTotalsRow(row.Int64("pc_seconds"), row.Int64("idle_seconds"));
    }

    public IReadOnlyList<ActivityUsageRow> GetAppUsage(DateTimeOffset start, DateTimeOffset end) =>
        GetUsage(start, end, "app_name", "is_idle=0");

    public IReadOnlyList<ActivityUsageRow> GetSiteUsage(DateTimeOffset start, DateTimeOffset end) =>
        GetUsage(start, end, "domain", "domain IS NOT NULL AND trim(domain)<>''");

    public IReadOnlyList<ActivityUsageRow> GetCreatorUsage(DateTimeOffset start, DateTimeOffset end) =>
        _database.Query("""
            WITH clipped AS (
                SELECT creator_name, creator_id, start_utc_ms, id,
                    CASE WHEN creator_id IS NOT NULL AND creator_id<>'' THEN 'id:' || creator_id
                         ELSE 'legacy:' || lower(COALESCE(domain,'')) || ':' || lower(creator_name) END AS creator_key,
                    CAST(MAX(0, MIN(end_utc_ms, ?2) - MAX(start_utc_ms, ?1)) / 1000 AS INTEGER) AS seconds
                FROM activity_sessions
                WHERE end_utc_ms > ?1 AND start_utc_ms < ?2 AND is_idle=0
                  AND creator_name IS NOT NULL AND trim(creator_name)<>''
            ), ranked AS (
                SELECT *, ROW_NUMBER() OVER (PARTITION BY creator_key ORDER BY start_utc_ms DESC, id DESC) AS recent
                FROM clipped
            )
            SELECT MAX(CASE WHEN recent=1 THEN creator_name END) AS name,
                MAX(CASE WHEN recent=1 THEN creator_id END) AS identity, SUM(seconds) AS seconds
            FROM ranked GROUP BY creator_key
            HAVING seconds > 0 ORDER BY seconds DESC, name COLLATE NOCASE;
            """, start, end)
        .Select(row => new ActivityUsageRow(row.String("name") ?? "Other", row.Int64("seconds"), row.String("identity")))
        .ToList();

    public IReadOnlyList<ActivityUsageRow> GetCategoryUsage(DateTimeOffset start, DateTimeOffset end) =>
        GetUsage(start, end, "category", "is_idle=0");

    // Labels only: reclassify historical totals without loading URLs/titles or
    // rewriting the category captured in the original session.
    public IReadOnlyList<ClassificationUsageRow> GetClassificationUsage(DateTimeOffset start, DateTimeOffset end) =>
        _database.Query("""
            SELECT app_name, process_name, domain, creator_name, creator_id, COUNT(*) AS sessions,
                SUM(CAST(MAX(0, MIN(end_utc_ms, ?2) - MAX(start_utc_ms, ?1)) / 1000 AS INTEGER)) AS seconds
            FROM activity_sessions
            WHERE end_utc_ms > ?1 AND start_utc_ms < ?2 AND is_idle=0
            GROUP BY app_name, process_name, domain, creator_name, creator_id
            HAVING seconds > 0;
            """, start, end).Select(row => new ClassificationUsageRow(
                row.String("app_name") ?? "Unknown", row.String("process_name") ?? string.Empty,
                row.String("domain"), row.String("creator_name"), row.String("creator_id"),
                row.Int64("seconds"), checked((int)row.Int64("sessions")))).ToList();

    public IReadOnlyList<DailyActivityRow> GetDailyUsage(DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start) return Array.Empty<DailyActivityRow>();
        return _database.Query("""
            WITH RECURSIVE days(day_start, day_end) AS (
                SELECT ?1, MIN(?1 + 86400000, ?2)
                UNION ALL
                SELECT day_end, MIN(day_end + 86400000, ?2)
                FROM days WHERE day_end < ?2
            )
            SELECT days.day_start,
                COALESCE(SUM(CASE WHEN sessions.id IS NOT NULL AND sessions.is_idle=0
                    THEN CAST(MAX(0, MIN(sessions.end_utc_ms, days.day_end) - MAX(sessions.start_utc_ms, days.day_start)) / 1000 AS INTEGER)
                    ELSE 0 END), 0) AS pc_seconds,
                COALESCE(SUM(CASE WHEN sessions.id IS NOT NULL AND sessions.is_idle=1
                    THEN CAST(MAX(0, MIN(sessions.end_utc_ms, days.day_end) - MAX(sessions.start_utc_ms, days.day_start)) / 1000 AS INTEGER)
                    ELSE 0 END), 0) AS idle_seconds
            FROM days
            LEFT JOIN activity_sessions AS sessions
              ON sessions.end_utc_ms > days.day_start AND sessions.start_utc_ms < days.day_end
            WHERE days.day_start < ?2
            GROUP BY days.day_start
            ORDER BY days.day_start;
            """, start, end)
            .Select(row => new DailyActivityRow(
                DateTimeOffset.FromUnixTimeMilliseconds(row.Int64("day_start")),
                row.Int64("pc_seconds"), row.Int64("idle_seconds")))
            .ToList();
    }

    public IReadOnlyList<HourlyActivityRow> GetHourlyUsage(DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start) return Array.Empty<HourlyActivityRow>();
        return _database.Query("""
            WITH RECURSIVE hours(hour_start, hour_end) AS (
                SELECT ?1, MIN(?1 + 3600000, ?2)
                UNION ALL
                SELECT hour_end, MIN(hour_end + 3600000, ?2)
                FROM hours WHERE hour_end < ?2
            )
            SELECT hours.hour_start,
                COALESCE(SUM(CASE WHEN sessions.id IS NOT NULL AND sessions.is_idle=0
                    THEN CAST(MAX(0, MIN(sessions.end_utc_ms, hours.hour_end) - MAX(sessions.start_utc_ms, hours.hour_start)) / 1000 AS INTEGER)
                    ELSE 0 END), 0) AS pc_seconds
            FROM hours
            LEFT JOIN activity_sessions AS sessions
              ON sessions.end_utc_ms > hours.hour_start AND sessions.start_utc_ms < hours.hour_end
            WHERE hours.hour_start < ?2
            GROUP BY hours.hour_start
            ORDER BY hours.hour_start;
            """, start, end)
            .Select(row => new HourlyActivityRow(
                DateTimeOffset.FromUnixTimeMilliseconds(row.Int64("hour_start")), row.Int64("pc_seconds")))
            .ToList();
    }

    public IReadOnlyList<ActivityIntervalRow> GetIntervals(DateTimeOffset start, DateTimeOffset end) =>
        _database.Query("""
            SELECT start_utc_ms, end_utc_ms, is_idle
            FROM activity_sessions
            WHERE end_utc_ms > ?1 AND start_utc_ms < ?2
            ORDER BY start_utc_ms;
            """, start, end)
        .Select(row => new ActivityIntervalRow(
            DateTimeOffset.FromUnixTimeMilliseconds(row.Int64("start_utc_ms")),
            DateTimeOffset.FromUnixTimeMilliseconds(row.Int64("end_utc_ms")),
            row.Boolean("is_idle")))
        .ToList();

    public DateTimeOffset? GetLatestUpdatedAt(DateTimeOffset start, DateTimeOffset end)
    {
        var value = _database.Query("""
            SELECT COALESCE(MAX(updated_utc_ms), 0) AS latest_utc_ms
            FROM activity_sessions
            WHERE end_utc_ms > ?1 AND start_utc_ms < ?2;
            """, start, end).Single().Int64("latest_utc_ms");
        return value <= 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(value);
    }

    private IReadOnlyList<ActivityUsageRow> GetUsage(DateTimeOffset start, DateTimeOffset end,
        string groupColumn, string filter)
    {
        // groupColumn and filter are private hard-coded values, never user input.
        var sql = $"""
            SELECT {groupColumn} AS name,
                SUM(CAST(MAX(0, MIN(end_utc_ms, ?2) - MAX(start_utc_ms, ?1)) / 1000 AS INTEGER)) AS seconds
            FROM activity_sessions
            WHERE end_utc_ms > ?1 AND start_utc_ms < ?2 AND {filter}
            GROUP BY {groupColumn} COLLATE NOCASE
            HAVING seconds > 0
            ORDER BY seconds DESC, name COLLATE NOCASE;
            """;
        return _database.Query(sql, start, end)
            .Select(row => new ActivityUsageRow(row.String("name") ?? "Other", row.Int64("seconds")))
            .ToList();
    }

    public void DeleteAll() => _database.ExecuteScript("""
        BEGIN IMMEDIATE;
        DELETE FROM activity_sessions;
        DELETE FROM activity_annotations;
        DELETE FROM ai_reports;
        DELETE FROM sqlite_sequence WHERE name='activity_sessions';
        DELETE FROM sqlite_sequence WHERE name='activity_annotations';
        DELETE FROM sqlite_sequence WHERE name='ai_reports';
        COMMIT;
        PRAGMA wal_checkpoint(TRUNCATE);
        VACUUM;
        PRAGMA wal_checkpoint(TRUNCATE);
        """);

    public void DeleteBefore(DateTimeOffset cutoff)
    {
        _database.Execute("DELETE FROM activity_sessions WHERE end_utc_ms < ?1;", cutoff);
        _database.Execute("DELETE FROM activity_annotations WHERE end_utc_ms < ?1;", cutoff);
        _database.Execute("DELETE FROM ai_reports WHERE period_end_utc_ms < ?1;", cutoff);
        _database.ExecuteScript("PRAGMA wal_checkpoint(PASSIVE);");
    }

    public void SanitizeStoredUrls()
    {
        var rows = _database.Query("""
            SELECT id, url FROM activity_sessions
            WHERE url IS NOT NULL AND (instr(url, '?') > 0 OR instr(url, '#') > 0 OR instr(url, '@') > 0);
            """);
        foreach (var row in rows)
        {
            var original = row.String("url");
            var sanitized = UrlPrivacyService.Sanitize(original);
            if (!string.Equals(original, sanitized, StringComparison.Ordinal))
                _database.Execute("UPDATE activity_sessions SET url=?1 WHERE id=?2;", sanitized, row.Int64("id"));
        }
    }

    public void DeleteByProcessName(string processName) =>
        _database.Execute("DELETE FROM activity_sessions WHERE process_name = ?1 COLLATE NOCASE;", processName);

    private static ActivitySession Map(SqliteRow row)
    {
        _ = Enum.TryParse<ActivityType>(row.String("activity_type"), out var type);
        return new ActivitySession
        {
            Id = row.Int64("id"),
            StartTime = DateTimeOffset.FromUnixTimeMilliseconds(row.Int64("start_utc_ms")),
            EndTime = DateTimeOffset.FromUnixTimeMilliseconds(row.Int64("end_utc_ms")),
            ActivityType = type,
            AppName = row.String("app_name") ?? "Unknown",
            ProcessName = row.String("process_name") ?? string.Empty,
            WindowTitle = row.String("window_title"),
            ExecutableName = row.String("executable_name"),
            BrowserName = row.String("browser_name"),
            Url = row.String("url"),
            Domain = row.String("domain"),
            PageTitle = row.String("page_title"),
            CreatorName = row.String("creator_name"),
            CreatorId = row.String("creator_id"),
            IsIdle = row.Boolean("is_idle"),
            Category = row.String("category") ?? "Other"
        };
    }
}

public sealed record ActivityTotalsRow(long PcSeconds, long IdleSeconds);
public sealed record ActivityUsageRow(string Name, long Seconds, string? Identity = null);
public sealed record DailyActivityRow(DateTimeOffset DayStart, long Seconds, long IdleSeconds);
public sealed record HourlyActivityRow(DateTimeOffset HourStart, long Seconds);
public sealed record ActivityIntervalRow(DateTimeOffset Start, DateTimeOffset End, bool IsIdle);
public sealed record ClassificationUsageRow(string AppName, string ProcessName, string? Domain,
    string? CreatorName, string? CreatorId, long Seconds, int SessionCount);
