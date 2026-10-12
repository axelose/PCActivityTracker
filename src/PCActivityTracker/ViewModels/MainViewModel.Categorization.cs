using System.Collections;
using PCActivityTracker.Domain;
using PCActivityTracker.Services;

namespace PCActivityTracker.ViewModels;

public sealed partial class MainViewModel
{
    private bool _isUnclassifiedView;
    private string _quickCategory = "Other";
    private string _quickScope = "アプリ";
    private int _quickPriority = 300;
    private string _quickPreviewText = "対象とカテゴリを選び、影響をプレビューしてください。";
    private string _batchCategory = "Development";
    private string _batchPreviewText = "未分類の項目を選択して、影響をプレビューしてください。";
    private string _unclassifiedStatus = "期間を選んで未分類を整理できます。";
    private CategoryRuleReview? _quickReview;
    private CategoryRuleReview? _batchReview;

    public BulkObservableCollection<UnclassifiedItem> UnclassifiedItems { get; } = new();
    public AsyncRelayCommand ShowExplorerTimelineCommand { get; private set; } = null!;
    public AsyncRelayCommand ShowUnclassifiedCommand { get; private set; } = null!;
    public AsyncRelayCommand PreviewQuickCategoryCommand { get; private set; } = null!;
    public AsyncRelayCommand SaveQuickCategoryCommand { get; private set; } = null!;
    public AsyncRelayCommand SetIntervalCategoryCommand { get; private set; } = null!;
    public AsyncRelayCommand PreviewBatchCategoryCommand { get; private set; } = null!;
    public AsyncRelayCommand SaveBatchCategoryCommand { get; private set; } = null!;
    public bool IsUnclassifiedView
    {
        get => _isUnclassifiedView;
        private set
        {
            if (!Set(ref _isUnclassifiedView, value)) return;
            SelectedExplorerItem = null;
            InvalidateCategoryReviews();
            Raise(nameof(IsExplorerTimelineView));
        }
    }
    public bool IsExplorerTimelineView => !IsUnclassifiedView;
    public bool CanQuickCategorize => SelectedExplorerItem is { ActivityCount: 1, IsIdle: false };
    public bool CanSaveQuickCategory => _quickReview is not null;
    public bool CanSaveBatchCategory => _batchReview is not null;
    public string QuickSourceStatus => CanQuickCategorize ? "完全一致。過去と今後の履歴に適用されます。" +
        (QuickCategoryScope == "配信者・チャンネル" ? " 表示名は同名・改名の影響を受けます。IDがあればIDを推奨します。"
            : SelectedExplorerItem!.IsWebsite && QuickCategoryScope is "アプリ" or "プロセス"
                ? " ブラウザの別サイトにも適用されます。サイト単位ならドメインを選んでください。" : string.Empty)
        : "Idleや複数の活動が混ざった行ではルールを作れません。1分表示などで単一の活動を選んでください。";
    public IReadOnlyList<string> QuickCategoryScopes
    {
        get
        {
            var item = SelectedExplorerItem;
            var scopes = new List<string> { "アプリ" };
            if (!string.IsNullOrWhiteSpace(item?.ProcessName)) scopes.Add("プロセス");
            if (!string.IsNullOrWhiteSpace(item?.Domain)) scopes.Add("ドメイン");
            if (!string.IsNullOrWhiteSpace(item?.CreatorId)) scopes.Add("チャンネルID");
            if (!string.IsNullOrWhiteSpace(item?.Creator)) scopes.Add("配信者・チャンネル");
            return scopes;
        }
    }
    public string QuickCategory { get => _quickCategory; set { if (Set(ref _quickCategory, value)) InvalidateQuickReview(); } }
    public string QuickCategoryScope { get => _quickScope; set { if (Set(ref _quickScope, value)) { InvalidateQuickReview(); Raise(nameof(QuickSourceStatus)); } } }
    public int QuickCategoryPriority { get => _quickPriority; set { if (Set(ref _quickPriority, value)) InvalidateQuickReview(); } }
    public string QuickCategoryPreview { get => _quickPreviewText; private set => Set(ref _quickPreviewText, value); }
    public string BatchCategory { get => _batchCategory; set { if (Set(ref _batchCategory, value)) InvalidateBatchReview(); } }
    public string BatchCategoryPreview { get => _batchPreviewText; private set => Set(ref _batchPreviewText, value); }
    public string UnclassifiedStatus { get => _unclassifiedStatus; private set => Set(ref _unclassifiedStatus, value); }
    public IReadOnlyList<int> QuickPriorityChoices { get; } = [0, 100, 300, 500, 1000];

