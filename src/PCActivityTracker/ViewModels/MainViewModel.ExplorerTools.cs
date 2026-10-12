using PCActivityTracker.Services;

namespace PCActivityTracker.ViewModels;

public sealed partial class MainViewModel
{
    private string _savedSearchName = string.Empty, _savedSearchStatus = string.Empty;
    private string _searchExcludedCategory = string.Empty, _explorerComparisonSummary = "前期間比較は必要なときだけ有効にできます。";
    private SavedExplorerSearch? _selectedSavedSearch;
    private bool _compareExplorer;
    private ExplorerTimelineItem? _selectedExplorerBucket;
    public ExplorerTimelineItem? SelectedExplorerBucket
    {
        get => _selectedExplorerBucket;
        set { if (Set(ref _selectedExplorerBucket, value)) SelectedExplorerItem = value; }
    }
    public BulkObservableCollection<SavedExplorerSearch> SavedExplorerSearches { get; } = new();
    public BulkObservableCollection<ComparisonItem> ExplorerCategoryComparison { get; } = new();
    public string SavedSearchName { get => _savedSearchName; set => Set(ref _savedSearchName, value); }
    public string SavedSearchStatus { get => _savedSearchStatus; private set => Set(ref _savedSearchStatus, value); }
    public SavedExplorerSearch? SelectedSavedSearch { get => _selectedSavedSearch; set => Set(ref _selectedSavedSearch, value); }
    public string SearchExcludedCategory { get => _searchExcludedCategory; set => Set(ref _searchExcludedCategory, value); }
    public bool CompareExplorer { get => _compareExplorer; set => Set(ref _compareExplorer, value); }
    public string ExplorerComparisonSummary { get => _explorerComparisonSummary; private set => Set(ref _explorerComparisonSummary, value); }
    public RelayCommand SaveExplorerSearchCommand { get; private set; } = null!;
    public RelayCommand DeleteExplorerSearchCommand { get; private set; } = null!;
    public AsyncRelayCommand ApplyExplorerSearchCommand { get; private set; } = null!;
    public RelayCommand SelectExplorerPartCommand { get; private set; } = null!;

    private void InitializeExplorerTools()
    {
        SaveExplorerSearchCommand = new RelayCommand(_ =>
        {
            if (_isDeletingData) return;
            _settingsService.ExplorerSearches.Save(new(SavedSearchName, ExplorerRange, BuildExplorerQuery()));
            LoadSavedExplorerSearches();
            SavedSearchStatus = "条件をローカル保存しました。同名は上書き。履歴削除・保存期間とは別に保持されます。";
        }, errorHandler: HandleCommandError);
        DeleteExplorerSearchCommand = new RelayCommand(_ =>
        {
            if (_isDeletingData || SelectedSavedSearch is null) return;
            _settingsService.ExplorerSearches.Delete(SelectedSavedSearch.Name);
            LoadSavedExplorerSearches();
            SavedSearchStatus = "選択した保存条件を削除しました。";
        }, errorHandler: HandleCommandError);
        ApplyExplorerSearchCommand = new AsyncRelayCommand(async _ =>
        {
            var item = SelectedSavedSearch;
            if (item is null) return;
            var query = item.Conditions;
            ExplorerRange = item.Range; TimelineZoomMinutes = query.ZoomMinutes;
            SearchText = query.Text; SearchApp = query.App; SearchDomain = query.Domain;
            SearchCreator = query.Creator; SearchCategory = query.Category; SearchProject = query.Project;
            SearchIncludeIdle = query.IncludeIdle; SearchOnlyFocus = query.FocusOnly; SearchOnlySwitches = query.SwitchesOnly;
            SearchExcludedCategory = query.ExcludedCategory;
            SavedSearchName = item.Name;
            IsUnclassifiedView = false;
            await SearchExplorerAsync();
        }, HandleCommandError);
        SelectExplorerPartCommand = new RelayCommand(item =>
        {
            if (item is ExplorerTimelineItem part) SelectedExplorerItem = part;
        });
        LoadSavedExplorerSearches();
    }

