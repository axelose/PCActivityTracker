using PCActivityTracker.Domain;

namespace PCActivityTracker.Services;

public sealed record CategoryRuleDraft(CategoryRuleTarget Target, string Pattern, string Category, int Priority = 300)
{
    public CategoryRule ToRule() => new()
    {
        Target = Target, Pattern = Pattern.Trim(), Category = SettingsService.NormalizeCategoryPath(Category),
        Priority = Math.Clamp(Priority, 0, 1000), MatchMode = CategoryMatchMode.Exact
    };

    public static CategoryRuleDraft FromSession(ActivitySession session, string category)
    {
        if (!string.IsNullOrWhiteSpace(session.Domain))
            return new(CategoryRuleTarget.Domain, session.Domain.Trim().TrimEnd('.').ToLowerInvariant(), category);
        var process = ActivityCategorizer.NormalizeProcessName(session.ProcessName);
        return string.IsNullOrWhiteSpace(process)
            ? new(CategoryRuleTarget.Application, session.AppName.Trim().ToLowerInvariant(), category)
            : new(CategoryRuleTarget.ProcessName, process.ToLowerInvariant(), category);
    }

    internal static bool SameKey(CategoryRule left, CategoryRule right) => left.Target == right.Target &&
        (left.Target == CategoryRuleTarget.CreatorId ? left.Pattern == right.Pattern : AsciiKey(left.Pattern) == AsciiKey(right.Pattern));

    // SQLite's legacy NOCASE collation folds ASCII only, not all Unicode.
    // Preview must model the actual upsert key rather than a broader comparer.
    private static string AsciiKey(string text) => new(text.Select(character => character is >= 'A' and <= 'Z'
        ? (char)(character + ('a' - 'A')) : character).ToArray());
}

public sealed record CategoryRuleReview(DateTimeOffset Start, DateTimeOffset End,
    IReadOnlyList<CategoryRuleDraft> Drafts, int SessionCount, long MatchingSeconds,
    long ChangedSeconds, long ProtectedSeconds, int ConflictCount, string Fingerprint);

public sealed record UnclassifiedEntry(CategoryRuleTarget Target, string Pattern, string Name,
    long Seconds, int SessionCount);