    private void InitializeCategoryTools()
    {
        ShowExplorerTimelineCommand = new(async _ => { IsUnclassifiedView = false; await SearchExplorerAsync(); }, HandleCommandError);
        ShowUnclassifiedCommand = new(async _ => { IsUnclassifiedView = true; await SearchExplorerAsync(); }, HandleCommandError);
        PreviewQuickCategoryCommand = new(async _ => await PreviewQuickCategoryAsync(), HandleCommandError);
        SaveQuickCategoryCommand = new(async _ => await SaveQuickCategoryAsync(), HandleCommandError);
        SetIntervalCategoryCommand = new(async _ =>
        {
            if (_isDeletingData) throw new InvalidOperationException("データ削除中は補正できません。");
            if (SelectedExplorerItem is null || SelectedExplorerItem.IsIdle) throw new InvalidOperationException("補正する通常活動を選択してください。");
            AnnotationCategory = QuickCategory;
            await SaveAnnotationAsync();
        }, HandleCommandError);
        PreviewBatchCategoryCommand = new(async parameter => await PreviewBatchCategoryAsync(parameter), HandleCommandError);
        SaveBatchCategoryCommand = new(async parameter => await SaveBatchCategoryAsync(parameter), HandleCommandError);
    }

    private void LoadQuickCategoryEditor(ExplorerTimelineItem? item)
    {
        InvalidateQuickReview();
        Raise(nameof(QuickCategoryScopes));
        QuickCategoryScope = !string.IsNullOrWhiteSpace(item?.CreatorId) ? "チャンネルID"
            : !string.IsNullOrWhiteSpace(item?.Domain) ? "ドメイン" : !string.IsNullOrWhiteSpace(item?.ProcessName) ? "プロセス" : "アプリ";
        if (item is not null) QuickCategory = item.Category;
        Raise(nameof(CanQuickCategorize)); Raise(nameof(QuickSourceStatus));
    }

    private CategoryRuleDraft BuildQuickDraft()
    {
        var item = SelectedExplorerItem;
        if (item is null || !CanQuickCategorize) throw new InvalidOperationException("単一の通常活動を選んでください。");
        if (!QuickCategoryScopes.Contains(QuickCategoryScope)) throw new InvalidOperationException("分類する対象を選んでください。");
        var (target, pattern) = QuickCategoryScope switch
        {
            "プロセス" => (CategoryRuleTarget.ProcessName, ActivityCategorizer.NormalizeProcessName(item.ProcessName).ToLowerInvariant()),
            "ドメイン" => (CategoryRuleTarget.Domain, item.Domain.Trim().TrimEnd('.').ToLowerInvariant()),
            "チャンネルID" => (CategoryRuleTarget.CreatorId, item.CreatorId),
            "配信者・チャンネル" => (CategoryRuleTarget.Creator, item.Creator),
            _ => (CategoryRuleTarget.Application, item.AppName)
        };
        return new(target, pattern.Trim(), SettingsService.NormalizeCategoryPath(QuickCategory), Math.Clamp(QuickCategoryPriority, 0, 1000));
    }

    private async Task PreviewQuickCategoryAsync()
    {
        if (_isDeletingData) throw new InvalidOperationException("データ削除中は分類を変更できません。");
        var item = SelectedExplorerItem;
        var draft = BuildQuickDraft();
        var (start, end) = GetExplorerRangeBounds();
        var review = await Task.Run(() => _explorerService.PreviewCategoryRules([draft], start, end));
        if (!IsExplorer || IsUnclassifiedView || SelectedExplorerItem != item || BuildQuickDraft() != draft ||
            GetExplorerRangeBounds() != (start, end)) return;
        _quickReview = review;
        QuickCategoryPreview = DescribeReview(review) + $"\n対象: {draft.Pattern}\n分類: {draft.Category}（優先度 {draft.Priority}）";
        Raise(nameof(CanSaveQuickCategory));
    }

    private async Task SaveQuickCategoryAsync()
    {
        if (_isDeletingData) throw new InvalidOperationException("データ削除中は分類を変更できません。");
        var review = _quickReview ?? throw new InvalidOperationException("先にプレビューしてください。");
        if (BuildQuickDraft() != review.Drafts.Single() || GetExplorerRangeBounds() != (review.Start, review.End))
            throw new InvalidOperationException("条件が変わりました。再度プレビューしてください。");
        await Task.Run(() => _explorerService.ApplyCategoryRules(review));
        InvalidateQuickReview(); LoadCategoryRules();
        StatusMessage = "分類ルールを保存しました。元の履歴と手動補正は保持しています。";
        await SearchExplorerAsync();
    }

