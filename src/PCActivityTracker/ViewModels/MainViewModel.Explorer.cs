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
    private async Task SearchExplorerAsync()
    {
        if (!_uiActive || !IsExplorer) return;
        if (IsUnclassifiedView) { await LoadUnclassifiedAsync(); return; }
        var selectedDate = SelectedDate;
        var refresh = new CancellationTokenSource();
        var previous = _refreshCancellation;
        _refreshCancellation = refresh;
        previous?.Cancel();
        IsLoading = true;
        try
        {
            var query = BuildExplorerQuery();
            var compare = CompareExplorer;
            var comparison = compare ? await Task.Run(() => _explorerService.Compare(query), refresh.Token) : null;
            var result = comparison?.Current ?? await Task.Run(() => _explorerService.Search(query), refresh.Token);
            refresh.Token.ThrowIfCancellationRequested();
            if (!CanApplyRefresh(refresh, "Explorer", selectedDate) || query != BuildExplorerQuery() || compare != CompareExplorer) return;
            SelectedExplorerItem = null;
            SelectedExplorerBucket = null;
            Replace(ExplorerTimeline, result.Entries.Select(ToExplorerItem));
            ApplyExplorerComparison(result, comparison);
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
        SearchExcludedCategory = string.Empty;
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
        AnnotationStart = start.LocalDateTime.ToString("yyyy/MM/dd HH:mm:ss.fff");
        AnnotationEnd = end.LocalDateTime.ToString("yyyy/MM/dd HH:mm:ss.fff");
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

}
