using System.Threading;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using LocalAi.Runtime;
using PCActivityTracker.Browser;
using PCActivityTracker.Data;
using PCActivityTracker.Domain;
using PCActivityTracker.Services;
using PCActivityTracker.Tracking;
using PCActivityTracker.ViewModels;
using PCActivityTracker.Views;

namespace PCActivityTracker;

public partial class App : System.Windows.Application
{
    private static Mutex? _singleInstance;
    private ActivityTrackerService? _tracker;
    private BrowserNativeMessagingServer? _browserBridge;
    private TrayIconService? _tray;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private SqliteDatabase? _database;
    private SessionLockMonitor? _sessionLock;
    private DataMaintenanceService? _maintenance;
    private BackupService? _backupService;
    private DispatcherTimer? _smokeTimer;
    private bool _ownsMutex;
    private string? _dataDirectory;

    public static bool IsExitRequested { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (TryRunRestoreHelper(e.Args, out var restoreExitCode))
        {
            Shutdown(restoreExitCode);
            return;
        }
        if (e.Args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            Shutdown(SelfTestRunner.Run());
            return;
        }

        _singleInstance = new Mutex(true, "Local\\PCActivityTracker.SingleInstance", out var created);
        _ownsMutex = created;
        if (!created)
        {
            System.Windows.MessageBox.Show(
                "PC Activity Tracker は既に起動しています。\n\n" +
                "更新後の画面へ切り替える場合は、通知領域のアイコンを右クリックして「終了」を選び、もう一度起動してください。\n" +
                "ウィンドウの×ボタンだけでは通知領域に残ります。",
                "PC Activity Tracker", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        PerformanceModeService.ApplyLowImpactMode();

        try
        {
            _dataDirectory = Environment.GetEnvironmentVariable("PC_ACTIVITY_TRACKER_DATA_DIR");
            if (string.IsNullOrWhiteSpace(_dataDirectory))
                _dataDirectory = DataPathService.GetDefaultDataDirectory();
            DiagnosticLogService.Initialize(_dataDirectory);
            var databasePath = System.IO.Path.Combine(_dataDirectory, "activity.db");
            _database = new SqliteDatabase(databasePath);
            var database = _database;
            database.Initialize();

            var activities = new ActivityRepository(database);
            var settingsRepository = new SettingsRepository(database);
            var settings = new SettingsService(settingsRepository);
            settings.Load();
            var nativeMessagingRegistration = new NativeMessagingRegistrationService(settings, _dataDirectory);
            try { nativeMessagingRegistration.RefreshRegistration(); }
            catch (Exception exception) { DiagnosticLogService.Error("NativeMessaging.RefreshRegistration", exception); }
            ThemeService.Apply(settings.Current.ThemeMode);
            if (settings.Current.PrivacyConsentVersion < AppSettings.CurrentPrivacyConsentVersion)
            {
                var onboarding = new PrivacyOnboardingWindow(settings.Current);
                if (onboarding.ShowDialog() != true || onboarding.Result is null)
                {
                    IsExitRequested = true;
                    Shutdown();
                    return;
                }
                settings.Save(onboarding.Result);
            }
            var autoStart = new AutoStartService();
            // Packaged startup is owned by the Windows StartupTask API and must not
            // be rewritten on every launch. ZIP/developer builds use the HKCU Run key.
            try
            {
                if (!DataPathService.IsPackaged)
                    autoStart.ApplyAsync(settings.Current.StartWithWindows).GetAwaiter().GetResult();
            }
            catch (Exception exception) { DiagnosticLogService.Error("AutoStart.Reconcile", exception); }
            activities.SanitizeStoredUrls();
            using (var currentProcess = System.Diagnostics.Process.GetCurrentProcess())
                activities.DeleteByProcessName(currentProcess.ProcessName);
            activities.DeleteBefore(DateTimeOffset.Now.AddDays(-settings.Current.RetentionDays));

            var browserState = new BrowserStateStore();
            _browserBridge = new BrowserNativeMessagingServer(browserState, settings);
            var bridgeStatus = string.Empty;
            var bridgeStarted = false;
            try { _browserBridge.Start(); bridgeStatus = _browserBridge.DisplayStatus; bridgeStarted = true; }
            catch (Exception exception) { bridgeStatus = "開始失敗: " + exception.Message; }

            var provider = new ForegroundActivityProvider(new IdleDetector(), browserState);
            _sessionLock = new SessionLockMonitor();
            _tracker = new ActivityTrackerService(provider, activities, settings, _sessionLock);
            _tracker.Start();
            _backupService = new BackupService(database, settings);
            _backupService.ApplyRetention(DateTimeOffset.Now.AddDays(-settings.Current.RetentionDays));
            _backupService.Start();
            _maintenance = new DataMaintenanceService(activities, settings, _backupService);
            _maintenance.Start();

            var annotations = new ActivityAnnotationRepository(database);
            var aggregation = new AggregationService(activities, settings, annotations);
            var explorer = new ActivityExplorerService(activities, annotations, settings);
            var aiReports = new AiReportService(aggregation, activities, new AiReportRepository(database),
                new GpuExecutionCoordinator());
            var gameSafety = new GameSafetyService(_tracker);
            Func<BrowserBridgeDiagnostics> browserDiagnostics = () => bridgeStarted
                ? _browserBridge.GetDiagnostics()
                : new BrowserBridgeDiagnostics(bridgeStatus, "開始失敗", null, null, null, null, null,
                    BrowserNativeMessagingServer.ExpectedExtensionVersion, 0, 0, 0, bridgeStatus);
            var diagnosticExport = new DiagnosticExportService(database, settings, browserDiagnostics);
            _viewModel = new MainViewModel(aggregation, activities, annotations, explorer, settings, _tracker, databasePath,
                browserDiagnostics,
                _backupService, autoStart, aiReports, gameSafety, nativeMessagingRegistration, diagnosticExport);
            if (e.Args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
            {
                _smokeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                _smokeTimer.Tick += (_, _) => { _smokeTimer?.Stop(); RequestExit(); };
                _smokeTimer.Start();
            }
            else
            {
                _tray = new TrayIconService(ShowMainWindow);
                var background = e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase) ||
                                 settings.Current.StartMinimized;
                if (!background) ShowMainWindow();
                else
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                        new Action(PerformanceModeService.ReleaseUnusedUiMemory));
                }
            }
        }
        catch (Exception exception)
        {
            DiagnosticLogService.Error("App.Startup", exception);
            System.Windows.MessageBox.Show($"起動できませんでした。\n\n{exception}", "PC Activity Tracker", MessageBoxButton.OK, MessageBoxImage.Error);
            IsExitRequested = true;
            Shutdown(1);
        }
    }

