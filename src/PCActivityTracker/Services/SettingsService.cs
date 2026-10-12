using System.Security.Cryptography;
using System.Text.RegularExpressions;
using PCActivityTracker.Data;
using PCActivityTracker.Domain;

namespace PCActivityTracker.Services;

public sealed class SettingsService
{
    private const string ProtectedBrowserTokenKey = "BrowserBridgeTokenProtected";
    private const int MaximumPairedBrowserOrigins = 5;
    private readonly SettingsRepository _repository;
    private readonly object _gate = new();
    private AppSettings _current = new();
    private List<ExclusionRule> _exclusions = new();
    private List<CategoryRule> _categoryRules = new();

    public SettingsService(SettingsRepository repository) => _repository = repository;

    public SavedExplorerSearchStore ExplorerSearches => new(_repository);

    public event EventHandler? Changed;

    public AppSettings Current
    {
        get
        {
            lock (_gate) return Clone(_current);
        }
    }

    public IReadOnlyList<ExclusionRule> Exclusions
    {
        get
        {
            lock (_gate) return _exclusions.Select(Clone).ToList();
        }
    }

    public IReadOnlyList<CategoryRule> CategoryRules
    {
        get
        {
            lock (_gate) return _categoryRules.Select(Clone).ToList();
        }
    }

    public void Load()
    {
        var values = _repository.GetSettings();
        var settings = new AppSettings
        {
            PrivacyConsentVersion = GetInt(values, nameof(AppSettings.PrivacyConsentVersion), 0, 0, AppSettings.CurrentPrivacyConsentVersion),
            RecordingEnabled = GetBool(values, nameof(AppSettings.RecordingEnabled), false),
            RecordUrls = GetBool(values, nameof(AppSettings.RecordUrls), false),
            RecordPageTitles = GetBool(values, nameof(AppSettings.RecordPageTitles), false),
            RecordWindowTitles = GetBool(values, nameof(AppSettings.RecordWindowTitles), false),
            RecordCreatorNames = GetBool(values, nameof(AppSettings.RecordCreatorNames), false),
            IdleThresholdMinutes = GetInt(values, nameof(AppSettings.IdleThresholdMinutes), 5, 1, 60),
            RetentionDays = GetInt(values, nameof(AppSettings.RetentionDays), 90, 1, 3650),
            StartWithWindows = GetBool(values, nameof(AppSettings.StartWithWindows), false),
            StartMinimized = GetBool(values, nameof(AppSettings.StartMinimized), false),
            AutomaticBackupsEnabled = GetBool(values, nameof(AppSettings.AutomaticBackupsEnabled), true),
            OllamaEndpoint = values.GetValueOrDefault(nameof(AppSettings.OllamaEndpoint), "http://127.0.0.1:11434"),
            OllamaModel = values.GetValueOrDefault(nameof(AppSettings.OllamaModel), string.Empty),
            UnloadOllamaAfterGeneration = GetBool(values, nameof(AppSettings.UnloadOllamaAfterGeneration), true),
            OllamaTimeoutMinutes = GetInt(values, nameof(AppSettings.OllamaTimeoutMinutes), 10, 1, 60),
            GameSafeModeEnabled = GetBool(values, nameof(AppSettings.GameSafeModeEnabled), true),
            RequireIdleOllama = GetBool(values, nameof(AppSettings.RequireIdleOllama), true),
            GameProcessNames = values.GetValueOrDefault(nameof(AppSettings.GameProcessNames), AppSettings.DefaultGameProcessNames),
            ThemeMode = NormalizeTheme(values.GetValueOrDefault(nameof(AppSettings.ThemeMode), "System")),
            BrowserBridgeAllowedOrigins = NormalizeOrigins(values.GetValueOrDefault(nameof(AppSettings.BrowserBridgeAllowedOrigins), string.Empty))
        };

        var legacyToken = values.GetValueOrDefault(nameof(AppSettings.BrowserBridgeToken), string.Empty);
        if (!ProtectedSecretService.TryUnprotect(values.GetValueOrDefault(ProtectedBrowserTokenKey), out var token))
        {
            token = string.IsNullOrWhiteSpace(legacyToken) ? CreateToken() : legacyToken;
            _repository.Set(ProtectedBrowserTokenKey, ProtectedSecretService.Protect(token));
        }
        settings.BrowserBridgeToken = token;
        if (!string.IsNullOrWhiteSpace(legacyToken)) _repository.Remove(nameof(AppSettings.BrowserBridgeToken));
        if (values.ContainsKey("BrowserBridgePort")) _repository.Remove("BrowserBridgePort");

        lock (_gate)
        {
            _current = settings;
            _exclusions = _repository.GetExclusions().ToList();
            _categoryRules = _repository.GetCategoryRules().ToList();
        }
    }

