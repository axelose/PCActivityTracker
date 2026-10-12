using System.Collections;
using PCActivityTracker.Services;

namespace PCActivityTracker.ViewModels;

public sealed partial class MainViewModel
{
    private string _aiCategoryStatus = "未分類から1～20件選択して手動生成。候補は自動適用されません。";
    private CategoryRuleReview? _aiCategoryReview;
    public BulkObservableCollection<AiCategorySuggestion> AiCategorySuggestions { get; } = new();
    public string AiCategoryStatus { get => _aiCategoryStatus; private set => Set(ref _aiCategoryStatus, value); }
    public bool CanSaveAiCategories => _aiCategoryReview is not null;
    public AsyncRelayCommand SuggestAiCategoriesCommand { get; private set; } = null!;
    public AsyncRelayCommand PreviewAiCategoriesCommand { get; private set; } = null!;
    public AsyncRelayCommand SaveAiCategoriesCommand { get; private set; } = null!;

    private void InitializeAiCategoryTools()
    {
        SuggestAiCategoriesCommand = new(GenerateAiCategoriesAsync, HandleCommandError);
        PreviewAiCategoriesCommand = new(async parameter =>
        {
            if (_isDeletingData || IsAiGenerating) return;
            var drafts = SelectedAiDrafts(parameter);
            var (start, end) = GetExplorerRangeBounds();
            var review = await Task.Run(() => _explorerService.PreviewCategoryRules(drafts, start, end));
            if (!IsExplorer || !IsUnclassifiedView || !SelectedAiDrafts(parameter).SequenceEqual(drafts) || GetExplorerRangeBounds() != (start, end)) return;
            _aiCategoryReview = review;
            AiCategoryStatus = DescribeReview(review);
            Raise(nameof(CanSaveAiCategories));
        }, HandleCommandError);
        SaveAiCategoriesCommand = new(async parameter =>
        {
            if (_isDeletingData || IsAiGenerating) return;
            var review = _aiCategoryReview ?? throw new InvalidOperationException("選択した候補の影響を先にプレビューしてください。");
            if (!SelectedAiDrafts(parameter).SequenceEqual(review.Drafts) || GetExplorerRangeBounds() != (review.Start, review.End))
                throw new InvalidOperationException("候補の選択が変わりました。再度プレビューしてください。");
            await Task.Run(() => _explorerService.ApplyCategoryRules(review));
            ClearAiCategorySuggestions();
            AiCategoryStatus = "承認したルールを保存しました。元の自動記録は変更していません。";
            await LoadUnclassifiedAsync();
        }, HandleCommandError);
    }

    private IReadOnlyList<CategoryRuleDraft> SelectedAiDrafts(object? parameter)
    {
        var items = (parameter as IEnumerable)?.Cast<object>().OfType<AiCategorySuggestion>().ToArray() ?? [];
        if (items.Length is < 1 or > 20 || items.Any(item => !AiCategorySuggestions.Contains(item)))
            throw new ArgumentException("現在表示されているAI候補を1～20件選択してください。");
        return items.Select(item => item.Draft).ToArray();
    }

    private void ClearAiCategorySuggestions()
    {
        _aiCategoryReview = null;
        AiCategorySuggestions.Clear();
        Raise(nameof(CanSaveAiCategories));
    }

    private async Task GenerateAiCategoriesAsync(object? parameter)
    {
        if (IsAiGenerating || _isDeletingData || !IsExplorer || !IsUnclassifiedView) return;
        var selected = (parameter as IEnumerable)?.Cast<object>().OfType<UnclassifiedItem>().ToArray() ?? [];
        if (selected.Length is < 1 or > 20 || selected.Any(item => !UnclassifiedItems.Contains(item)))
            throw new ArgumentException("現在の未分類項目を1～20件選択してください。");
        if (string.IsNullOrWhiteSpace(OllamaModel)) throw new ArgumentException("設定でローカルOllamaモデルを選択・保存してください。");
        var safetySettings = BuildAiSafetySettings();
        // Classification always opts into the game safeguard, irrespective of report preferences.
        safetySettings.GameSafeModeEnabled = true;
        if (_gameSafetyService.Evaluate(safetySettings).IsGameForeground)
        { AiCategoryStatus = "ゲームがForegroundのためAI分類を開始しませんでした。"; return; }
        var range = GetExplorerRangeBounds();
        var entries = selected.Select(item => new UnclassifiedEntry(item.Target, item.Pattern, item.Name, item.Seconds, item.SessionCount)).ToArray();
        ClearAiCategorySuggestions();
        using var cancellation = new CancellationTokenSource();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _aiGenerationCancellation = cancellation; _aiGenerationStopped = stopped;
        _gameCancellationProcess = null; IsAiGenerating = true;
        AiCategoryStatus = "名称だけをローカルOllamaで分類中（最大2分、空きGPUのみ、終了後解放）。";
        var monitor = MonitorGameAndCancelAsync(safetySettings, cancellation);
        try
        {
            var unknown = await Task.Run(() => _explorerService.GetUnclassified(range.Start, range.End), cancellation.Token);
            if (entries.Any(item => !unknown.Any(current => current.Target == item.Target && current.Pattern == item.Pattern)))
                throw new InvalidOperationException("選択対象が未分類ではなくなりました。検索で更新してください。");
            // Preflight establishes a stale-data fingerprint before submitting any labels.
            var evidence = await Task.Run(() => _explorerService.PreviewCategoryRules(
                entries.Select(item => new CategoryRuleDraft(item.Target, item.Pattern, "Other")).ToArray(), range.Start, range.End), cancellation.Token);
            var suggestions = await new AiCategorySuggestionService().SuggestAsync(entries, OllamaEndpoint, OllamaModel, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!IsExplorer || !IsUnclassifiedView || GetExplorerRangeBounds() != range) return;
            var current = await Task.Run(() => _explorerService.PreviewCategoryRules(evidence.Drafts, range.Start, range.End), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (current.Fingerprint != evidence.Fingerprint)
            { AiCategoryStatus = "生成中に履歴・ルールが変更されました。未分類を更新して再実行してください。"; return; }
            Replace(AiCategorySuggestions, suggestions);
            AiCategoryStatus = suggestions.Count == 0 ? "分類可能な候補はありません。手動分類をご利用ください。" :
                $"{suggestions.Count}件の未適用候補。名称からの推測であり、内容・目的は未確認です。選択してプレビュー→承認してください。";
        }
        catch (OperationCanceledException) { AiCategoryStatus = "生成を中止しました（キャンセル・ゲーム検出・時間制限）。適用していません。"; }
        catch (Exception error) { AiCategoryStatus = "AI分類できませんでした: " + error.Message; }
        finally
        {
            cancellation.Cancel();
            try { await monitor; } catch (OperationCanceledException) { }
            if (ReferenceEquals(_aiGenerationCancellation, cancellation)) { _aiGenerationCancellation = null; IsAiGenerating = false; }
            if (ReferenceEquals(_aiGenerationStopped, stopped)) _aiGenerationStopped = null;
            stopped.TrySetResult();
        }
    }
}
