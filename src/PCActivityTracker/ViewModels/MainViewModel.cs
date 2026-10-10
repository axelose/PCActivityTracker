using System.Collections.ObjectModel;
using System.Windows.Threading;
using LocalAi.Runtime;
using PCActivityTracker.Browser;
using PCActivityTracker.Data;
using PCActivityTracker.Domain;
using PCActivityTracker.Services;
using PCActivityTracker.Tracking;

namespace PCActivityTracker.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
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
    private bool _uiActive = true;
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
            await OpenExplorerAsync("Creator", parameter?.ToString()), HandleCommandError);
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
        _refreshTimer.Start();
        _bridgeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _bridgeTimer.Tick += (_, _) =>
        {
            ApplyBridgeDiagnostics(_bridgeDiagnosticsProvider());
            TrackingStatus = _tracker.DisplayStatus;
            GameSafetyStatus = _gameSafetyService.Evaluate(BuildAiSafetySettings()).Message;
        };
        _bridgeTimer.Start();
    }

    public BulkObservableCollection<TimelineItem> Timeline { get; } = new();
    public BulkObservableCollection<RankingItem> AppRankings { get; } = new();
    public BulkObservableCollection<RankingItem> SiteRankings { get; } = new();
    public BulkObservableCollection<RankingItem> CreatorRankings { get; } = new();
    public BulkObservableCollection<RankingItem> CategoryRankings { get; } = new();
    public BulkObservableCollection<DailyItem> DailyUsage { get; } = new();
    public BulkObservableCollection<ExclusionItem> Exclusions { get; } = new();
    public BulkObservableCollection<HeatmapDayItem> HeatmapDays { get; } = new();
    public BulkObservableCollection<BackupItem> Backups { get; } = new();
    public BulkObservableCollection<string> OllamaModels { get; } = new();
    public BulkObservableCollection<RankingItem> AiEvidenceApps { get; } = new();
    public BulkObservableCollection<RankingItem> AiEvidenceSites { get; } = new();
    public BulkObservableCollection<RankingItem> AiEvidenceCategories { get; } = new();
    public BulkObservableCollection<AnalysisMetricItem> SwitchDestinations { get; } = new();
    public BulkObservableCollection<ComparisonItem> PeriodChanges { get; } = new();
    public BulkObservableCollection<CategoryRuleItem> CategoryRules { get; } = new();
    public BulkObservableCollection<ExplorerTimelineItem> ExplorerTimeline { get; } = new();
    public BulkObservableCollection<AnalysisMetricItem> InterruptionSources { get; } = new();
    public BulkObservableCollection<AnalysisMetricItem> FocusHours { get; } = new();
    public BulkObservableCollection<AnalysisMetricItem> FocusDistribution { get; } = new();
    public BulkObservableCollection<AnalysisMetricItem> FocusScoreComponents { get; } = new();
    public IReadOnlyList<string> CategoryRuleTargets { get; } = new[] { "アプリ", "ドメイン", "配信者・チャンネル" };
    public IReadOnlyList<string> CategoryRuleMatchModes { get; } = new[] { "ワイルドカード", "正規表現" };
    public IReadOnlyList<string> ExplorerRanges { get; } = new[] { "1日", "7日", "1か月", "90日" };
    public IReadOnlyList<int> TimelineZoomChoices { get; } = new[] { 1, 5, 10, 15, 30, 60 };
    public IReadOnlyList<string> CategoryChoices { get; } = new[]
    {
        "Development", "Productivity", "Research", "Communication", "Entertainment", "System", "Other"
    };

    public AsyncRelayCommand NavigateCommand { get; }
    public AsyncRelayCommand PreviousCommand { get; }
    public AsyncRelayCommand NextCommand { get; }
    public AsyncRelayCommand TodayCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand SelectDailyAiReportCommand { get; }
    public AsyncRelayCommand SelectWeeklyAiReportCommand { get; }
    public AsyncRelayCommand GenerateAiReportCommand { get; }
    public RelayCommand CancelAiReportCommand { get; }
    public AsyncRelayCommand RefreshOllamaCommand { get; }
    public RelayCommand SelectThemeCommand { get; }
    public AsyncRelayCommand SaveSettingsCommand { get; }
    public RelayCommand AddAppExclusionCommand { get; }
    public RelayCommand AddDomainExclusionCommand { get; }
    public RelayCommand RemoveExclusionCommand { get; }
    public RelayCommand AddCategoryRuleCommand { get; }
    public RelayCommand RemoveCategoryRuleCommand { get; }
    public AsyncRelayCommand PreviewCategoryRuleCommand { get; }
    public AsyncRelayCommand SearchExplorerCommand { get; }
    public AsyncRelayCommand ClearExplorerCommand { get; }
    public AsyncRelayCommand OpenAppDrilldownCommand { get; }
    public AsyncRelayCommand OpenSiteDrilldownCommand { get; }
    public AsyncRelayCommand OpenCreatorDrilldownCommand { get; }
    public AsyncRelayCommand OpenCategoryDrilldownCommand { get; }
    public AsyncRelayCommand OpenFocusDrilldownCommand { get; }
    public AsyncRelayCommand OpenSwitchDrilldownCommand { get; }
    public AsyncRelayCommand OpenComparisonDrilldownCommand { get; }
    public AsyncRelayCommand SaveAnnotationCommand { get; }
    public AsyncRelayCommand DeleteAnnotationCommand { get; }

    public string SelectedPage
    {
        get => _selectedPage;
        private set
        {
            if (!Set(ref _selectedPage, value)) return;
            CancelRefresh();
            if (value != "AiReports") _aiGenerationCancellation?.Cancel();
            RaisePageFlags();
            Raise(nameof(PageTitle));
            Raise(nameof(ShowPeriodNavigation));
        }
    }

    public DateTime SelectedDate
    {
        get => _selectedDate;
        set
        {
            if (!Set(ref _selectedDate, value)) return;
            CancelRefresh();
            _aiGenerationCancellation?.Cancel();
            Raise(nameof(DateLabel));
            Raise(nameof(AiPeriodLabel));
        }
    }
    public string PageTitle => SelectedPage switch
    {
        "Dashboard" => "ダッシュボード",
        "Today" => "今日のタイムライン",
        "Day" => "日別履歴",
        "Week" => "週次レポート",
        "Month" => "月次レポート",
        "Analysis" => "行動分析",
        "Explorer" => "分析エクスプローラー",
        "AiReports" => "AI日報・週報",
        _ => "設定"
    };
    public string DateLabel => SelectedPage switch
    {
        "Week" or "Analysis" => $"{StartOfWeek(SelectedDate):yyyy年M月d日} — {StartOfWeek(SelectedDate).AddDays(6):M月d日}",
        "Explorer" => GetExplorerDateLabel(),
        "Month" => $"{SelectedDate:yyyy年M月}",
        "AiReports" when IsWeeklyAiReport => $"{StartOfWeek(SelectedDate):yyyy年M月d日} — {StartOfWeek(SelectedDate).AddDays(6):M月d日}",
        "Settings" => string.Empty,
        _ => $"{SelectedDate:yyyy年M月d日 (ddd)}"
    };
    public bool ShowPeriodNavigation => SelectedPage != "Settings";
    public bool IsDashboard => SelectedPage == "Dashboard";
    public bool IsTimeline => SelectedPage is "Today" or "Day";
    public bool IsReport => SelectedPage is "Week" or "Month";
    public bool IsAnalysis => SelectedPage == "Analysis";
    public bool IsExplorer => SelectedPage == "Explorer";
    public bool IsAiReports => SelectedPage == "AiReports";
    public bool IsSettings => SelectedPage == "Settings";
    public bool IsMonth => SelectedPage == "Month";

    public string TodayPcTime { get => _todayPcTime; private set => Set(ref _todayPcTime, value); }
    public string IdleTime { get => _idleTime; private set => Set(ref _idleTime, value); }
    public string TopApp { get => _topApp; private set => Set(ref _topApp, value); }
    public string TopSite { get => _topSite; private set => Set(ref _topSite, value); }
    public string PeriodTotal { get => _periodTotal; private set => Set(ref _periodTotal, value); }
    public string PeriodLabel { get => _periodLabel; private set => Set(ref _periodLabel, value); }
    public string DatabasePath { get; }
    public string DatabaseProtectionStatus => _diagnosticExport.DatabaseEncrypted
        ? "SQLCipherでDB全体を暗号化（鍵はWindows DPAPI・現在のユーザー専用）"
        : "DB暗号化なし";
    public string AppVersion => $"v{typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "unknown"}";
    public string BridgeStatus { get => _bridgeStatus; private set => Set(ref _bridgeStatus, value); }
    public string TrackingStatus { get => _trackingStatus; private set => Set(ref _trackingStatus, value); }
    public string BrowserBridgeToken { get => _browserBridgeToken; private set => Set(ref _browserBridgeToken, value); }
    public string BrowserPairingStatus { get => _browserPairingStatus; private set => Set(ref _browserPairingStatus, value); }
    public string BrowserExtensionId { get => _browserExtensionId; set => Set(ref _browserExtensionId, value); }
    public string DiagnosticLogPath => DiagnosticLogService.Path;
    public string BackupDirectory => _backupService.BackupDirectory;
    public string BrowserConnectionState { get => _browserConnectionState; private set => Set(ref _browserConnectionState, value); }
    public string BrowserLastReceived { get => _browserLastReceived; private set => Set(ref _browserLastReceived, value); }
    public string BrowserLastActivity { get => _browserLastActivity; private set => Set(ref _browserLastActivity, value); }
    public string BrowserExtensionVersion { get => _browserExtensionVersion; private set => Set(ref _browserExtensionVersion, value); }
    public string BrowserExtensionStatus { get => _browserExtensionStatus; private set => Set(ref _browserExtensionStatus, value); }
    public string BrowserRequestCounts { get => _browserRequestCounts; private set => Set(ref _browserRequestCounts, value); }
    public string FocusTime { get => _focusTime; private set => Set(ref _focusTime, value); }
    public string FocusDetail { get => _focusDetail; private set => Set(ref _focusDetail, value); }
    public string WeekComparison { get => _weekComparison; private set => Set(ref _weekComparison, value); }
    public string WeekComparisonDetail { get => _weekComparisonDetail; private set => Set(ref _weekComparisonDetail, value); }
    public string FocusQualityScore { get => _focusQualityScore; private set => Set(ref _focusQualityScore, value); }
    public string FocusQualityGrade { get => _focusQualityGrade; private set => Set(ref _focusQualityGrade, value); }
    public string FocusQualityDetail { get => _focusQualityDetail; private set => Set(ref _focusQualityDetail, value); }
    public string ContextSwitchCount { get => _contextSwitchCount; private set => Set(ref _contextSwitchCount, value); }
    public string SwitchesPerHour { get => _switchesPerHour; private set => Set(ref _switchesPerHour, value); }
    public string AverageSessionDuration { get => _averageSessionDuration; private set => Set(ref _averageSessionDuration, value); }
    public string ShortSessionSummary { get => _shortSessionSummary; private set => Set(ref _shortSessionSummary, value); }
    public string AnalysisComparisonSummary { get => _analysisComparisonSummary; private set => Set(ref _analysisComparisonSummary, value); }
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }
    public bool HasNoTimeline { get => _hasNoTimeline; private set => Set(ref _hasNoTimeline, value); }
    public bool HasNoSites { get => _hasNoSites; private set => Set(ref _hasNoSites, value); }
    public bool HasNoCreators { get => _hasNoCreators; private set => Set(ref _hasNoCreators, value); }
    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }
    public string NewCategoryRuleTarget { get => _newCategoryRuleTarget; set => Set(ref _newCategoryRuleTarget, value); }
    public string NewCategoryRulePattern { get => _newCategoryRulePattern; set => Set(ref _newCategoryRulePattern, value); }
    public string NewCategoryRuleCategory { get => _newCategoryRuleCategory; set => Set(ref _newCategoryRuleCategory, value); }
    public string NewCategoryRuleMatchMode { get => _newCategoryRuleMatchMode; set => Set(ref _newCategoryRuleMatchMode, value); }
    public string NewCategoryRuleColor { get => _newCategoryRuleColor; set => Set(ref _newCategoryRuleColor, value); }
    public int NewCategoryRulePriority { get => _newCategoryRulePriority; set => Set(ref _newCategoryRulePriority, Math.Clamp(value, 0, 1000)); }
    public string CategoryRulePreview { get => _categoryRulePreview; private set => Set(ref _categoryRulePreview, value); }
    public string ExplorerRange
    {
        get => _explorerRange;
        set
        {
            if (!Set(ref _explorerRange, value)) return;
            Raise(nameof(DateLabel));
        }
    }
    public int TimelineZoomMinutes { get => _timelineZoomMinutes; set => Set(ref _timelineZoomMinutes, value); }
    public string SearchText { get => _searchText; set => Set(ref _searchText, value); }
    public string SearchApp { get => _searchApp; set => Set(ref _searchApp, value); }
    public string SearchDomain { get => _searchDomain; set => Set(ref _searchDomain, value); }
    public string SearchCreator { get => _searchCreator; set => Set(ref _searchCreator, value); }
    public string SearchCategory { get => _searchCategory; set => Set(ref _searchCategory, value); }
    public string SearchProject { get => _searchProject; set => Set(ref _searchProject, value); }
    public bool SearchIncludeIdle { get => _searchIncludeIdle; set => Set(ref _searchIncludeIdle, value); }
    public bool SearchOnlyFocus { get => _searchOnlyFocus; set => Set(ref _searchOnlyFocus, value); }
    public bool SearchOnlySwitches { get => _searchOnlySwitches; set => Set(ref _searchOnlySwitches, value); }
    public string ExplorerStatus { get => _explorerStatus; private set => Set(ref _explorerStatus, value); }
    public bool HasNoExplorerResults { get => _hasNoExplorerResults; private set => Set(ref _hasNoExplorerResults, value); }
    public string AverageRecoveryTime { get => _averageRecoveryTime; private set => Set(ref _averageRecoveryTime, value); }
    public string RecoverySummary { get => _recoverySummary; private set => Set(ref _recoverySummary, value); }
    public ExplorerTimelineItem? SelectedExplorerItem
    {
        get => _selectedExplorerItem;
        set
        {
            if (!Set(ref _selectedExplorerItem, value)) return;
            LoadAnnotationEditor(value);
            Raise(nameof(HasSelectedExplorerItem));
        }
    }
    public bool HasSelectedExplorerItem => SelectedExplorerItem is not null;
    public bool CanDeleteAnnotation => _editingAnnotationId != 0;
    public string AnnotationStart { get => _annotationStart; set => Set(ref _annotationStart, value); }
    public string AnnotationEnd { get => _annotationEnd; set => Set(ref _annotationEnd, value); }
    public string AnnotationProject { get => _annotationProject; set => Set(ref _annotationProject, value); }
    public string AnnotationTask { get => _annotationTask; set => Set(ref _annotationTask, value); }
    public string AnnotationNote { get => _annotationNote; set => Set(ref _annotationNote, value); }
    public string AnnotationCategory { get => _annotationCategory; set => Set(ref _annotationCategory, value); }

    public bool IsWeeklyAiReport
    {
        get => _isWeeklyAiReport;
        private set
        {
            if (!Set(ref _isWeeklyAiReport, value)) return;
            Raise(nameof(DateLabel));
            Raise(nameof(AiPeriodLabel));
            Raise(nameof(AiReportTypeLabel));
        }
    }
    public string AiReportTypeLabel => IsWeeklyAiReport ? "週報" : "日報";
    public string AiPeriodLabel => IsWeeklyAiReport
        ? $"{StartOfWeek(SelectedDate):yyyy/M/d}〜{StartOfWeek(SelectedDate).AddDays(6):yyyy/M/d}"
        : $"{SelectedDate:yyyy/M/d}";
    public bool IsAiGenerating { get => _isAiGenerating; private set => Set(ref _isAiGenerating, value); }
    public bool HasAiReport { get => _hasAiReport; private set => Set(ref _hasAiReport, value); }
    public string AiReportContent { get => _aiReportContent; private set => Set(ref _aiReportContent, value); }
    public string AiReportMetadata { get => _aiReportMetadata; private set => Set(ref _aiReportMetadata, value); }
    public string AiReportStatus { get => _aiReportStatus; private set => Set(ref _aiReportStatus, value); }
    public string AiEvidenceSummary { get => _aiEvidenceSummary; private set => Set(ref _aiEvidenceSummary, value); }
    public string AiFreshnessStatus { get => _aiFreshnessStatus; private set => Set(ref _aiFreshnessStatus, value); }
    public bool IsAiReportStale { get => _isAiReportStale; private set => Set(ref _isAiReportStale, value); }
    public string OllamaConnectionStatus { get => _ollamaConnectionStatus; private set => Set(ref _ollamaConnectionStatus, value); }
    public string GameSafetyStatus { get => _gameSafetyStatus; private set => Set(ref _gameSafetyStatus, value); }

    public bool RecordingEnabled { get => _recordingEnabled; set => Set(ref _recordingEnabled, value); }
    public bool RecordUrls { get => _recordUrls; set => Set(ref _recordUrls, value); }
    public bool RecordPageTitles { get => _recordPageTitles; set => Set(ref _recordPageTitles, value); }
    public bool RecordWindowTitles { get => _recordWindowTitles; set => Set(ref _recordWindowTitles, value); }
    public bool RecordCreatorNames { get => _recordCreatorNames; set => Set(ref _recordCreatorNames, value); }
    public int IdleThresholdMinutes { get => _idleThresholdMinutes; set => Set(ref _idleThresholdMinutes, value); }
    public int RetentionDays { get => _retentionDays; set => Set(ref _retentionDays, value); }
    public bool StartWithWindows { get => _startWithWindows; set => Set(ref _startWithWindows, value); }
    public bool StartMinimized { get => _startMinimized; set => Set(ref _startMinimized, value); }
    public bool AutomaticBackupsEnabled { get => _automaticBackupsEnabled; set => Set(ref _automaticBackupsEnabled, value); }
    public string OllamaEndpoint { get => _ollamaEndpoint; set => Set(ref _ollamaEndpoint, value); }
    public string OllamaModel { get => _ollamaModel; set => Set(ref _ollamaModel, value); }
    public bool UnloadOllamaAfterGeneration { get => _unloadOllamaAfterGeneration; set => Set(ref _unloadOllamaAfterGeneration, value); }
    public int OllamaTimeoutMinutes { get => _ollamaTimeoutMinutes; set => Set(ref _ollamaTimeoutMinutes, value); }
    public bool GameSafeModeEnabled { get => _gameSafeModeEnabled; set { if (Set(ref _gameSafeModeEnabled, value)) GameSafetyStatus = _gameSafetyService.Evaluate(BuildAiSafetySettings()).Message; } }
    public bool RequireIdleOllama { get => _requireIdleOllama; set => Set(ref _requireIdleOllama, value); }
    public string GameProcessNames { get => _gameProcessNames; set { if (Set(ref _gameProcessNames, value)) GameSafetyStatus = _gameSafetyService.Evaluate(BuildAiSafetySettings()).Message; } }
    public string ThemeMode
    {
        get => _themeMode;
        set
        {
            if (!Set(ref _themeMode, value)) return;
            EffectiveTheme = ThemeService.Apply(value);
        }
    }
    public string EffectiveTheme { get => _effectiveTheme; private set => Set(ref _effectiveTheme, value); }
    public string NewAppExclusion { get => _newAppExclusion; set => Set(ref _newAppExclusion, value); }
    public string NewDomainExclusion { get => _newDomainExclusion; set => Set(ref _newDomainExclusion, value); }

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

    private async Task LoadAiReportAsync()
    {
        if (!_uiActive || !IsAiReports) return;
        var type = IsWeeklyAiReport ? AiReportType.Weekly : AiReportType.Daily;
        var (start, scheduledEnd) = GetAiRange();
        var now = DateTimeOffset.Now;
        var end = start <= now && now < scheduledEnd ? now : scheduledEnd;
        var snapshot = await Task.Run(() => _aiReportService.GetSnapshot(type, start, end));
        ApplyAiSnapshot(snapshot);
        if (OllamaModels.Count == 0 && OllamaConnectionStatus == "未確認")
            await RefreshOllamaAsync();
    }

    private async Task RefreshOllamaAsync()
    {
        try
        {
            OllamaConnectionStatus = "ローカルOllamaへ接続確認中…";
            _ = OllamaClient.ParseLocalEndpoint(OllamaEndpoint);
            var status = await _aiReportService.GetOllamaStatusAsync(OllamaEndpoint, CancellationToken.None);
            Replace(OllamaModels, status.Models.Select(model => model.Name));
            if (string.IsNullOrWhiteSpace(OllamaModel) ||
                !OllamaModels.Contains(OllamaModel, StringComparer.OrdinalIgnoreCase))
                OllamaModel = OllamaModels.FirstOrDefault() ?? string.Empty;
            var runtime = status.RunningModels.Count == 0
                ? "GPUメモリ上のモデルなし"
                : $"稼働中 {string.Join(", ", status.RunningModels.Select(model => model.Name))}";
            OllamaConnectionStatus = $"Ollama {status.Version} / モデル {status.Models.Count}件 / {runtime}";
        }
        catch (Exception exception)
        {
            OllamaConnectionStatus = $"接続できません: {exception.Message}";
            DiagnosticLogService.Error("AI.OllamaStatus", exception);
        }
    }

    private async Task GenerateAiReportAsync()
    {
        if (IsAiGenerating || _isDeletingData) return;
        if (string.IsNullOrWhiteSpace(OllamaModel))
        {
            AiReportStatus = "モデルを選択してください。先に「接続確認」を押すと一覧を取得できます。";
            return;
        }

        try
        {
            _ = OllamaClient.ParseLocalEndpoint(OllamaEndpoint);
        }
        catch (Exception exception)
        {
            AiReportStatus = $"Ollama接続先が不正です: {exception.Message}";
            return;
        }

        var safetySettings = BuildAiSafetySettings();
        var safety = _gameSafetyService.Evaluate(safetySettings);
        if (safety.IsGameForeground)
        {
            AiReportStatus = $"{safety.ProcessName} がForegroundのため、ゲームセーフモードが生成を停止しました。";
            return;
        }

        var type = IsWeeklyAiReport ? AiReportType.Weekly : AiReportType.Daily;
        var selectedDate = SelectedDate;
        var (start, scheduledEnd) = GetAiRange();
        var now = DateTimeOffset.Now;
        var end = start <= now && now < scheduledEnd ? now : scheduledEnd;
        var cancellation = new CancellationTokenSource();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _aiGenerationCancellation?.Dispose();
        _aiGenerationCancellation = cancellation;
        _aiGenerationStopped = stopped;
        _gameCancellationProcess = null;
        IsAiGenerating = true;
        AiReportStatus = RequireIdleOllama
            ? "Ollamaの空き状態を確認し、ローカルGPUで生成しています…"
            : "GPU実行枠を確保し、ローカルOllamaで生成しています…";
        var gameMonitor = MonitorGameAndCancelAsync(safetySettings, cancellation);
        try
        {
            var result = await _aiReportService.GenerateAsync(type, start, end, OllamaEndpoint,
                OllamaModel, UnloadOllamaAfterGeneration, RequireIdleOllama,
                OllamaTimeoutMinutes, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!IsAiReports || SelectedDate != selectedDate || type != (IsWeeklyAiReport ? AiReportType.Weekly : AiReportType.Daily))
                return;
            await LoadAiReportAsync();
            var memory = result.ModelUnloaded ? "生成後にGPUからモデルを解放" : "モデルを5分間保持";
            AiReportStatus = $"生成完了・品質検査OK（{result.Duration:mm\\:ss}、入力 {result.PromptTokens} / 出力 {result.OutputTokens} tokens、{memory}）";
        }
        catch (OperationCanceledException)
        {
            AiReportStatus = _gameCancellationProcess is null
                ? "生成をキャンセルしました。"
                : $"{_gameCancellationProcess} がForegroundになったため、ゲームセーフモードが生成を中止しました。";
        }
        catch (OllamaBusyException exception)
        {
            AiReportStatus = $"Ollamaは使用中です（{string.Join(", ", exception.Models.Select(model => model.Name))}）。完了または解放後に再実行してください。";
        }
        catch (AiReportQualityException exception)
        {
            AiReportStatus = exception.Message + " 保存はしていません。";
            DiagnosticLogService.Error("AI.QualityValidation", exception);
        }
        catch (Exception exception)
        {
            AiReportStatus = $"生成エラー: {exception.Message}";
            DiagnosticLogService.Error("AI.GenerateReport", exception);
        }
        finally
        {
            cancellation.Cancel();
            try { await gameMonitor; } catch (OperationCanceledException) { }
            if (ReferenceEquals(_aiGenerationCancellation, cancellation))
            {
                _aiGenerationCancellation = null;
                IsAiGenerating = false;
            }
            if (ReferenceEquals(_aiGenerationStopped, stopped)) _aiGenerationStopped = null;
            stopped.TrySetResult();
            cancellation.Dispose();
        }
    }

    private async Task MonitorGameAndCancelAsync(AppSettings settings, CancellationTokenSource generation)
    {
        try
        {
            var processName = await _gameSafetyService.MonitorAsync(settings, generation.Token);
            if (string.IsNullOrWhiteSpace(processName)) return;
            _gameCancellationProcess = processName;
            generation.Cancel();
        }
        catch (OperationCanceledException) { }
    }

    private void ApplyAiSnapshot(AiReportSnapshot snapshot)
    {
        var report = snapshot.Report;
        HasAiReport = report is not null;
        AiReportContent = report?.Content ?? string.Empty;
        IsAiReportStale = snapshot.IsStale;
        AiReportMetadata = report is null
            ? $"{AiPeriodLabel} の{AiReportTypeLabel}はまだ生成されていません"
            : $"{report.GeneratedAt.LocalDateTime:yyyy/M/d HH:mm}生成 ・ {report.ModelName} ・ ローカル保存";
        AiFreshnessStatus = report is null
            ? "未生成"
            : snapshot.IsStale
                ? "生成後に新しいActivityがあります。再生成すると最新データを反映できます。"
                : "生成後のActivity変更はありません";
        var analysis = snapshot.Analysis;
        AiEvidenceSummary = $"PC {FormatDuration(analysis.Report.TotalPcSeconds)} ・ Idle {FormatDuration(analysis.Report.IdleSeconds)} ・ 集中 {FormatDuration(analysis.Focus.TotalSeconds)} ・ 25分以上の集中 {analysis.Focus.BlockCount}回";
        Replace(AiEvidenceApps, ToRanking(analysis.Report.Apps.Take(5).ToList()));
        Replace(AiEvidenceSites, ToRanking(analysis.Report.Sites.Take(5).ToList()));
        Replace(AiEvidenceCategories, ToRanking(analysis.Report.Categories.Take(5).ToList()));
    }

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

    public void ResetBrowserPairing()
    {
        _nativeMessagingRegistration.Unregister();
        BrowserBridgeToken = _settingsService.ResetBrowserPairing();
        BrowserPairingStatus = "未ペアリング（新しいトークンを拡張機能へ設定してください）";
        StatusMessage = "ブラウザ連携トークンを再発行し、登録済み拡張IDを解除しました";
    }

    public void RegisterBrowserExtension()
    {
        _nativeMessagingRegistration.Register(BrowserExtensionId);
        BrowserExtensionId = string.Empty;
        ApplyBridgeDiagnostics(_bridgeDiagnosticsProvider());
        StatusMessage = "Chrome／EdgeのNative Messaging連携を登録しました。ブラウザを再起動してください";
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

    private void ApplyReport(ActivityReport report, bool includeTimeline)
    {
        TodayPcTime = FormatDuration(report.TotalPcSeconds);
        IdleTime = FormatDuration(report.IdleSeconds);
        TopApp = report.Apps.FirstOrDefault()?.Name ?? "—";
        TopSite = report.Sites.FirstOrDefault()?.Name ?? "—";
        PeriodTotal = FormatDuration(report.TotalPcSeconds);
        PeriodLabel = $"Idle {FormatDuration(report.IdleSeconds)}";

        if (includeTimeline)
            Replace(Timeline, report.Timeline.Select(item => new TimelineItem(
                $"{item.Start:HH:mm} – {item.End:HH:mm}", item.Title, item.Subtitle,
                FormatDuration(item.Seconds), item.IsIdle, item.IsWebsite)));
        else
            Timeline.Clear();
        HasNoTimeline = Timeline.Count == 0;
        Replace(AppRankings, ToRanking(report.Apps));
        Replace(SiteRankings, ToRanking(report.Sites));
        HasNoSites = SiteRankings.Count == 0;
        Replace(CreatorRankings, ToRanking(report.Creators));
        HasNoCreators = CreatorRankings.Count == 0;
        Replace(CategoryRankings, ToRanking(report.Categories));

        ApplyDaily(report.Daily);
    }

    private void ApplyDaily(IReadOnlyList<DailyTotal> daily)
    {
        var maxDaily = Math.Max(1, daily.Count == 0 ? 0 : daily.Max(item => item.Seconds));
        Replace(DailyUsage, daily.Select(item => new DailyItem(
            item.Date.ToString("M/d (ddd)"), FormatDuration(item.Seconds), $"Idle {FormatDuration(item.IdleSeconds)}",
            320d * item.Seconds / maxDaily)));
    }

    private void ApplyInsights(DashboardInsights insights)
    {
        FocusTime = FormatDuration(insights.Focus.TotalSeconds);
        FocusDetail = insights.Focus.BlockCount == 0
            ? "25分以上の集中ブロックはありません"
            : $"最長 {FormatDuration(insights.Focus.LongestSeconds)} ・ {insights.Focus.BlockCount}回";

        if (insights.PreviousWeekSeconds <= 0)
            WeekComparison = insights.CurrentWeekSeconds > 0 ? "前週データなし" : "比較データなし";
        else
        {
            var percent = (insights.CurrentWeekSeconds - insights.PreviousWeekSeconds) * 100d / insights.PreviousWeekSeconds;
            WeekComparison = $"前週同期間比 {percent:+0;-0;±0}%";
        }
        WeekComparisonDetail = $"今週 {FormatDuration(insights.CurrentWeekSeconds)} / 前週 {FormatDuration(insights.PreviousWeekSeconds)}";

        var maximum = Math.Max(1, insights.Hourly.Count == 0 ? 0 : insights.Hourly.Max(item => item.Seconds));
        var byHour = insights.Hourly.ToDictionary(item => (item.HourStart.Date, item.HourStart.Hour));
        var firstDay = insights.Hourly.FirstOrDefault()?.HourStart.Date ?? DateTime.Today.AddDays(-6);
        var days = Enumerable.Range(0, 7).Select(offset =>
        {
            var date = firstDay.AddDays(offset);
            var cells = Enumerable.Range(0, 24).Select(hour =>
            {
                var seconds = byHour.GetValueOrDefault((date, hour))?.Seconds ?? 0;
                var opacity = seconds == 0 ? 0.08 : 0.18 + 0.82 * seconds / maximum;
                return new HeatmapCellItem(hour, opacity,
                    $"{date:M/d (ddd)} {hour:00}:00–{hour + 1:00}:00  {FormatDuration(seconds)}");
            }).ToList();
            return new HeatmapDayItem(date.ToString("M/d (ddd)"), cells);
        });
        Replace(HeatmapDays, days);
    }

    private void ApplyBehaviorAnalysis(BehaviorAnalysis analysis)
    {
        FocusQualityScore = analysis.FocusQuality.Score.ToString();
        FocusQualityGrade = analysis.FocusQuality.Grade;
        FocusQualityDetail = analysis.FocusQuality.Detail;
        ContextSwitchCount = $"{analysis.Context.SwitchCount:N0}回";
        SwitchesPerHour = $"{analysis.Context.SwitchesPerHour:0.0}回/時";
        AverageSessionDuration = FormatDuration(analysis.Context.AverageSessionSeconds);
        ShortSessionSummary = $"{analysis.Context.ShortSessionCount:N0}回（{analysis.Context.ShortSessionRate:0}%）";
        Replace(SwitchDestinations, analysis.Context.TopDestinations.Select(item =>
            new AnalysisMetricItem(item.Name, $"{item.Seconds:N0}回")));

        var deepDive = analysis.DeepDive;
        AverageRecoveryTime = deepDive.ReturnCount == 0 ? "—" : FormatDuration(deepDive.AverageRecoverySeconds);
        RecoverySummary = deepDive.InterruptionCount == 0
            ? "5分以上の作業後に発生した割り込みはありません"
            : $"割り込み {deepDive.InterruptionCount:N0}回中 {deepDive.ReturnCount:N0}回で30分以内に復帰";
        Replace(InterruptionSources, deepDive.Interrupters.Select(item => new AnalysisMetricItem(
            item.Name, $"{item.Count:N0}回 ・ 平均復帰 {FormatDuration(item.AverageRecoverySeconds)}")));
        Replace(FocusHours, deepDive.FocusHours.Select(item => new AnalysisMetricItem(
            $"{item.Hour:00}:00〜{(item.Hour + 1) % 24:00}:00",
            $"集中率 {item.Rate:0}% ・ 実作業 {FormatDuration(item.ActiveSeconds)}")));
        Replace(FocusDistribution, deepDive.DurationDistribution.Select(item => new AnalysisMetricItem(
            item.Label, $"{item.Count:N0}回 ・ {FormatDuration(item.TotalSeconds)}")));
        Replace(FocusScoreComponents, deepDive.ScoreComponents.Select(item => new AnalysisMetricItem(
            item.Name, $"{item.Points:+0;-0;0}点 ・ {item.Detail}")));

        var comparison = analysis.Comparison;
        if (comparison.PreviousSeconds <= 0)
            AnalysisComparisonSummary = comparison.CurrentSeconds > 0 ? "前期間の比較データがありません" : "比較データなし";
        else
        {
            var percent = (comparison.CurrentSeconds - comparison.PreviousSeconds) * 100d / comparison.PreviousSeconds;
            AnalysisComparisonSummary = $"前週比 {percent:+0;-0;±0}%　今週 {FormatDuration(comparison.CurrentSeconds)} / 前週 {FormatDuration(comparison.PreviousSeconds)}";
        }
        Replace(PeriodChanges, comparison.Changes.Select(item => new ComparisonItem(
            item.Scope, item.Name, FormatDuration(item.PreviousSeconds), FormatDuration(item.CurrentSeconds),
            $"{(item.DeltaSeconds >= 0 ? "+" : "−")}{FormatDuration(Math.Abs(item.DeltaSeconds))}", item.DeltaSeconds >= 0)));
    }

    private void ApplyBridgeDiagnostics(BrowserBridgeDiagnostics diagnostics)
    {
        BridgeStatus = diagnostics.ListenerStatus;
        BrowserConnectionState = diagnostics.ConnectionState;
        BrowserLastReceived = diagnostics.LastReceivedAt?.LocalDateTime.ToString("M/d HH:mm:ss") ?? "未受信";
        BrowserLastActivity = string.Join(" / ", new[] { diagnostics.LastBrowser, diagnostics.LastDomain, diagnostics.LastCreator }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        if (string.IsNullOrWhiteSpace(BrowserLastActivity)) BrowserLastActivity = "—";
        BrowserExtensionVersion = diagnostics.ExtensionVersion ?? "未受信";
        BrowserExtensionStatus = diagnostics.ExtensionVersion is null
            ? $"推奨バージョン {diagnostics.ExpectedExtensionVersion}"
            : diagnostics.IsExpectedExtension
                ? "拡張機能は最新です"
                : $"再読み込みが必要です（推奨 {diagnostics.ExpectedExtensionVersion}）";
        BrowserRequestCounts = $"正常 {diagnostics.AcceptedCount} / 拒否 {diagnostics.RejectedCount} / 処理中 {diagnostics.ActiveClients} ・ {diagnostics.LastResult}";
        var paired = _settingsService.Current.BrowserBridgeAllowedOrigins.Split(';', StringSplitOptions.RemoveEmptyEntries);
        BrowserPairingStatus = paired.Length == 0
            ? "未ペアリング"
            : $"登録済み拡張 {paired.Length}件: {string.Join(", ", paired.Select(origin => origin.Replace("chrome-extension://", string.Empty)))}";
    }

    private static IEnumerable<RankingItem> ToRanking(IReadOnlyList<UsageTotal> values)
    {
        var max = Math.Max(1, values.FirstOrDefault()?.Seconds ?? 1);
        return values.Take(10).Select((item, index) => new RankingItem(index + 1, item.Name, FormatDuration(item.Seconds), 240d * item.Seconds / max));
    }

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

    private (DateTimeOffset Start, DateTimeOffset End) GetAiRange()
    {
        var day = DateTime.SpecifyKind(SelectedDate.Date, DateTimeKind.Local);
        if (!IsWeeklyAiReport) return (new DateTimeOffset(day), new DateTimeOffset(day.AddDays(1)));
        var monday = DateTime.SpecifyKind(StartOfWeek(day), DateTimeKind.Local);
        return (new DateTimeOffset(monday), new DateTimeOffset(monday.AddDays(7)));
    }

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
            _ => CategoryRuleTarget.Domain
        };
        var mode = NewCategoryRuleMatchMode == "正規表現" ? CategoryMatchMode.Regex : CategoryMatchMode.Wildcard;
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
            _ => "ドメイン"
        }, rule.Pattern, rule.Category,
            rule.MatchMode == CategoryMatchMode.Regex ? "正規表現" : "ワイルドカード",
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
            _ => CategoryRuleTarget.Domain
        };
        var candidate = new CategoryRule
        {
            Target = target,
            Pattern = NewCategoryRulePattern.Trim(),
            Category = SettingsService.NormalizeCategoryPath(NewCategoryRuleCategory),
            MatchMode = NewCategoryRuleMatchMode == "正規表現" ? CategoryMatchMode.Regex : CategoryMatchMode.Wildcard,
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

    private async Task SearchExplorerAsync()
    {
        if (!_uiActive || !IsExplorer) return;
        var selectedDate = SelectedDate;
        var refresh = new CancellationTokenSource();
        var previous = _refreshCancellation;
        _refreshCancellation = refresh;
        previous?.Cancel();
        IsLoading = true;
        try
        {
            var (start, end) = GetExplorerRangeBounds();
            var query = new ActivitySearchQuery(start, end, TimelineZoomMinutes,
                SearchText, SearchApp, SearchDomain, SearchCreator, SearchCategory, SearchProject,
                SearchIncludeIdle, SearchOnlyFocus, SearchOnlySwitches);
            var result = await Task.Run(() => _explorerService.Search(query), refresh.Token);
            refresh.Token.ThrowIfCancellationRequested();
            if (!CanApplyRefresh(refresh, "Explorer", selectedDate)) return;
            SelectedExplorerItem = null;
            Replace(ExplorerTimeline, result.Entries.Select(item => new ExplorerTimelineItem(
                item.AnnotationId, item.Start, item.End, item.AnnotationStart, item.AnnotationEnd,
                item.Start.LocalDateTime.Date == item.End.LocalDateTime.Date
                    ? $"{item.Start.LocalDateTime:HH:mm} – {item.End.LocalDateTime:HH:mm}"
                    : $"{item.Start.LocalDateTime:M/d HH:mm} – {item.End.LocalDateTime:M/d HH:mm}",
                item.Title,
                item.ActivityCount > 1 ? $"{item.Subtitle}  ·  {item.ActivityCount}種類" : item.Subtitle,
                FormatDuration(item.Seconds), item.Category, item.CategoryReason, item.Project, item.Task,
                item.Note, item.IsIdle, item.IsWebsite, item.IsFocus, item.IsSwitch, item.ActivityCount)));
            HasNoExplorerResults = ExplorerTimeline.Count == 0;
            ExplorerStatus = $"{result.RawSessionCount:N0}セッション・{FormatDuration(result.MatchingSeconds)}" +
                             $"／{TimelineZoomMinutes}分粒度・{ExplorerTimeline.Count:N0}区間" +
                             (result.IsTruncated ? "（最新2,000区間を表示）" : string.Empty);
            StatusMessage = $"分析検索を更新 {DateTime.Now:HH:mm:ss}";
        }
        catch (OperationCanceledException) { }
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

    private async Task OpenExplorerAsync(string scope, string? value)
    {
        var origin = SelectedPage;
        ClearExplorerFilters();
        ExplorerRange = origin switch
        {
            "Week" or "Analysis" => "7日",
            "Month" => "1か月",
            _ => "1日"
        };
        switch (scope)
        {
            case "App": case "アプリ": SearchApp = value ?? string.Empty; break;
            case "Domain": case "サイト": SearchDomain = value ?? string.Empty; break;
            case "Creator": SearchCreator = value ?? string.Empty; break;
            case "Category": case "カテゴリ": SearchCategory = value ?? string.Empty; break;
            case "Focus": SearchOnlyFocus = true; break;
            case "Switch": SearchOnlySwitches = true; break;
        }
        SelectedPage = "Explorer";
        await SearchExplorerAsync();
    }

    private void ClearExplorerFilters()
    {
        SearchText = string.Empty;
        SearchApp = string.Empty;
        SearchDomain = string.Empty;
        SearchCreator = string.Empty;
        SearchCategory = string.Empty;
        SearchProject = string.Empty;
        SearchIncludeIdle = true;
        SearchOnlyFocus = false;
        SearchOnlySwitches = false;
    }

    private void LoadAnnotationEditor(ExplorerTimelineItem? item)
    {
        SetEditingAnnotationId(item?.AnnotationId ?? 0);
        if (item is null)
        {
            AnnotationStart = AnnotationEnd = AnnotationProject = AnnotationTask = AnnotationNote = AnnotationCategory = string.Empty;
            return;
        }
        var start = item.AnnotationStart ?? item.Start;
        var end = item.AnnotationEnd ?? item.End;
        AnnotationStart = start.LocalDateTime.ToString("yyyy/MM/dd HH:mm");
        AnnotationEnd = end.LocalDateTime.ToString("yyyy/MM/dd HH:mm");
        AnnotationProject = item.Project;
        AnnotationTask = item.Task;
        AnnotationNote = item.Note;
        AnnotationCategory = item.AnnotationId == 0 ? string.Empty : item.Category;
    }

    private async Task SaveAnnotationAsync()
    {
        if (SelectedExplorerItem is null) throw new InvalidOperationException("補正する時間帯を選択してください。");
        var annotation = new ActivityAnnotation
        {
            Id = _editingAnnotationId,
            StartTime = ParseLocalTimestamp(AnnotationStart, "開始時刻"),
            EndTime = ParseLocalTimestamp(AnnotationEnd, "終了時刻"),
            ProjectName = AnnotationProject,
            TaskName = AnnotationTask,
            Note = AnnotationNote,
            CategoryOverride = SettingsService.NormalizeCategoryPath(AnnotationCategory)
        };
        _annotationRepository.Save(annotation);
        StatusMessage = "補正を保存しました。自動記録データは変更していません";
        await SearchExplorerAsync();
    }

    private async Task DeleteAnnotationAsync()
    {
        if (_editingAnnotationId == 0) return;
        _annotationRepository.Delete(_editingAnnotationId);
        SetEditingAnnotationId(0);
        StatusMessage = "補正を取り消しました。元の自動記録表示へ戻しました";
        await SearchExplorerAsync();
    }

    private void SetEditingAnnotationId(long value)
    {
        if (_editingAnnotationId == value) return;
        _editingAnnotationId = value;
        Raise(nameof(CanDeleteAnnotation));
    }

    private static DateTimeOffset ParseLocalTimestamp(string value, string label)
    {
        if (!DateTime.TryParse(value, out var parsed))
            throw new ArgumentException($"{label}は yyyy/MM/dd HH:mm 形式で入力してください。");
        return new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Local));
    }

    private (DateTimeOffset Start, DateTimeOffset End) GetExplorerRangeBounds()
    {
        var day = DateTime.SpecifyKind(SelectedDate.Date, DateTimeKind.Local);
        return ExplorerRange switch
        {
            "7日" => (new DateTimeOffset(StartOfWeek(day)), new DateTimeOffset(StartOfWeek(day).AddDays(7))),
            "1か月" => (new DateTimeOffset(new DateTime(day.Year, day.Month, 1, 0, 0, 0, DateTimeKind.Local)),
                new DateTimeOffset(new DateTime(day.Year, day.Month, 1, 0, 0, 0, DateTimeKind.Local).AddMonths(1))),
            "90日" => (new DateTimeOffset(day.AddDays(-89)), new DateTimeOffset(day.AddDays(1))),
            _ => (new DateTimeOffset(day), new DateTimeOffset(day.AddDays(1)))
        };
    }

    private string GetExplorerDateLabel()
    {
        var (start, end) = GetExplorerRangeBounds();
        return $"{start.LocalDateTime:yyyy年M月d日} — {end.AddTicks(-1).LocalDateTime:yyyy年M月d日}";
    }

    private void LoadBackups() => Replace(Backups, _backupService.ListBackups().Select(item =>
        new BackupItem(item.Name, item.CreatedAt.LocalDateTime.ToString("yyyy/M/d HH:mm:ss"),
            FormatBytes(item.SizeBytes), item.FullPath, item.IsAutomatic)));

    private AppSettings BuildAiSafetySettings() => new()
    {
        GameSafeModeEnabled = GameSafeModeEnabled,
        GameProcessNames = GameProcessNames
    };

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
