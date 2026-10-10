using System.Windows;
using PCActivityTracker.Domain;
using PCActivityTracker.Services;

namespace PCActivityTracker.Views;

public partial class PrivacyOnboardingWindow : Window
{
    private readonly AppSettings _existing;

    public PrivacyOnboardingWindow(AppSettings existing)
    {
        InitializeComponent();
        _existing = existing;
        if (existing.PrivacyConsentVersion > 0)
        {
            RecordingCheck.IsChecked = existing.RecordingEnabled;
            UrlsCheck.IsChecked = existing.RecordUrls;
            PageTitlesCheck.IsChecked = existing.RecordPageTitles;
            WindowTitlesCheck.IsChecked = existing.RecordWindowTitles;
            CreatorsCheck.IsChecked = false; // A new field requires a new opt-in.
            ConsentUpdateNotice.Text = "公開チャンネルID・ハンドル等の任意保存を追加しました。配信者情報の記録を再選択してください。既存履歴は削除しません。";
            ConsentUpdateNotice.Visibility = Visibility.Visible;
        }
        RetentionText.Text = Math.Clamp(existing.RetentionDays, 1, 3650).ToString();
        ThemeService.ApplyWindowTheme(this, ThemeService.Apply(existing.ThemeMode));
    }

    public AppSettings? Result { get; private set; }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RetentionText.Text, out var retention) || retention is < 1 or > 3650)
        {
            MessageBox.Show("保存期間は1〜3650日の整数で入力してください。", "初期設定", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Result = BuildResult(RecordingCheck.IsChecked == true, retention);
        DialogResult = true;
    }

    private void ContinueWithoutRecording_Click(object sender, RoutedEventArgs e)
    {
        Result = BuildResult(false, Math.Clamp(_existing.RetentionDays, 1, 3650));
        DialogResult = true;
    }

    private AppSettings BuildResult(bool recording, int retention) => new()
    {
        PrivacyConsentVersion = AppSettings.CurrentPrivacyConsentVersion,
        RecordingEnabled = recording,
        RecordUrls = recording && UrlsCheck.IsChecked == true,
        RecordPageTitles = recording && PageTitlesCheck.IsChecked == true,
        RecordWindowTitles = recording && WindowTitlesCheck.IsChecked == true,
        RecordCreatorNames = recording && CreatorsCheck.IsChecked == true,
        IdleThresholdMinutes = _existing.IdleThresholdMinutes,
        RetentionDays = retention,
        StartWithWindows = _existing.StartWithWindows,
        StartMinimized = _existing.StartMinimized,
        AutomaticBackupsEnabled = _existing.AutomaticBackupsEnabled,
        OllamaEndpoint = _existing.OllamaEndpoint,
        OllamaModel = _existing.OllamaModel,
        UnloadOllamaAfterGeneration = _existing.UnloadOllamaAfterGeneration,
        OllamaTimeoutMinutes = _existing.OllamaTimeoutMinutes,
        GameSafeModeEnabled = _existing.GameSafeModeEnabled,
        RequireIdleOllama = _existing.RequireIdleOllama,
        GameProcessNames = _existing.GameProcessNames,
        ThemeMode = _existing.ThemeMode,
        BrowserBridgeToken = _existing.BrowserBridgeToken,
        BrowserBridgeAllowedOrigins = _existing.BrowserBridgeAllowedOrigins
    };
}