    public void Save(AppSettings settings)
    {
        settings.IdleThresholdMinutes = Math.Clamp(settings.IdleThresholdMinutes, 1, 60);
        settings.RetentionDays = Math.Clamp(settings.RetentionDays, 1, 3650);
        settings.OllamaTimeoutMinutes = Math.Clamp(settings.OllamaTimeoutMinutes, 1, 60);
        settings.GameProcessNames = NormalizeProcessList(settings.GameProcessNames);
        settings.ThemeMode = NormalizeTheme(settings.ThemeMode);
        _repository.SetMany(
            (nameof(settings.PrivacyConsentVersion), settings.PrivacyConsentVersion.ToString()),
            (nameof(settings.RecordingEnabled), settings.RecordingEnabled.ToString()),
            (nameof(settings.RecordUrls), settings.RecordUrls.ToString()),
            (nameof(settings.RecordPageTitles), settings.RecordPageTitles.ToString()),
            (nameof(settings.RecordWindowTitles), settings.RecordWindowTitles.ToString()),
            (nameof(settings.RecordCreatorNames), settings.RecordCreatorNames.ToString()),
            (nameof(settings.IdleThresholdMinutes), settings.IdleThresholdMinutes.ToString()),
            (nameof(settings.RetentionDays), settings.RetentionDays.ToString()),
            (nameof(settings.StartWithWindows), settings.StartWithWindows.ToString()),
            (nameof(settings.StartMinimized), settings.StartMinimized.ToString()),
            (nameof(settings.AutomaticBackupsEnabled), settings.AutomaticBackupsEnabled.ToString()),
            (nameof(settings.OllamaEndpoint), settings.OllamaEndpoint.Trim()),
            (nameof(settings.OllamaModel), settings.OllamaModel.Trim()),
            (nameof(settings.UnloadOllamaAfterGeneration), settings.UnloadOllamaAfterGeneration.ToString()),
            (nameof(settings.OllamaTimeoutMinutes), settings.OllamaTimeoutMinutes.ToString()),
            (nameof(settings.GameSafeModeEnabled), settings.GameSafeModeEnabled.ToString()),
            (nameof(settings.RequireIdleOllama), settings.RequireIdleOllama.ToString()),
            (nameof(settings.GameProcessNames), settings.GameProcessNames),
            (nameof(settings.ThemeMode), settings.ThemeMode),
            (nameof(settings.BrowserBridgeAllowedOrigins), NormalizeOrigins(settings.BrowserBridgeAllowedOrigins)));

        lock (_gate) _current = Clone(settings);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void AddExclusion(ExclusionType type, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        _repository.AddExclusion(type, value.Trim());
        ReloadExclusions();
    }

    public void RemoveExclusion(long id)
    {
        _repository.RemoveExclusion(id);
        ReloadExclusions();
    }

    public void AddCategoryRule(CategoryRuleTarget target, string pattern, string category,
        CategoryMatchMode matchMode = CategoryMatchMode.Wildcard, string color = "#64748B", int priority = 100)
    {
        pattern = pattern.Trim();
        category = NormalizeCategoryPath(category);
        color = NormalizeColor(color);
        priority = Math.Clamp(priority, 0, 1000);
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(category)) return;
        if (pattern.Length > 200) throw new ArgumentException("分類パターンは200文字以内で入力してください。");
        if (category.Length > 120) throw new ArgumentException("カテゴリ階層は120文字以内で入力してください。");
        if (!Enum.IsDefined(target) || !Enum.IsDefined(matchMode)) throw new ArgumentException("分類対象または照合方式が不正です。");
        if (target == CategoryRuleTarget.CreatorId && matchMode != CategoryMatchMode.Exact)
            throw new ArgumentException("チャンネルIDは完全一致で指定してください。");
        if (matchMode == CategoryMatchMode.Regex)
        {
            try { _ = Regex.IsMatch(string.Empty, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)); }
            catch (ArgumentException) { throw new ArgumentException("正規表現の構文が正しくありません。"); }
        }
        _repository.AddCategoryRule(target, pattern, category, matchMode, color, priority);
        ReloadCategoryRules();
    }

    public void RemoveCategoryRule(long id)
    {
        _repository.RemoveCategoryRule(id);
        ReloadCategoryRules();
    }

    public void AddCategoryRules(IReadOnlyList<CategoryRuleDraft> drafts)
    {
        if (drafts.Count is < 1 or > 100) throw new ArgumentException("一度に保存できる分類ルールは1～100件です。");
        var rules = drafts.Select(draft => draft.ToRule()).ToArray();
        foreach (var rule in rules)
        {
            if (!Enum.IsDefined(rule.Target) || string.IsNullOrWhiteSpace(rule.Pattern) || rule.Pattern.Length > 200 ||
                string.IsNullOrWhiteSpace(rule.Category) || rule.Category.Length > 120 || rule.Pattern.Any(char.IsControl))
                throw new ArgumentException("分類対象・パターン・カテゴリを確認してください（パターン200文字、カテゴリ120文字以内）。");
        }
        _repository.AddCategoryRules(rules);
        ReloadCategoryRules();
    }

    public string Categorize(string appName, string? domain, bool idle, string? creatorName = null,
        string? processName = null, string? creatorId = null)
    {
        lock (_gate)
            return ActivityCategorizer.Categorize(appName, domain, idle, creatorName, _categoryRules, processName, creatorId);
    }

    public bool IsApplicationExcluded(string appName, string processName, string executableName)
    {
        lock (_gate)
        {
            return _exclusions.Where(rule => rule.Type == ExclusionType.Application)
                .Any(rule => Matches(rule.Value, appName) || Matches(rule.Value, processName) || Matches(rule.Value, executableName));
        }
    }

    public bool IsDomainExcluded(string domain)
    {
        lock (_gate)
        {
            return _exclusions.Where(rule => rule.Type == ExclusionType.Domain)
                .Any(rule => DomainMatches(rule.Value, domain));
        }
    }

    public bool IsBrowserOriginPaired(string origin)
    {
        var normalized = NormalizeBrowserOrigin(origin);
        if (normalized is null) return false;
        lock (_gate) return SplitOrigins(_current.BrowserBridgeAllowedOrigins).Contains(normalized, StringComparer.Ordinal);
    }

    public bool PairBrowserOrigin(string origin)
    {
        var normalized = NormalizeBrowserOrigin(origin)
            ?? throw new ArgumentException("Browser extension origin is invalid.", nameof(origin));
        lock (_gate)
        {
            var origins = SplitOrigins(_current.BrowserBridgeAllowedOrigins).ToList();
            if (origins.Contains(normalized, StringComparer.Ordinal)) return false;
            if (origins.Count >= MaximumPairedBrowserOrigins)
                throw new InvalidOperationException("ペアリング上限に達しました。設定画面で連携をリセットしてください。");
            origins.Add(normalized);
            _current.BrowserBridgeAllowedOrigins = string.Join(';', origins);
            _repository.Set(nameof(AppSettings.BrowserBridgeAllowedOrigins), _current.BrowserBridgeAllowedOrigins);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public string ResetBrowserPairing()
    {
        var token = CreateToken();
        _repository.Set(ProtectedBrowserTokenKey, ProtectedSecretService.Protect(token));
        _repository.Set(nameof(AppSettings.BrowserBridgeAllowedOrigins), string.Empty);
        lock (_gate)
        {
            _current.BrowserBridgeToken = token;
            _current.BrowserBridgeAllowedOrigins = string.Empty;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return token;
    }

    private void ReloadExclusions()
    {
        lock (_gate) _exclusions = _repository.GetExclusions().ToList();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ReloadCategoryRules()
    {
        lock (_gate) _categoryRules = _repository.GetCategoryRules().ToList();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool Matches(string rule, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        rule = rule.Trim();
        if (rule == "*") return true;
        if (rule.StartsWith('*') && rule.EndsWith('*'))
            return value.Contains(rule.Trim('*'), StringComparison.OrdinalIgnoreCase);
        if (rule.StartsWith('*')) return value.EndsWith(rule[1..], StringComparison.OrdinalIgnoreCase);
        if (rule.EndsWith('*')) return value.StartsWith(rule[..^1], StringComparison.OrdinalIgnoreCase);
        return value.Equals(rule, StringComparison.OrdinalIgnoreCase);
    }

    private static bool DomainMatches(string rule, string domain)
    {
        rule = rule.Trim().TrimStart('*').TrimStart('.');
        return domain.Equals(rule, StringComparison.OrdinalIgnoreCase) ||
               domain.EndsWith('.' + rule, StringComparison.OrdinalIgnoreCase);
    }

    private static bool GetBool(IReadOnlyDictionary<string, string> values, string key, bool fallback) =>
        values.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static int GetInt(IReadOnlyDictionary<string, string> values, string key, int fallback, int min, int max) =>
        values.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? Math.Clamp(parsed, min, max) : fallback;

    private static string NormalizeTheme(string? value) => value?.Trim() switch
    {
        "Light" => "Light",
        "Dark" => "Dark",
        _ => "System"
    };

    public static string NormalizeCategoryPath(string? value)
    {
        var segments = (value ?? string.Empty)
            .Split(new[] { '>', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .Take(8);
        return string.Join(" > ", segments);
    }

    public static string NormalizeColor(string? value)
    {
        var color = (value ?? string.Empty).Trim().ToUpperInvariant();
        return Regex.IsMatch(color, "^#[0-9A-F]{6}$", RegexOptions.CultureInvariant)
            ? color
            : "#64748B";
    }

    private static string NormalizeProcessList(string? value)
    {
        var names = (value ?? string.Empty).Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(item => System.IO.Path.GetFileNameWithoutExtension(item.Trim()))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(100);
        return string.Join(',', names);
    }

    public static string? NormalizeBrowserOrigin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().TrimEnd('/').ToLowerInvariant();
        const string prefix = "chrome-extension://";
        if (!normalized.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var id = normalized[prefix.Length..];
        return id.Length == 32 && id.All(character => character is >= 'a' and <= 'p')
            ? prefix + id
            : null;
    }

    private static IEnumerable<string> SplitOrigins(string? value) =>
        (value ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeBrowserOrigin).Where(origin => origin is not null).Cast<string>()
            .Distinct(StringComparer.Ordinal).Take(MaximumPairedBrowserOrigins);

    private static string NormalizeOrigins(string? value) => string.Join(';', SplitOrigins(value));

    private static string CreateToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static AppSettings Clone(AppSettings source) => new()
    {
        PrivacyConsentVersion = source.PrivacyConsentVersion,
        RecordingEnabled = source.RecordingEnabled,
        RecordUrls = source.RecordUrls,
        RecordPageTitles = source.RecordPageTitles,
        RecordWindowTitles = source.RecordWindowTitles,
        RecordCreatorNames = source.RecordCreatorNames,
        IdleThresholdMinutes = source.IdleThresholdMinutes,
        RetentionDays = source.RetentionDays,
        StartWithWindows = source.StartWithWindows,
        StartMinimized = source.StartMinimized,
        AutomaticBackupsEnabled = source.AutomaticBackupsEnabled,
        OllamaEndpoint = source.OllamaEndpoint,
        OllamaModel = source.OllamaModel,
        UnloadOllamaAfterGeneration = source.UnloadOllamaAfterGeneration,
        OllamaTimeoutMinutes = source.OllamaTimeoutMinutes,
        GameSafeModeEnabled = source.GameSafeModeEnabled,
        RequireIdleOllama = source.RequireIdleOllama,
        GameProcessNames = source.GameProcessNames,
        ThemeMode = source.ThemeMode,
        BrowserBridgeToken = source.BrowserBridgeToken,
        BrowserBridgeAllowedOrigins = source.BrowserBridgeAllowedOrigins
    };

    private static ExclusionRule Clone(ExclusionRule source) => new() { Id = source.Id, Type = source.Type, Value = source.Value };
    private static CategoryRule Clone(CategoryRule source) => new()
    {
        Id = source.Id, Target = source.Target, Pattern = source.Pattern, Category = source.Category,
        MatchMode = source.MatchMode, Color = source.Color, Priority = source.Priority
    };
}
