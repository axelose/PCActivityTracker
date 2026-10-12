using System.Collections.ObjectModel;
using System.Windows.Threading;
using LocalAi.Runtime;
using PCActivityTracker.Browser;
using PCActivityTracker.Data;
using PCActivityTracker.Domain;
using PCActivityTracker.Services;
using PCActivityTracker.Tracking;

namespace PCActivityTracker.ViewModels;

// Screen-specific behavior; the public binding surface remains compatible.
public sealed partial class MainViewModel
{
    private void LoadSettings()
    {
        var settings = _settingsService.Current;
        RecordingEnabled = settings.RecordingEnabled;
        RecordUrls = settings.RecordUrls;
        RecordPageTitles = settings.RecordPageTitles;
        RecordWindowTitles = settings.RecordWindowTitles;
        RecordCreatorNames = settings.RecordCreatorNames;
        IdleThresholdMinutes = settings.IdleThresholdMinutes;
        RetentionDays = settings.RetentionDays;
        StartWithWindows = settings.StartWithWindows;
        StartMinimized = settings.StartMinimized;
        AutomaticBackupsEnabled = settings.AutomaticBackupsEnabled;
        OllamaEndpoint = settings.OllamaEndpoint;
        OllamaModel = settings.OllamaModel;
        UnloadOllamaAfterGeneration = settings.UnloadOllamaAfterGeneration;
        OllamaTimeoutMinutes = settings.OllamaTimeoutMinutes;
        GameSafeModeEnabled = settings.GameSafeModeEnabled;
        RequireIdleOllama = settings.RequireIdleOllama;
        GameProcessNames = settings.GameProcessNames;
        ThemeMode = settings.ThemeMode;
        // ThemeMode may already be "System" (the field default), so synchronize the
        // effective OS theme even when the property setter has no value change.
        EffectiveTheme = ThemeService.Apply(settings.ThemeMode);
    }

    private async Task SaveSettingsAsync()
    {
        await _autoStartService.ApplyAsync(StartWithWindows);
        _settingsService.Save(new AppSettings
        {
            PrivacyConsentVersion = AppSettings.CurrentPrivacyConsentVersion,
            RecordingEnabled = RecordingEnabled,
            RecordUrls = RecordUrls,
            RecordPageTitles = RecordPageTitles,
            RecordWindowTitles = RecordWindowTitles,
            RecordCreatorNames = RecordCreatorNames,
            IdleThresholdMinutes = IdleThresholdMinutes,
            RetentionDays = RetentionDays,
            StartWithWindows = StartWithWindows,
            StartMinimized = StartMinimized,
            AutomaticBackupsEnabled = AutomaticBackupsEnabled,
            OllamaEndpoint = OllamaEndpoint,
            OllamaModel = OllamaModel,
            UnloadOllamaAfterGeneration = UnloadOllamaAfterGeneration,
            OllamaTimeoutMinutes = OllamaTimeoutMinutes,
            GameSafeModeEnabled = GameSafeModeEnabled,
            RequireIdleOllama = RequireIdleOllama,
            GameProcessNames = GameProcessNames,
            ThemeMode = ThemeMode,
            BrowserBridgeToken = _settingsService.Current.BrowserBridgeToken,
            BrowserBridgeAllowedOrigins = _settingsService.Current.BrowserBridgeAllowedOrigins
        });
        StatusMessage = "設定を保存しました";
        Raise(nameof(BrowserCreatorStatus));
    }

    private void AddExclusion(ExclusionType type, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        _settingsService.AddExclusion(type, value);
        if (type == ExclusionType.Application) NewAppExclusion = string.Empty; else NewDomainExclusion = string.Empty;
        LoadExclusions();
        StatusMessage = "除外ルールを追加しました";
    }

    private void LoadExclusions() => Replace(Exclusions, _settingsService.Exclusions.Select(rule =>
        new ExclusionItem(rule.Id, rule.Type == ExclusionType.Application ? "アプリ" : "ドメイン", rule.Value)));

