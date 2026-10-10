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
    public string BrowserCreatorStatus => !_settingsService.Current.RecordCreatorNames
        ? "配信者・チャンネル記録はOFFです"
        : _browserCreatorStatus;
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

}