    private void ShowMainWindow()
    {
        if (_viewModel is null || IsExitRequested) return;

        if (_window is not null)
        {
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Show();
            _window.Activate();
            return;
        }

        var window = new MainWindow(_viewModel);
        _window = window;
        MainWindow = window;
        window.Closed += MainWindow_Closed;
        window.Show();
        window.Activate();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        if (sender is not MainWindow closedWindow || !ReferenceEquals(_window, closedWindow)) return;

        closedWindow.Closed -= MainWindow_Closed;
        _window = null;
        if (ReferenceEquals(MainWindow, closedWindow)) MainWindow = null;

        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            // If the user immediately reopened the dashboard, do not trim its new pages.
            if (_window is null) PerformanceModeService.ReleaseUnusedUiMemory();
        }));
    }

    public static void RequestExit()
    {
        IsExitRequested = true;
        if (Current.MainWindow is not null) Current.MainWindow.Close();
        Current.Shutdown();
    }

    public static void RequestRestore()
    {
        if (Current is not App app || string.IsNullOrWhiteSpace(app._dataDirectory) || Environment.ProcessPath is null)
            throw new InvalidOperationException("復元ヘルパーを開始できませんでした。");

        var startInfo = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false };
        startInfo.ArgumentList.Add("--restore-helper");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        startInfo.ArgumentList.Add(app._dataDirectory);
        _ = Process.Start(startInfo) ?? throw new InvalidOperationException("復元ヘルパーを開始できませんでした。");
        RequestExit();
    }

    private static bool TryRunRestoreHelper(IReadOnlyList<string> args, out int exitCode)
    {
        exitCode = 0;
        var marker = Array.FindIndex(args.ToArray(), value => value.Equals("--restore-helper", StringComparison.OrdinalIgnoreCase));
        if (marker < 0) return false;
        try
        {
            if (marker + 2 >= args.Count || !int.TryParse(args[marker + 1], out var parentId))
                throw new InvalidOperationException("復元ヘルパーの引数が不正です。");
            var dataDirectory = Path.GetFullPath(args[marker + 2]);
            try
            {
                using var parent = Process.GetProcessById(parentId);
                if (!parent.WaitForExit(20000)) throw new TimeoutException("アプリの終了待機がタイムアウトしました。");
            }
            catch (ArgumentException) { /* The parent already exited. */ }

            BackupService.ApplyPendingRestore(dataDirectory);
            if (Environment.ProcessPath is not null)
                Process.Start(new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false });
        }
        catch (Exception exception)
        {
            exitCode = 1;
            try
            {
                var dataDirectory = marker + 2 < args.Count ? Path.GetFullPath(args[marker + 2]) : AppContext.BaseDirectory;
                Directory.CreateDirectory(dataDirectory);
                File.WriteAllText(Path.Combine(dataDirectory, "restore-error.log"),
                    $"{DateTimeOffset.Now:O} Restore failed: {exception.GetType().Name} (0x{exception.HResult:X8})");
            }
            catch { }
        }
        return true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _viewModel?.Dispose();
        if (_backupService is not null) _backupService.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (_maintenance is not null) _maintenance.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (_tracker is not null) _tracker.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (_browserBridge is not null) _browserBridge.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _sessionLock?.Dispose();
        _database?.Dispose();
        if (_ownsMutex) _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
