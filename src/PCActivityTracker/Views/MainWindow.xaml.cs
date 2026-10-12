using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using PCActivityTracker.Services;
using PCActivityTracker.ViewModels;

namespace PCActivityTracker.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += async (_, _) =>
        {
            ThemeService.ApplyWindowTheme(this, _viewModel.EffectiveTheme);
            await _viewModel.InitializeAsync();
        };
        IsVisibleChanged += (_, _) =>
        {
            _viewModel.SetUiActive(IsVisible && IsActive && WindowState != WindowState.Minimized);
            if (!IsVisible) _viewModel.CancelAiReportCommand.Execute(null);
        };
        Activated += (_, _) => _viewModel.SetUiActive(true);
        Deactivated += (_, _) => _viewModel.SetUiActive(false);
        StateChanged += Window_StateChanged;
        Closed += Window_Closed;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _viewModel.SetUiActive(false);
        _viewModel.CancelAiReportCommand.Execute(null);
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && !App.IsExitRequested)
        {
            _viewModel.SetUiActive(false);
            _viewModel.CancelAiReportCommand.Execute(null);
            Dispatcher.BeginInvoke(Close);
            return;
        }

        _viewModel.SetUiActive(IsVisible && IsActive);
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _viewModel.SetUiActive(false);
        _viewModel.ReleaseUiData();

        // Break bindings and release the complete dashboard visual tree. A fresh window
        // is created only when the user opens the app again from the notification area.
        DataContext = null;
        Content = null;
    }

    private void CopyToken_Click(object sender, RoutedEventArgs e)
    {
        try { System.Windows.Clipboard.SetText(_viewModel.BrowserBridgeToken); }
        catch (Exception exception) { _viewModel.ReportUiError("UI.CopyToken", exception); }
    }

    private void ResetBrowserPairing_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            "登録済みのブラウザ拡張IDを解除し、連携トークンを再発行します。すべてのブラウザで再設定が必要です。続行しますか？",
            "ブラウザ連携のリセット", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        try { _viewModel.ResetBrowserPairing(); }
        catch (Exception exception) { _viewModel.ReportUiError("UI.ResetBrowserPairing", exception); }
    }

    private void RegisterBrowserExtension_Click(object sender, RoutedEventArgs e)
    {
        try { _viewModel.RegisterBrowserExtension(); }
        catch (Exception exception) { _viewModel.ReportUiError("UI.RegisterBrowserExtension", exception); }
    }

    private void CopyAiReport_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_viewModel.AiReportContent)) return;
        try { System.Windows.Clipboard.SetText(_viewModel.AiReportContent); }
        catch (Exception exception) { _viewModel.ReportUiError("UI.CopyAiReport", exception); }
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        var directory = System.IO.Path.GetDirectoryName(_viewModel.DatabasePath);
        try
        {
            if (directory is not null) Process.Start(new ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true });
        }
        catch (Exception exception) { _viewModel.ReportUiError("UI.OpenDataFolder", exception); }
    }

    private async void DeleteAll_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            "Activity、AIレポート、すべてのバックアップ、診断ログを完全に削除します。元に戻せません。\n\n続行しますか？",
            "履歴の完全削除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        try { await _viewModel.DeleteAllDataAsync(); }
        catch (Exception exception) { _viewModel.ReportUiError("UI.DeleteAll", exception); }
    }

    private async void CreateBackup_Click(object sender, RoutedEventArgs e)
    {
        try { await _viewModel.CreateBackupAsync(); }
        catch (Exception exception) { _viewModel.ReportUiError("UI.CreateBackup", exception); }
    }

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "匿名化した診断情報を保存",
            Filter = "ZIPファイル (*.zip)|*.zip",
            FileName = $"PCActivityTracker-Diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            AddExtension = true,
            DefaultExt = ".zip"
        };
        if (dialog.ShowDialog(this) != true) return;
        try { await _viewModel.ExportDiagnosticsAsync(dialog.FileName); }
        catch (Exception exception) { _viewModel.ReportUiError("UI.ExportDiagnostics", exception); }
    }

    private void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: BackupItem item }) return;
        var result = System.Windows.MessageBox.Show(
            $"現在の履歴を次のバックアップで置き換えます。\n\n{item.Name}\n\n現在のDBも復元直前バックアップとして保存され、アプリは再起動します。続行しますか？",
            "バックアップの復元", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        try
        {
            _viewModel.PrepareRestore(item);
            App.RequestRestore();
        }
        catch (Exception exception) { _viewModel.ReportUiError("UI.RestoreBackup", exception); }
    }

    private async void Calendar_SelectedDatesChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var selectedFromPopup = PeriodDatePopup.IsOpen;
        PeriodDateButton.IsChecked = false;
        PeriodDateButton.Focus();
        if (IsLoaded && selectedFromPopup) await _viewModel.RefreshCurrentPageAsync();
    }

    private void PeriodDatePopup_Opened(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
            new Action(() => PeriodCalendar.Focus()));

    private void Calendar_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape) return;
        PeriodDateButton.IsChecked = false;
        PeriodDateButton.Focus();
        e.Handled = true;
    }
}
