using System.Text.RegularExpressions;
using PCActivityTracker.Domain;

namespace PCActivityTracker.Services;

public static class ActivityCategorizer
{
    private static readonly (string Category, string[] Domains)[] SitePresets =
    [
        ("Research", ["learn.microsoft.com", "developer.mozilla.org", "docs.github.com", "docs.gitlab.com", "docs.python.org", "stackoverflow.com", "superuser.com", "wikipedia.org", "readthedocs.io"]),
        ("Communication", ["slack.com", "teams.microsoft.com", "discord.com", "zoom.us", "outlook.live.com", "outlook.office.com", "mail.google.com"]),
        ("Development", ["github.com", "gitlab.com", "bitbucket.org", "replit.com", "codesandbox.io"]),
        ("Entertainment", ["youtube.com", "youtu.be", "twitch.tv", "netflix.com", "spotify.com", "tv.dmm.com"]),
        ("Productivity", ["notion.so", "notion.site", "docs.google.com", "sheets.google.com", "office.com", "microsoft365.com", "figma.com"])
    ];
    private static readonly (string Category, string[] Names)[] AppPresets =
    [
        ("Communication", ["slack", "teams", "ms-teams", "microsoft teams", "discord", "zoom", "outlook"]),
        ("Development", ["code", "visual studio code", "devenv", "visual studio", "rider", "jetbrains rider", "windowsterminal", "windows terminal", "terminal", "powershell", "pwsh", "git-bash"]),
        ("Entertainment", ["spotify", "netflix", "steam", "steamwebhelper"]),
        ("Productivity", ["winword", "word", "microsoft word", "excel", "microsoft excel", "powerpnt", "powerpoint", "microsoft powerpoint", "notion", "obsidian", "explorer", "windows explorer"]),
        ("System", ["systemsettings", "settings", "taskmgr", "task manager", "shellhost", "searchhost", "startmenuexperiencehost"])
    ];
    private static readonly IReadOnlySet<string> Games = GameSafetyService.Parse(
        AppSettings.DefaultGameProcessNames + ",deadlock,LobotomyCorp");
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

    public static string Categorize(string appName, string? domain, bool idle,
        string? creatorName = null, IReadOnlyList<CategoryRule>? rules = null,
        string? processName = null, string? creatorId = null) =>
        Evaluate(appName, domain, idle, creatorName, rules, processName, creatorId).Category;

    public static CategorizationResult Evaluate(ActivitySession session, IReadOnlyList<CategoryRule>? rules = null) =>
        Evaluate(session.AppName, session.Domain, session.IsIdle, session.CreatorName, rules, session.ProcessName, session.CreatorId);

    public static CategorizationResult Evaluate(string appName, string? domain, bool idle,
        string? creatorName = null, IReadOnlyList<CategoryRule>? rules = null,
        string? processName = null, string? creatorId = null)
    {
        if (idle) return new CategorizationResult("Idle", "Idleとして記録", null, 0);

        var matched = (rules ?? Array.Empty<CategoryRule>())
            .Where(rule => MatchesRule(rule, appName, domain, creatorName, processName, creatorId))
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
                CategoryRuleTarget.ProcessName => "プロセス",
                CategoryRuleTarget.CreatorId => "チャンネルID",
                _ => "配信者・チャンネル"
            };
            var mode = winner.MatchMode switch { CategoryMatchMode.Regex => "正規表現", CategoryMatchMode.Exact => "完全一致", _ => "ワイルドカード" };
            var conflicts = Math.Max(0, matched.Count - 1);
            var conflictText = conflicts == 0 ? string.Empty : $"・他{conflicts}件より優先";
            return new CategorizationResult(winner.Category,
                $"{target}「{winner.Pattern}」の{mode}ルール（優先度 {winner.Priority}）{conflictText}", winner, conflicts);
        }

        return BuiltInCategory(appName, domain, processName);
    }

    public static bool MatchesRule(CategoryRule rule, ActivitySession session) =>
        MatchesRule(rule, session.AppName, session.Domain, session.CreatorName, session.ProcessName, session.CreatorId);

    public static bool MatchesRule(CategoryRule rule, string appName, string? domain, string? creatorName,
        string? processName = null, string? creatorId = null)
    {
        var value = rule.Target switch
        {
            CategoryRuleTarget.Application => appName,
            CategoryRuleTarget.Domain => domain,
            CategoryRuleTarget.Creator => creatorName,
            CategoryRuleTarget.ProcessName => NormalizeProcessName(processName),
            CategoryRuleTarget.CreatorId => creatorId,
            _ => null
        };
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(rule.Pattern)) return false;

        if (rule.MatchMode == CategoryMatchMode.Exact)
        {
            var pattern = rule.Target == CategoryRuleTarget.ProcessName ? NormalizeProcessName(rule.Pattern) : rule.Pattern.Trim();
            var actual = rule.Target == CategoryRuleTarget.Domain ? value.Trim().TrimEnd('.') : value.Trim();
            if (rule.Target == CategoryRuleTarget.Domain) pattern = pattern.TrimEnd('.');
            return string.Equals(actual, pattern,
                rule.Target == CategoryRuleTarget.CreatorId ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }

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
        CategoryRuleTarget.CreatorId => 0,
        CategoryRuleTarget.Creator => 1,
        CategoryRuleTarget.Domain => 2,
        CategoryRuleTarget.ProcessName => 3,
        _ => 4
    };

    internal static string NormalizeProcessName(string? value)
    {
        var name = System.IO.Path.GetFileName(value?.Trim() ?? string.Empty);
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private static CategorizationResult BuiltInCategory(string appName, string? domain, string? processName)
    {
        if (!string.IsNullOrWhiteSpace(domain))
        {
            var host = domain.Trim().TrimEnd('.');
            foreach (var (category, roots) in SitePresets)
                foreach (var root in roots)
                    if (host.Equals(root, StringComparison.OrdinalIgnoreCase) || host.EndsWith('.' + root, StringComparison.OrdinalIgnoreCase))
                        return new(category, $"標準分類: ドメイン「{root}」とそのサブドメイン", null, 0);
            // An unknown website must not inherit the browser application's purpose.
            return new("Other", "未分類: 一致するドメインの標準分類・ユーザールールなし", null, 0);
        }
        var process = NormalizeProcessName(processName);
        if (Games.Contains(process) || Games.Contains(appName.Trim()))
            return new("Gaming", "標準分類: 登録済みゲーム名（プロセス内部にはアクセスしません）", null, 0);
        foreach (var (category, names) in AppPresets)
            if (names.Any(name => name.Equals(process, StringComparison.OrdinalIgnoreCase) || name.Equals(appName.Trim(), StringComparison.OrdinalIgnoreCase)))
                return new(category, $"標準分類: アプリ／プロセス名の完全一致（{category}）", null, 0);
        return new("Other", "未分類: 一致するアプリ／プロセスの標準分類・ユーザールールなし", null, 0);
    }
}

public sealed record CategorizationResult(string Category, string Reason, CategoryRule? MatchedRule, int ConflictCount);
