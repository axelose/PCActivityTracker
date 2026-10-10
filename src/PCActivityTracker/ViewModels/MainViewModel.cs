using System.Collections.ObjectModel;
using System.Windows.Threading;
using LocalAi.Runtime;
using PCActivityTracker.Browser;
using PCActivityTracker.Data;
using PCActivityTracker.Domain;
using PCActivityTracker.Services;
using PCActivityTracker.Tracking;

namespace PCActivityTracker.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AggregationService _aggregation;
    private readonly ActivityRepository _activityRepository;
    private readonly ActivityAnnotationRepository _annotationRepository;
    private readonly ActivityExplorerService _explorerService;
    private readonly SettingsService _settingsService;
    private readonly ActivityTrackerService _tracker;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _bridgeTimer;
    private readonly Func<BrowserBridgeDiagnostics> _bridgeDiagnosticsProvider;
    private readonly BackupService _backupService;
    private readonly AutoStartService _autoStartService;
    private readonly AiReportService _aiReportService;
    private readonly GameSafetyService _gameSafetyService;
    private readonly NativeMessagingRegistrationService _nativeMessagingRegistration;
    private readonly DiagnosticExportService _diagnosticExport;
    private string _selectedPage = "Dashboard";
    private DateTime _selectedDate = DateTime.Today;
    private string _todayPcTime = "0分";
    private string _idleTime = "0分";
    private string _topApp = "—";
    private string _topSite = "—";
    private string _periodTotal = "0分";
    private string _periodLabel = string.Empty;
    private bool _recordingEnabled;
    private bool _recordUrls;
    private bool _recordPageTitles;
    private bool _recordWindowTitles;
    private bool _recordCreatorNames;
    private int _idleThresholdMinutes;
    private int _retentionDays;
    private bool _startWithWindows;
    private bool _startMinimized;
    private bool _automaticBackupsEnabled;
    private string _ollamaEndpoint = "http://127.0.0.1:11434";
    private string _ollamaModel = string.Empty;
    private bool _unloadOllamaAfterGeneration = true;
    private int _ollamaTimeoutMinutes = 10;
    private bool _gameSafeModeEnabled = true;
    private bool _requireIdleOllama = true;
    private string _gameProcessNames = AppSettings.DefaultGameProcessNames;
    private string _themeMode = "System";
    private string _effectiveTheme = "Light";
    private string _gameSafetyStatus = "ゲームセーフモード監視中";
    private string _ollamaConnectionStatus = "未確認";
    private bool _isWeeklyAiReport;
    private bool _isAiGenerating;
    private bool _hasAiReport;
    private string _aiReportContent = string.Empty;
    private string _aiReportMetadata = "まだ生成されていません";
    private string _aiReportStatus = "集計データだけをローカルOllamaへ渡します";
    private string _aiEvidenceSummary = "根拠データを読み込んでいます";
    private string _aiFreshnessStatus = string.Empty;
    private bool _isAiReportStale;
    private string? _gameCancellationProcess;
    private string _newAppExclusion = string.Empty;
    private string _newDomainExclusion = string.Empty;
    private string _statusMessage = string.Empty;
    private string _bridgeStatus = string.Empty;
    private string _trackingStatus = string.Empty;
    private string _browserConnectionState = string.Empty;
    private string _browserLastReceived = "未受信";
    private string _browserLastActivity = "—";
    private string _browserCreatorStatus = "配信者・チャンネル情報は未受信です";
    private string _browserExtensionVersion = "未受信";
    private string _browserExtensionStatus = string.Empty;
    private string _browserRequestCounts = string.Empty;
    private string _browserBridgeToken = string.Empty;
    private string _browserPairingStatus = "未ペアリング";
    private string _browserExtensionId = string.Empty;
    private string _focusTime = "0分";
    private string _focusDetail = "25分以上の集中ブロックはありません";
    private string _weekComparison = "比較データなし";
    private string _weekComparisonDetail = string.Empty;
    private string _focusQualityScore = "0";
    private string _focusQualityGrade = "データなし";
    private string _focusQualityDetail = string.Empty;
    private string _contextSwitchCount = "0回";
    private string _switchesPerHour = "0.0回/時";
    private string _averageSessionDuration = "0分";
    private string _shortSessionSummary = "0回";
    private string _analysisComparisonSummary = "比較データなし";
    private string _newCategoryRuleTarget = "ドメイン";
    private string _newCategoryRulePattern = string.Empty;
    private string _newCategoryRuleCategory = "Development";
    private string _newCategoryRuleMatchMode = "ワイルドカード";
    private string _newCategoryRuleColor = "#64748B";
    private int _newCategoryRulePriority = 100;
    private string _categoryRulePreview = "条件を入力して「プレビュー」を押すと、過去データへの影響を確認できます";
    private string _explorerRange = "1日";
    private int _timelineZoomMinutes = 5;
    private string _searchText = string.Empty;
    private string _searchApp = string.Empty;
    private string _searchDomain = string.Empty;
    private string _searchCreator = string.Empty;
    private string _searchCategory = string.Empty;
    private string _searchProject = string.Empty;
    private bool _searchIncludeIdle = true;
    private bool _searchOnlyFocus;
    private bool _searchOnlySwitches;
    private string _explorerStatus = "検索条件を指定してください";
    private bool _hasNoExplorerResults = true;
    private ExplorerTimelineItem? _selectedExplorerItem;
    private long _editingAnnotationId;
    private string _annotationStart = string.Empty;
    private string _annotationEnd = string.Empty;
    private string _annotationProject = string.Empty;
    private string _annotationTask = string.Empty;
    private string _annotationNote = string.Empty;
    private string _annotationCategory = string.Empty;
    private string _averageRecoveryTime = "—";
    private string _recoverySummary = "復帰イベントなし";
    private bool _isLoading;
    private bool _hasNoTimeline = true;
    private bool _hasNoSites = true;
    private bool _hasNoCreators = true;
    private bool _uiActive;
    internal bool UiRefreshRunning => _refreshTimer.IsEnabled || _bridgeTimer.IsEnabled;
    private CancellationTokenSource? _refreshCancellation;
    private CancellationTokenSource? _aiGenerationCancellation;
    private TaskCompletionSource? _aiGenerationStopped;
    private bool _isDeletingData;

    public MainViewModel(AggregationService aggregation, ActivityRepository activityRepository,
        ActivityAnnotationRepository annotationRepository, ActivityExplorerService explorerService,
        SettingsService settingsService, ActivityTrackerService tracker, string databasePath,
        Func<BrowserBridgeDiagnostics> bridgeDiagnosticsProvider, BackupService backupService,
        AutoStartService autoStartService, AiReportService aiReportService,
        GameSafetyService gameSafetyService, NativeMessagingRegistrationService nativeMessagingRegistration,
        DiagnosticExportService diagnosticExport)
    {
        _aggregation = aggregation;
        _activityRepository = activityRepository;
        _annotationRepository = annotationRepository;
        _explorerService = explorerService;
        _settingsService = settingsService;
        _tracker = tracker;
        _bridgeDiagnosticsProvider = bridgeDiagnosticsProvider;
        _backupService = backupService;
        _autoStartService = autoStartService;
        _aiReportService = aiReportService;
        _gameSafetyService = gameSafetyService;
        _nativeMessagingRegistration = nativeMessagingRegistration;
        _diagnosticExport = diagnosticExport;
        DatabasePath = databasePath;
        ApplyBridgeDiagnostics(bridgeDiagnosticsProvider());
        TrackingStatus = tracker.DisplayStatus;
        BrowserBridgeToken = settingsService.Current.BrowserBridgeToken;

        NavigateCommand = new AsyncRelayCommand(async parameter =>
        {
            SelectedPage = parameter?.ToString() ?? "Dashboard";
            if (SelectedPage is "Today" or "Dashboard") SelectedDate = DateTime.Today;
            await RefreshCurrentPageAsync();
        }, HandleCommandError);
        PreviousCommand = new AsyncRelayCommand(async _ => { MovePeriod(-1); await RefreshCurrentPageAsync(); }, HandleCommandError);
        NextCommand = new AsyncRelayCommand(async _ => { MovePeriod(1); await RefreshCurrentPageAsync(); }, HandleCommandError);
        TodayCommand = new AsyncRelayCommand(async _ => { SelectedDate = DateTime.Today; await RefreshCurrentPageAsync(); }, HandleCommandError);
        RefreshCommand = new AsyncRelayCommand(async _ => await RefreshCurrentPageAsync(), HandleCommandError);
        SelectDailyAiReportCommand = new AsyncRelayCommand(async _ =>
        {
            IsWeeklyAiReport = false;
            await LoadAiReportAsync();
        }, HandleCommandError);
        SelectWeeklyAiReportCommand = new AsyncRelayCommand(async _ =>
        {
            IsWeeklyAiReport = true;
            await LoadAiReportAsync();
        }, HandleCommandError);
        GenerateAiReportCommand = new AsyncRelayCommand(async _ => await GenerateAiReportAsync(), HandleCommandError);
        CancelAiReportCommand = new RelayCommand(_ => _aiGenerationCancellation?.Cancel());
        RefreshOllamaCommand = new AsyncRelayCommand(async _ => await RefreshOllamaAsync(), HandleCommandError);
        SelectThemeCommand = new RelayCommand(parameter =>
            ThemeMode = parameter?.ToString() ?? "System", errorHandler: HandleCommandError);
        SaveSettingsCommand = new AsyncRelayCommand(async _ => await SaveSettingsAsync(), HandleCommandError);
        AddAppExclusionCommand = new RelayCommand(_ => AddExclusion(ExclusionType.Application, NewAppExclusion), errorHandler: HandleCommandError);
        AddDomainExclusionCommand = new RelayCommand(_ => AddExclusion(ExclusionType.Domain, NewDomainExclusion), errorHandler: HandleCommandError);
        RemoveExclusionCommand = new RelayCommand(parameter =>
        {
            if (parameter is long id) { _settingsService.RemoveExclusion(id); LoadExclusions(); }
        }, errorHandler: HandleCommandError);
        AddCategoryRuleCommand = new RelayCommand(_ => AddCategoryRule(), errorHandler: HandleCommandError);
        RemoveCategoryRuleCommand = new RelayCommand(parameter =>
        {
            if (parameter is long id)
            {
                _settingsService.RemoveCategoryRule(id);
                LoadCategoryRules();
                _ = RefreshCurrentPageAsync();
                StatusMessage = "分類ルールを削除しました";
            }
        }, errorHandler: HandleCommandError);
        PreviewCategoryRuleCommand = new AsyncRelayCommand(async _ => await PreviewCategoryRuleAsync(), HandleCommandError);
        SearchExplorerCommand = new AsyncRelayCommand(async _ => await SearchExplorerAsync(), HandleCommandError);
        ClearExplorerCommand = new AsyncRelayCommand(async _ =>
        {
            ClearExplorerFilters();
            await SearchExplorerAsync();
        }, HandleCommandError);
        OpenAppDrilldownCommand = new AsyncRelayCommand(async parameter =>
            await OpenExplorerAsync("App", parameter?.ToString()), HandleCommandError);
        OpenSiteDrilldownCommand = new AsyncRelayCommand(async parameter =>
            await OpenExplorerAsync("Domain", parameter?.ToString()), HandleCommandError);
        OpenCreatorDrilldownCommand = new AsyncRelayCommand(async parameter =>
            await OpenExplorerAsync("Creator", parameter is RankingItem item ? item.FilterValue : parameter?.ToString()), HandleCommandError);
        OpenCategoryDrilldownCommand = new AsyncRelayCommand(async parameter =>
            await OpenExplorerAsync("Category", parameter?.ToString()), HandleCommandError);
        OpenFocusDrilldownCommand = new AsyncRelayCommand(async _ =>
            await OpenExplorerAsync("Focus", null), HandleCommandError);
        OpenSwitchDrilldownCommand = new AsyncRelayCommand(async _ =>
            await OpenExplorerAsync("Switch", null), HandleCommandError);
        OpenComparisonDrilldownCommand = new AsyncRelayCommand(async parameter =>
        {
            if (parameter is ComparisonItem item) await OpenExplorerAsync(item.Scope, item.Name);
        }, HandleCommandError);
        SaveAnnotationCommand = new AsyncRelayCommand(async _ => await SaveAnnotationAsync(), HandleCommandError);
        DeleteAnnotationCommand = new AsyncRelayCommand(async _ => await DeleteAnnotationAsync(), HandleCommandError);

        LoadSettings();
        LoadExclusions();
        LoadCategoryRules();
        LoadBackups();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _bridgeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _bridgeTimer.Tick += (_, _) =>
        {
            ApplyBridgeDiagnostics(_bridgeDiagnosticsProvider());
            TrackingStatus = _tracker.DisplayStatus;
            GameSafetyStatus = _gameSafetyService.Evaluate(BuildAiSafetySettings()).Message;
        };
    }

    public async Task InitializeAsync() => await RefreshCurrentPageAsync();

    public void SetUiActive(bool active)
    {
        if (_uiActive == active) return;
        _uiActive = active;
        if (active)
        {
            _refreshTimer.Start();
            _bridgeTimer.Start();
            _ = RefreshCurrentPageAsync();
        }
        else
        {
            _refreshTimer.Stop();
            _bridgeTimer.Stop();
            CancelRefresh();
        }
    }

    public void ReleaseUiData()
    {
        CancelRefresh();
        Timeline.Clear();
        AppRankings.Clear();
        SiteRankings.Clear();
        CreatorRankings.Clear();
        CategoryRankings.Clear();
        DailyUsage.Clear();
        HeatmapDays.Clear();
        AiEvidenceApps.Clear();
        AiEvidenceSites.Clear();
        AiEvidenceCategories.Clear();
        SwitchDestinations.Clear();
        PeriodChanges.Clear();
        ExplorerTimeline.Clear();
        InterruptionSources.Clear();
        FocusHours.Clear();
        FocusDistribution.Clear();
        FocusScoreComponents.Clear();
        SelectedExplorerItem = null;
        HasNoTimeline = true;
        HasNoExplorerResults = true;
        HasNoSites = true;
        HasNoCreators = true;
    }

    public async Task RefreshAsync()
    {
        if (!_uiActive || IsSettings || IsAiReports) return;
        if (IsExplorer)
        {
            await SearchExplorerAsync();
            return;
        }

        var page = SelectedPage;
        var selectedDate = SelectedDate;
        var refresh = new CancellationTokenSource();
        var previous = _refreshCancellation;
        _refreshCancellation = refresh;
        previous?.Cancel();
        IsLoading = true;
        try
        {
            var (start, end) = GetRange(page, selectedDate);
            var includeTimeline = page is "Today" or "Day";
            var result = await Task.Run(() =>
            {
                ActivityReport? report = page == "Analysis" ? null : _aggregation.BuildReport(start, end, includeTimeline);
                DashboardInsights? insights = page == "Dashboard" ? _aggregation.BuildDashboardInsights(DateTimeOffset.Now) : null;
                BehaviorAnalysis? behavior = page == "Analysis" ? _aggregation.BuildBehaviorAnalysis(start, end) : null;
                return (Report: report, Insights: insights, Behavior: behavior);
            }, refresh.Token);
            refresh.Token.ThrowIfCancellationRequested();
            if (!CanApplyRefresh(refresh, page, selectedDate)) return;
            if (result.Report is not null) ApplyReport(result.Report, includeTimeline);
            if (result.Insights is not null)
            {
                ApplyDaily(result.Insights.Daily);
                ApplyInsights(result.Insights);
            }
            if (result.Behavior is not null) ApplyBehaviorAnalysis(result.Behavior);
            StatusMessage = $"更新 {DateTime.Now:HH:mm:ss}";
        }
        catch (OperationCanceledException)
        {
            // A newer page/date request or a hidden window superseded this result.
        }
        catch (Exception exception)
        {
            if (CanApplyRefresh(refresh, page, selectedDate))
                StatusMessage = $"読み込みエラー: {exception.Message}";
        }
        finally
        {
            refresh.Dispose();
            if (ReferenceEquals(_refreshCancellation, refresh))
            {
                _refreshCancellation = null;
                IsLoading = false;
            }
        }
    }

    public Task RefreshCurrentPageAsync() => IsAiReports
        ? LoadAiReportAsync()
        : IsExplorer ? SearchExplorerAsync() : RefreshAsync();

    public async Task DeleteAllDataAsync()
    {
        if (_isDeletingData) return;
        _isDeletingData = true;
        _tracker.SuspendAndDiscard();
        try
        {
            var generationStopped = _aiGenerationStopped?.Task;
            _aiGenerationCancellation?.Cancel();
            if (generationStopped is not null) await generationStopped;
            await Task.Run(() =>
            {
                _activityRepository.DeleteAll();
                _backupService.DeleteAllBackups();
                DiagnosticLogService.DeleteAll();
            });
            LoadBackups();
            await RefreshAsync();
            StatusMessage = "履歴・AIレポート・バックアップ・診断ログを完全削除しました";
        }
        finally
        {
            _tracker.Resume();
            _isDeletingData = false;
        }
    }

    public async Task CreateBackupAsync()
    {
        IsLoading = true;
        try
        {
            var backup = await Task.Run(() => _backupService.CreateBackup());
            LoadBackups();
            StatusMessage = $"バックアップを作成しました: {backup.Name}";
        }
        finally { IsLoading = false; }
    }

    public async Task ExportDiagnosticsAsync(string destinationPath)
    {
        IsLoading = true;
        try
        {
            await Task.Run(() => _diagnosticExport.Export(destinationPath));
            StatusMessage = $"匿名化した診断情報を保存しました: {System.IO.Path.GetFileName(destinationPath)}";
        }
        finally { IsLoading = false; }
    }

    public void PrepareRestore(BackupItem item)
    {
        _tracker.SuspendAndDiscard();
        try
        {
            _backupService.StageRestore(item.FullPath);
            StatusMessage = "復元準備が完了しました";
        }
        catch
        {
            _tracker.Resume();
            throw;
        }
    }

    public void ReportUiError(string operation, Exception exception)
    {
        DiagnosticLogService.Error(operation, exception);
        StatusMessage = $"エラー: {exception.Message}";
    }

    private void HandleCommandError(Exception exception) => ReportUiError("UI.Command", exception);

    private static (DateTimeOffset Start, DateTimeOffset End) GetRange(string page, DateTime selectedDate)
    {
        var day = DateTime.SpecifyKind(selectedDate.Date, DateTimeKind.Local);
        return page switch
        {
            "Week" or "Analysis" => (new DateTimeOffset(StartOfWeek(day)), new DateTimeOffset(StartOfWeek(day).AddDays(7))),
            "Month" => (new DateTimeOffset(new DateTime(day.Year, day.Month, 1, 0, 0, 0, DateTimeKind.Local)), new DateTimeOffset(new DateTime(day.Year, day.Month, 1, 0, 0, 0, DateTimeKind.Local).AddMonths(1))),
            _ => (new DateTimeOffset(day), new DateTimeOffset(day.AddDays(1)))
        };
    }

    private bool CanApplyRefresh(CancellationTokenSource refresh, string page, DateTime selectedDate) =>
        !refresh.IsCancellationRequested && _uiActive && ReferenceEquals(_refreshCancellation, refresh) &&
        SelectedPage == page && SelectedDate == selectedDate;

    private void CancelRefresh() => _refreshCancellation?.Cancel();

    private void MovePeriod(int direction)
    {
        SelectedDate = SelectedPage switch
        {
            "Week" or "Analysis" => SelectedDate.AddDays(7 * direction),
            "Month" => SelectedDate.AddMonths(direction),
            "Explorer" when ExplorerRange == "7日" => SelectedDate.AddDays(7 * direction),
            "Explorer" when ExplorerRange == "1か月" => SelectedDate.AddMonths(direction),
            "Explorer" when ExplorerRange == "90日" => SelectedDate.AddDays(90 * direction),
            "AiReports" when IsWeeklyAiReport => SelectedDate.AddDays(7 * direction),
            _ => SelectedDate.AddDays(direction)
        };
    }

    private void LoadBackups() => Replace(Backups, _backupService.ListBackups().Select(item =>
        new BackupItem(item.Name, item.CreatedAt.LocalDateTime.ToString("yyyy/M/d HH:mm:ss"),
            FormatBytes(item.SizeBytes), item.FullPath, item.IsAutomatic)));

    private void RaisePageFlags()
    {
        Raise(nameof(IsDashboard));
        Raise(nameof(IsTimeline));
        Raise(nameof(IsReport));
        Raise(nameof(IsAnalysis));
        Raise(nameof(IsExplorer));
        Raise(nameof(IsAiReports));
        Raise(nameof(IsSettings));
        Raise(nameof(IsMonth));
        Raise(nameof(DateLabel));
    }

    private static DateTime StartOfWeek(DateTime date)
    {
        var diff = (7 + ((int)date.DayOfWeek - (int)DayOfWeek.Monday)) % 7;
        return date.Date.AddDays(-diff);
    }

    private static void Replace<T>(BulkObservableCollection<T> collection, IEnumerable<T> values) =>
        collection.ReplaceWith(values);

    public static string FormatDuration(long seconds)
    {
        if (seconds < 60) return $"{seconds}秒";
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}時間 {span.Minutes}分" : $"{span.Minutes}分";
    }

    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / (1024d * 1024d):0.0} MB"
        : $"{Math.Max(1, bytes / 1024d):0} KB";

    public void Dispose()
    {
        _refreshTimer.Stop();
        _bridgeTimer.Stop();
        CancelRefresh();
        _aiGenerationCancellation?.Cancel();
        _aiGenerationCancellation?.Dispose();
    }
}