    private void AddCategoryRule()
    {
        if (string.IsNullOrWhiteSpace(NewCategoryRulePattern) || string.IsNullOrWhiteSpace(NewCategoryRuleCategory)) return;
        var target = NewCategoryRuleTarget switch
        {
            "アプリ" => CategoryRuleTarget.Application,
            "配信者・チャンネル" => CategoryRuleTarget.Creator,
            "プロセス" => CategoryRuleTarget.ProcessName,
            "チャンネルID" => CategoryRuleTarget.CreatorId,
            _ => CategoryRuleTarget.Domain
        };
        var mode = NewCategoryRuleMatchMode switch { "正規表現" => CategoryMatchMode.Regex, "完全一致" => CategoryMatchMode.Exact, _ => CategoryMatchMode.Wildcard };
        _settingsService.AddCategoryRule(target, NewCategoryRulePattern, NewCategoryRuleCategory,
            mode, NewCategoryRuleColor, NewCategoryRulePriority);
        NewCategoryRulePattern = string.Empty;
        CategoryRulePreview = "保存しました。次の条件を入力してプレビューできます";
        LoadCategoryRules();
        _ = RefreshCurrentPageAsync();
        StatusMessage = "分類ルールを保存しました（過去の集計にも反映されます）";
    }

    private void LoadCategoryRules() => Replace(CategoryRules, _settingsService.CategoryRules.Select(rule =>
        new CategoryRuleItem(rule.Id, rule.Target switch
        {
            CategoryRuleTarget.Application => "アプリ",
            CategoryRuleTarget.Creator => "配信者・チャンネル",
            CategoryRuleTarget.ProcessName => "プロセス",
            CategoryRuleTarget.CreatorId => "チャンネルID",
            _ => "ドメイン"
        }, rule.Pattern, rule.Category,
            rule.MatchMode switch { CategoryMatchMode.Regex => "正規表現", CategoryMatchMode.Exact => "完全一致", _ => "ワイルドカード" },
            rule.Color, rule.Priority)));

    private async Task PreviewCategoryRuleAsync()
    {
        if (string.IsNullOrWhiteSpace(NewCategoryRulePattern))
        {
            CategoryRulePreview = "分類パターンを入力してください";
            return;
        }
        var target = NewCategoryRuleTarget switch
        {
            "アプリ" => CategoryRuleTarget.Application,
            "配信者・チャンネル" => CategoryRuleTarget.Creator,
            "プロセス" => CategoryRuleTarget.ProcessName,
            "チャンネルID" => CategoryRuleTarget.CreatorId,
            _ => CategoryRuleTarget.Domain
        };
        var candidate = new CategoryRule
        {
            Target = target,
            Pattern = NewCategoryRulePattern.Trim(),
            Category = SettingsService.NormalizeCategoryPath(NewCategoryRuleCategory),
            MatchMode = NewCategoryRuleMatchMode switch { "正規表現" => CategoryMatchMode.Regex, "完全一致" => CategoryMatchMode.Exact, _ => CategoryMatchMode.Wildcard },
            Color = SettingsService.NormalizeColor(NewCategoryRuleColor),
            Priority = Math.Clamp(NewCategoryRulePriority, 0, 1000)
        };
        if (candidate.MatchMode == CategoryMatchMode.Regex)
        {
            try
            {
                _ = System.Text.RegularExpressions.Regex.IsMatch(string.Empty, candidate.Pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                    TimeSpan.FromMilliseconds(50));
            }
            catch (ArgumentException)
            {
                CategoryRulePreview = "正規表現の構文が正しくありません";
                return;
            }
        }
        var (start, end) = GetRange("Analysis", SelectedDate);
        var preview = await Task.Run(() => _explorerService.PreviewRule(candidate, start, end));
        var examples = preview.Examples.Count == 0 ? "該当例なし" : string.Join("、", preview.Examples);
        CategoryRulePreview = $"今週: {preview.SessionCount:N0}セッション・{FormatDuration(preview.Seconds)}に該当" +
                              $"／既存ルールと競合 {preview.ConflictCount:N0}件／例: {examples}";
    }

}
