using PCActivityTracker.Domain;

namespace PCActivityTracker.Data;

public sealed class SettingsRepository
{
    private readonly SqliteDatabase _database;

    public SettingsRepository(SqliteDatabase database) => _database = database;

    public Dictionary<string, string> GetSettings() => _database
        .Query("SELECT key, value FROM settings;")
        .ToDictionary(row => row.String("key")!, row => row.String("value") ?? string.Empty, StringComparer.OrdinalIgnoreCase);

    public void Set(string key, string value) => _database.Execute("""
        INSERT INTO settings(key, value) VALUES (?1, ?2)
        ON CONFLICT(key) DO UPDATE SET value=excluded.value;
        """, key, value);

    public void Remove(string key) => _database.Execute("DELETE FROM settings WHERE key=?1;", key);

    public void SetMany(params (string Key, string Value)[] settings)
    {
        if (settings.Length == 0) return;
        var valueClauses = settings.Select((_, index) => $"(?{index * 2 + 1}, ?{index * 2 + 2})");
        var parameters = settings.SelectMany(item => new object?[] { item.Key, item.Value }).ToArray();
        _database.Execute($"""
            INSERT INTO settings(key, value) VALUES {string.Join(", ", valueClauses)}
            ON CONFLICT(key) DO UPDATE SET value=excluded.value;
            """, parameters);
    }

    public IReadOnlyList<ExclusionRule> GetExclusions()
    {
        return _database.Query("SELECT id, exclusion_type, value FROM exclusions ORDER BY exclusion_type, value;")
            .Select(row => new ExclusionRule
            {
                Id = row.Int64("id"),
                Type = Enum.TryParse<ExclusionType>(row.String("exclusion_type"), out var type) ? type : ExclusionType.Application,
                Value = row.String("value") ?? string.Empty
            }).ToList();
    }

    public void AddExclusion(ExclusionType type, string value) => _database.Execute("""
        INSERT OR IGNORE INTO exclusions(exclusion_type, value) VALUES (?1, ?2);
        """, type.ToString(), value.Trim());

    public void RemoveExclusion(long id) => _database.Execute("DELETE FROM exclusions WHERE id=?1;", id);

    public IReadOnlyList<CategoryRule> GetCategoryRules() => _database.Query("""
        SELECT id, target_type, pattern, category, match_mode, color, priority
        FROM category_rules
        ORDER BY priority DESC,
                 CASE target_type WHEN 'Creator' THEN 0 WHEN 'Domain' THEN 1 ELSE 2 END,
                 id DESC;
        """).Select(row => new CategoryRule
        {
            Id = row.Int64("id"),
            Target = Enum.TryParse<CategoryRuleTarget>(row.String("target_type"), out var target)
                ? target : CategoryRuleTarget.Application,
            Pattern = row.String("pattern") ?? string.Empty,
            Category = row.String("category") ?? "Other",
            MatchMode = Enum.TryParse<CategoryMatchMode>(row.String("match_mode"), out var mode)
                ? mode : CategoryMatchMode.Wildcard,
            Color = row.String("color") ?? "#64748B",
            Priority = checked((int)row.Int64("priority"))
        }).ToList();

    public void AddCategoryRule(CategoryRuleTarget target, string pattern, string category,
        CategoryMatchMode matchMode = CategoryMatchMode.Wildcard, string color = "#64748B", int priority = 100) => _database.Execute("""
        INSERT INTO category_rules(target_type, pattern, category, match_mode, color, priority, created_utc_ms)
        VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)
        ON CONFLICT(target_type, pattern) DO UPDATE SET
            category=excluded.category, match_mode=excluded.match_mode, color=excluded.color,
            priority=excluded.priority, created_utc_ms=excluded.created_utc_ms;
        """, target.ToString(), pattern.Trim(), category.Trim(), matchMode.ToString(), color, priority,
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public void RemoveCategoryRule(long id) => _database.Execute("DELETE FROM category_rules WHERE id=?1;", id);
}
