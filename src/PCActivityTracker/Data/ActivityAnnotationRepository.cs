using PCActivityTracker.Domain;

namespace PCActivityTracker.Data;

public sealed class ActivityAnnotationRepository
{
    private readonly SqliteDatabase _database;

    public ActivityAnnotationRepository(SqliteDatabase database) => _database = database;

    public IReadOnlyList<ActivityAnnotation> GetRange(DateTimeOffset start, DateTimeOffset end) =>
        _database.Query("""
            SELECT id, start_utc_ms, end_utc_ms, project_name, task_name, note,
                   category_override, created_utc_ms, updated_utc_ms
            FROM activity_annotations
            WHERE end_utc_ms > ?1 AND start_utc_ms < ?2
            ORDER BY start_utc_ms, updated_utc_ms DESC;
            """, start, end).Select(Map).ToList();

    public long Save(ActivityAnnotation annotation)
    {
        Validate(annotation);
        var now = DateTimeOffset.UtcNow;
        if (annotation.Id == 0)
        {
            annotation.CreatedAt = now;
            annotation.UpdatedAt = now;
            annotation.Id = _database.Execute("""
                INSERT INTO activity_annotations(
                    start_utc_ms, end_utc_ms, project_name, task_name, note,
                    category_override, created_utc_ms, updated_utc_ms)
                VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8);
                """, annotation.StartTime, annotation.EndTime, Normalize(annotation.ProjectName, 120),
                Normalize(annotation.TaskName, 160), Normalize(annotation.Note, 1000),
                Normalize(annotation.CategoryOverride, 120), now, now);
            return annotation.Id;
        }

        annotation.UpdatedAt = now;
        _database.Execute("""
            UPDATE activity_annotations SET start_utc_ms=?1, end_utc_ms=?2,
                project_name=?3, task_name=?4, note=?5, category_override=?6,
                updated_utc_ms=?7
            WHERE id=?8;
            """, annotation.StartTime, annotation.EndTime, Normalize(annotation.ProjectName, 120),
            Normalize(annotation.TaskName, 160), Normalize(annotation.Note, 1000),
            Normalize(annotation.CategoryOverride, 120), now, annotation.Id);
        return annotation.Id;
    }

    public void Delete(long id) => _database.Execute("DELETE FROM activity_annotations WHERE id=?1;", id);

    private static void Validate(ActivityAnnotation annotation)
    {
        if (annotation.EndTime <= annotation.StartTime)
            throw new ArgumentException("補正の終了時刻は開始時刻より後にしてください。");
        if (annotation.EndTime - annotation.StartTime > TimeSpan.FromDays(31))
            throw new ArgumentException("1件の補正範囲は31日以内にしてください。");
        if (string.IsNullOrWhiteSpace(annotation.ProjectName) &&
            string.IsNullOrWhiteSpace(annotation.TaskName) &&
            string.IsNullOrWhiteSpace(annotation.Note) &&
            string.IsNullOrWhiteSpace(annotation.CategoryOverride))
            throw new ArgumentException("プロジェクト、タスク、メモ、カテゴリ補正のいずれかを入力してください。");
    }

    private static string Normalize(string? value, int maxLength)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static ActivityAnnotation Map(SqliteRow row) => new()
    {
        Id = row.Int64("id"),
        StartTime = DateTimeOffset.FromUnixTimeMilliseconds(row.Int64("start_utc_ms")),
        EndTime = DateTimeOffset.FromUnixTimeMilliseconds(row.Int64("end_utc_ms")),
        ProjectName = row.String("project_name") ?? string.Empty,
        TaskName = row.String("task_name") ?? string.Empty,
        Note = row.String("note") ?? string.Empty,
        CategoryOverride = row.String("category_override") ?? string.Empty,
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(row.Int64("created_utc_ms")),
        UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(row.Int64("updated_utc_ms"))
    };
}