    private async Task LoadUnclassifiedAsync()
    {
        CancelRefresh();
        var refresh = new CancellationTokenSource();
        _refreshCancellation = refresh;
        var selectedDate = SelectedDate;
        var (start, end) = GetExplorerRangeBounds();
        IsLoading = true;
        InvalidateBatchReview();
        try
        {
            var entries = await Task.Run(() => _explorerService.GetUnclassified(start, end), refresh.Token);
            refresh.Token.ThrowIfCancellationRequested();
            if (!CanApplyRefresh(refresh, "Explorer", selectedDate) || !IsUnclassifiedView || GetExplorerRangeBounds() != (start, end)) return;
            Replace(UnclassifiedItems, entries.Select(item => new UnclassifiedItem(item.Target, item.Pattern,
                item.Name, FormatDuration(item.Seconds), item.SessionCount, item.Seconds)));
            UnclassifiedStatus = entries.Count == 0 ? "この期間に未分類の通常活動はありません。"
                : $"未分類 {entries.Count:N0}対象・{FormatDuration(entries.Sum(item => item.Seconds))}（時間の多い順）";
            ExplorerStatus = "未分類の整理では選択した期間全体を対象にします。検索文字・集中などの条件は使用しません。";
        }
        catch (OperationCanceledException) { }
        finally
        {
            refresh.Dispose();
            if (ReferenceEquals(_refreshCancellation, refresh)) { _refreshCancellation = null; IsLoading = false; }
        }
    }

    private IReadOnlyList<CategoryRuleDraft> BuildBatchDrafts(object? parameter)
    {
        var selected = (parameter as IEnumerable)?.OfType<UnclassifiedItem>().ToArray() ?? [];
        if (selected.Length is < 1 or > 100) throw new ArgumentException("未分類の項目を1～100件選択してください。");
        return selected.Select(item => new CategoryRuleDraft(item.Target, item.Pattern, SettingsService.NormalizeCategoryPath(BatchCategory))).ToArray();
    }

    private async Task PreviewBatchCategoryAsync(object? parameter)
    {
        if (_isDeletingData) throw new InvalidOperationException("データ削除中は分類を変更できません。");
        var drafts = BuildBatchDrafts(parameter);
        var (start, end) = GetExplorerRangeBounds();
        var review = await Task.Run(() => _explorerService.PreviewCategoryRules(drafts, start, end));
        if (!IsExplorer || !IsUnclassifiedView || !BuildBatchDrafts(parameter).SequenceEqual(drafts) ||
            GetExplorerRangeBounds() != (start, end)) return;
        _batchReview = review;
        BatchCategoryPreview = DescribeReview(review) + $"\n{review.Drafts.Count}対象 → {BatchCategory}";
        Raise(nameof(CanSaveBatchCategory));
    }

    private async Task SaveBatchCategoryAsync(object? parameter)
    {
        if (_isDeletingData) throw new InvalidOperationException("データ削除中は分類を変更できません。");
        var review = _batchReview ?? throw new InvalidOperationException("先にプレビューしてください。");
        if (!BuildBatchDrafts(parameter).SequenceEqual(review.Drafts) || GetExplorerRangeBounds() != (review.Start, review.End))
            throw new InvalidOperationException("選択・分類・期間が変わりました。再度プレビューしてください。");
        await Task.Run(() => _explorerService.ApplyCategoryRules(review));
        InvalidateBatchReview(); LoadCategoryRules();
        StatusMessage = $"{review.Drafts.Count}件の分類ルールを一括保存しました。元の履歴は変更していません。";
        await LoadUnclassifiedAsync();
    }

    private static string DescribeReview(CategoryRuleReview review) =>
        $"この期間: {review.SessionCount:N0}件・{FormatDuration(review.MatchingSeconds)}\n分類変更: {FormatDuration(review.ChangedSeconds)}／手動補正を維持: {FormatDuration(review.ProtectedSeconds)}\n既存ルールも一致: {review.ConflictCount:N0}セッション\nルールは過去・今後の全期間に適用。期間外の件数はこのプレビューに含みません。";
    private void InvalidateQuickReview()
    {
        _quickReview = null; QuickCategoryPreview = "対象とカテゴリを選び、影響をプレビューしてください。";
        Raise(nameof(CanSaveQuickCategory));
    }
    private void InvalidateBatchReview()
    {
        _batchReview = null; BatchCategoryPreview = "項目・カテゴリを選んで影響をプレビューしてください。";
        Raise(nameof(CanSaveBatchCategory));
    }
    private void InvalidateCategoryReviews() { InvalidateQuickReview(); InvalidateBatchReview(); }
}