    private void LoadSavedExplorerSearches()
    {
        try { Replace(SavedExplorerSearches, _settingsService.ExplorerSearches.Read()); SelectedSavedSearch = null; }
        catch (InvalidOperationException error) { SavedSearchStatus = error.Message; }
    }

    private ActivitySearchQuery BuildExplorerQuery()
    {
        var (start, end) = GetExplorerRangeBounds();
        return new(start, end, TimelineZoomMinutes, SearchText, SearchApp, SearchDomain, SearchCreator,
            SearchCategory, SearchProject, SearchIncludeIdle, SearchOnlyFocus, SearchOnlySwitches, SearchExcludedCategory);
    }

    private static ExplorerTimelineItem ToExplorerItem(ExplorerEntry item) => new(
        item.AnnotationId, item.Start, item.End, item.AnnotationStart, item.AnnotationEnd,
        item.Start.LocalDateTime.Date == item.End.LocalDateTime.Date
            ? $"{item.Start.LocalDateTime:HH:mm} – {item.End.LocalDateTime:HH:mm}"
            : $"{item.Start.LocalDateTime:M/d HH:mm} – {item.End.LocalDateTime:M/d HH:mm}",
        item.Title, item.ActivityCount > 1 ? $"{item.Subtitle}  ·  {item.ActivityCount}区間" : item.Subtitle,
        FormatDuration(item.Seconds), item.Category, item.CategoryReason, item.Project, item.Task, item.Note,
        item.IsIdle, item.IsWebsite, item.IsFocus, item.IsSwitch, item.ActivityCount, item.AppName, item.ProcessName,
        item.Domain, item.Creator, item.CreatorId,
        string.Join("  /  ", (item.Categories ?? Array.Empty<ExplorerCategoryUsage>()).Select(part => $"{part.Category} {FormatDuration(part.Seconds)}")),
        item.Parts?.Select(ToExplorerItem).ToArray());

    private void ApplyExplorerComparison(ActivitySearchResult current, ExplorerComparison? comparison)
    {
        var previous = comparison?.Previous;
        var currentCategories = (current.Categories ?? Array.Empty<ExplorerCategoryUsage>()).ToDictionary(item => item.Category, item => item.Seconds, StringComparer.OrdinalIgnoreCase);
        var previousCategories = (previous?.Categories ?? Array.Empty<ExplorerCategoryUsage>()).ToDictionary(item => item.Category, item => item.Seconds, StringComparer.OrdinalIgnoreCase);
        Replace(ExplorerCategoryComparison, currentCategories.Keys.Union(previousCategories.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(key => currentCategories.GetValueOrDefault(key)).Select(key =>
            {
                var now = currentCategories.GetValueOrDefault(key); var before = previousCategories.GetValueOrDefault(key);
                return new ComparisonItem("カテゴリ", key, previous is null ? "—" : FormatDuration(before), FormatDuration(now),
                    previous is null ? "—" : Difference(now, before), now > before);
            }));
        ExplorerComparisonSummary = $"検索結果全体：PC {FormatDuration(current.MatchingSeconds - current.IdleSeconds)} / Idle {FormatDuration(current.IdleSeconds)}";
        if (comparison is not null)
        {
            ExplorerComparisonSummary += $"　前期間 {comparison.PreviousStart.LocalDateTime:yyyy/M/d HH:mm}～{comparison.PreviousEnd.LocalDateTime:yyyy/M/d HH:mm}（同じ長さ）" +
                $"：PC {Difference(current.MatchingSeconds - current.IdleSeconds, previous!.MatchingSeconds - previous.IdleSeconds)} / Idle {Difference(current.IdleSeconds, previous.IdleSeconds)}";
            var (start, end) = GetExplorerRangeBounds();
            if (start <= DateTimeOffset.Now && DateTimeOffset.Now < end) ExplorerComparisonSummary += " ※選択期間は進行中です。";
        }
    }

    private static string Difference(long current, long previous) => current == previous ? "変化なし" :
        (current > previous ? "+" : "−") + FormatDuration(Math.Abs(current - previous));
}
