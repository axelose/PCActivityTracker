using System.Text.RegularExpressions;
using PCActivityTracker.Domain;

namespace PCActivityTracker.Services;

public static class ActivityCategorizer
{
    private static readonly string[] Communication = { "slack", "teams", "discord", "zoom", "outlook", "mail" };
    private static readonly string[] Development = { "code", "visual studio", "rider", "github", "gitlab", "terminal", "powershell" };
    private static readonly string[] Entertainment = { "youtube", "netflix", "spotify", "twitch", "steam" };
    private static readonly string[] Productivity = { "word", "excel", "powerpoint", "notion", "obsidian", "explorer" };
    private static readonly string[] Research = { "wikipedia", "stackoverflow", "superuser", "learn.microsoft", "docs." };
    private static readonly string[] System = { "settings", "task manager", "shellhost", "searchhost", "startmenuexperiencehost" };
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

    public static string Categorize(string appName, string? domain, bool idle,
        string? creatorName = null, IReadOnlyList<CategoryRule>? rules = null) =>
        Evaluate(appName, domain, idle, creatorName, rules).Category;

    public static CategorizationResult Evaluate(string appName, string? domain, bool idle,
        string? creatorName = null, IReadOnlyList<CategoryRule>? rules = null)
    {
        if (idle) return new CategorizationResult("Idle", "Idleとして記録", null, 0);

        var matched = (rules ?? Array.Empty<CategoryRule>())
            .Where(rule => MatchesRule(rule, appName, domain, creatorName))
            .OrderByDescending(rule => rule.Priority)
            .ThenBy(rule => TargetRank(rule.Target))
            .ThenByDescending(rule => rule.Id)
            .ToList();
        if (matched.Count > 0)
        {
            var winner = matched[0];
            var target = winner.Target switch
            {
                CategoryRuleTarget.Application => "アプリ",
                CategoryRuleTarget.Domain => "ドメイン",
                _ => "配信者・チャンネル"
            };
            var mode = winner.MatchMode == CategoryMatchMode.Regex ? "正規表現" : "ワイルドカード";
            var conflicts = Math.Max(0, matched.Count - 1);
            var conflictText = conflicts == 0 ? string.Empty : $"・他{conflicts}件より優先";
            return new CategorizationResult(winner.Category,
                $"{target}「{winner.Pattern}」の{mode}ルール（優先度 {winner.Priority}）{conflictText}", winner, conflicts);
        }

        var category = BuiltInCategory(appName, domain);
        return new CategorizationResult(category, $"標準分類（{category}）", null, 0);
    }

    public static bool MatchesRule(CategoryRule rule, string appName, string? domain, string? creatorName)
    {
        var value = rule.Target switch
        {
            CategoryRuleTarget.Application => appName,
            CategoryRuleTarget.Domain => domain,
            CategoryRuleTarget.Creator => creatorName,
            _ => null
        };
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(rule.Pattern)) return false;

        if (rule.MatchMode == CategoryMatchMode.Regex)
        {
            try
            {
                return Regex.IsMatch(value, rule.Pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            }
            catch (ArgumentException) { return false; }
            catch (RegexMatchTimeoutException) { return false; }
        }

        if (rule.Target == CategoryRuleTarget.Domain && !rule.Pattern.Contains('*'))
        {
            var domainPattern = rule.Pattern.Trim().TrimStart('.');
            return value.Equals(domainPattern, StringComparison.OrdinalIgnoreCase) ||
                   value.EndsWith('.' + domainPattern, StringComparison.OrdinalIgnoreCase);
        }
        return WildcardMatches(rule.Pattern, value);
    }

    private static bool WildcardMatches(string pattern, string value)
    {
        pattern = pattern.Trim();
        if (pattern == "*") return true;
        try
        {
            var expression = "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$";
            return Regex.IsMatch(value, expression,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private static int TargetRank(CategoryRuleTarget target) => target switch
    {
        CategoryRuleTarget.Creator => 0,
        CategoryRuleTarget.Domain => 1,
        _ => 2
    };

    private static string BuiltInCategory(string appName, string? domain)
    {
        var value = $"{appName} {domain}".ToLowerInvariant();
        if (Communication.Any(value.Contains)) return "Communication";
        if (Development.Any(value.Contains)) return "Development";
        if (Research.Any(value.Contains)) return "Research";
        if (Entertainment.Any(value.Contains)) return "Entertainment";
        if (Productivity.Any(value.Contains)) return "Productivity";
        if (System.Any(value.Contains)) return "System";
        return "Other";
    }
}

public sealed record CategorizationResult(string Category, string Reason, CategoryRule? MatchedRule, int ConflictCount);
