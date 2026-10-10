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
        Result = BuildResult(false, 90);
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
        StartWithWindows = false,
        StartMinimized = false,
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
