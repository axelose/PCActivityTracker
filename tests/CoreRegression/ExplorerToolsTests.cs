using System.Text.Json;
using LocalAi.Runtime;
using PCActivityTracker.Data;
using PCActivityTracker.Domain;
using PCActivityTracker.Services;

namespace CoreRegression;

internal static class ExplorerToolsTests
{
    internal static void Run()
    {
        using var fixture = new Fixture();
        var start = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddMinutes(5);
        var idA = "youtube:channel:UC" + new string('A', 22);
        var idB = "youtube:channel:UC" + new string('a', 22);
        void Add(DateTimeOffset from, int seconds, string id, string app = "Chrome", string process = "chrome", string? domain = "youtube.com") =>
            fixture.Activities.Save(new ActivitySession { StartTime = from, EndTime = from.AddSeconds(seconds),
                ActivityType = domain is null ? ActivityType.Application : ActivityType.Website, AppName = app, ProcessName = process,
                Domain = domain, CreatorId = id, CreatorName = "Synthetic", WindowTitle = "PRIVATE TITLE", Url = "https://example.test/private?secret=yes" });
        Add(start, 180, idA); Add(start.AddMinutes(3), 120, idB);
        Add(start.AddMinutes(-5), 60, idA); Add(start.AddMinutes(-4), 240, idB);
        fixture.Settings.AddCategoryRule(CategoryRuleTarget.CreatorId, idA, "Education", CategoryMatchMode.Exact, priority: 300);
        fixture.Annotations.Save(new ActivityAnnotation { StartTime = start.AddMinutes(1), EndTime = start.AddMinutes(2), CategoryOverride = "Research" });
        var explorer = new ActivityExplorerService(fixture.Activities, fixture.Annotations, fixture.Settings);
        var comparison = explorer.Compare(new(start, end));
        var bucket = comparison.Current.Entries.Single();
        var parts = bucket.Parts!;
        var categories = bucket.Categories!;
        Check.That(bucket.ActivityCount == 4 && bucket.Parts!.Count == 4, "Mixed channels or annotation pieces collapsed.");
        Check.That(categories.Sum(item => item.Seconds) == 300 && parts.Sum(item => item.Seconds) == 300 &&
            comparison.Current.MatchingSeconds == 300, "Mixed bucket time was lost or double-counted.");
        Check.That(categories.Single(item => item.Category == "Education").Seconds == 120 &&
            categories.Single(item => item.Category == "Research").Seconds == 60 &&
            categories.Single(item => item.Category == "Entertainment").Seconds == 120, "Category split ignored current rules/manual override.");
        Check.That(parts.First().CreatorId == idA && parts.Last().CreatorId == idB && parts.First().Start == start &&
            parts.Last().End == end, "Original identity/clipped interval lost.");
        Check.That(comparison.PreviousStart == start.AddMinutes(-5) && comparison.PreviousEnd == start &&
            comparison.Previous.Categories!.Single(item => item.Category == "Education").Seconds == 60 && comparison.Previous.Entries.Count == 0,
            "Previous period used different bounds or allocated unnecessary rows.");
        var filtered = explorer.Compare(new(start, end, Category: "Education", IncludeIdle: false));
        Check.That(filtered.Current.MatchingSeconds == 120 && filtered.Previous.MatchingSeconds == 60, "Filters not shared by comparison periods.");
        Check.That(explorer.Search(new(start, end, ExcludedCategory: "Entertainment")).MatchingSeconds == 180, "Excluded category filter failed.");
        var raw = fixture.Activities.GetRange(start, end);
        Check.That(raw.Count == 2 && raw.All(item => item.WindowTitle == "PRIVATE TITLE"), "Explorer modified automatic sessions.");
        var fractional = start.AddDays(2).AddMilliseconds(500);
        Add(fractional, 120, "", "Custom", "custom", null);
        var precise = explorer.Search(new(fractional, fractional.AddSeconds(120), ZoomMinutes: 1));
        Check.That(precise.Entries.Sum(item => item.Seconds) == precise.MatchingSeconds && precise.MatchingSeconds == 120,
            "Fractional bucket boundary lost seconds.");
        var longStart = start.AddDays(3);
        Add(longStart, 2100 * 60, "", "Custom", "custom", null);
        var capped = explorer.Search(new(longStart, longStart.AddMinutes(2100), ZoomMinutes: 1));
        Check.That(capped.IsTruncated && capped.Entries.Count == 2000 && capped.Categories!.Sum(item => item.Seconds) == 2100 * 60,
            "Truncation changed full-result category totals.");

        var repository = new SettingsRepository(fixture.Database);
        var store = fixture.Settings.ExplorerSearches;
        var query = new ActivitySearchQuery(start, end, 15, "search text", "app", "site", "creator", "Education", "project", false, true, true, "Gaming");
        store.Save(new(" Learning ", "7日", query));
        var loaded = new SavedExplorerSearchStore(repository).Read().Single();
        Check.That(loaded.Name == "Learning" && loaded.Conditions == query with { Start = DateTimeOffset.UnixEpoch, End = DateTimeOffset.UnixEpoch },
            "Saved filters changed or retained fixed dates.");
        store.Save(loaded with { Name = "learning", Range = "1か月" });
        Check.That(store.Read().Count == 1 && store.Read()[0].Range == "1か月", "Same-name update duplicated preference.");
        store.Delete("LEARNING"); Check.That(store.Read().Count == 0, "Saved search deletion failed.");
        for (var index = 0; index < 20; index++) store.Save(new("Search " + index, "1日", query));
        fixture.Activities.DeleteAll();
        Check.That(store.Read().Count == 20, "History deletion removed explicit search preferences.");
        Reject(() => store.Save(new("Twenty first", "1日", query)));
        Reject(() => store.Save(new("Invalid", "1日", query with { Text = new string('x', 1001) })));
        repository.Set("ExplorerSavedSearchesV1", "BROKEN JSON");
        Reject(() => store.Read()); Reject(() => store.Save(new("No overwrite", "1日", query)));
        Check.That(repository.GetSettings()["ExplorerSavedSearchesV1"] == "BROKEN JSON", "Corrupt preference was silently overwritten.");

        TestAiAsync().GetAwaiter().GetResult();
    }

    private static async Task TestAiAsync()
    {
        var service = new AiCategorySuggestionService();
        UnclassifiedEntry[] entries = [new(CategoryRuleTarget.ProcessName, "custom-editor", "PRIVATE DISPLAY TITLE", 120, 1),
            new(CategoryRuleTarget.Application, "https://example.test/private?token=SECRET", "PRIVATE URL", 60, 1)];
        OllamaGenerateRequest? captured = null;
        Task<OllamaGenerateResult> Generate(OllamaGenerateRequest request, CancellationToken token)
        {
            captured = request; token.ThrowIfCancellationRequested();
            return Task.FromResult(new OllamaGenerateResult("{\"suggestions\":[{\"index\":0,\"category\":\"Development\"},{\"index\":1,\"category\":\"Research\"}]}", "fake", 0, 0, 0, true));
        }
        var result = await service.SuggestWithClientAsync(entries, "fake", Generate, CancellationToken.None);
        Check.That(result.Count == 1 && result[0].Draft.Pattern == "custom-editor" && result[0].Draft.Category == "Development", "AI invented target or classified redacted label.");
        Check.That(captured is { RequireIdleRuntime: true, UnloadAfterCompletion: true, Temperature: 0, MaximumOutputTokens: 1000 } &&
            captured.Timeout == TimeSpan.FromMinutes(2) && captured.ResponseFormat!.Value.ValueKind == JsonValueKind.Object &&
            !captured.Prompt.Contains("SECRET") && !captured.Prompt.Contains("PRIVATE") && !captured.Prompt.Contains("https://"), "AI privacy/GPU/request bounds regressed.");
        async Task RejectResponse(string json, string? doneReason = null)
        {
            try
            {
                await service.SuggestWithClientAsync(entries, "fake", (_, _) => Task.FromResult(new OllamaGenerateResult(json, "fake", 0, 0, 0, true, doneReason)), CancellationToken.None);
                throw new Exception("Invalid AI output was accepted: " + json);
            }
            catch (InvalidOperationException) { }
        }
        await RejectResponse("{\"suggestions\":[{\"index\":0,\"category\":\"Development\"},{\"index\":0,\"category\":\"Research\"}]}");
        await RejectResponse("{\"suggestions\":[{\"index\":9,\"category\":\"Development\"}]}");
        await RejectResponse("{\"suggestions\":[{\"index\":0,\"category\":\"New category\"}]}");
        await RejectResponse("{\"suggestions\":[{\"index\":0,\"category\":\"Development\",\"pattern\":\"*\"}]}");
        await RejectResponse("{\"suggestions\":[]}", "length");
        await RejectResponse("not JSON");
        var abstained = await service.SuggestWithClientAsync(entries, "fake", (_, _) => Task.FromResult(new OllamaGenerateResult(
            "{\"suggestions\":[{\"index\":0,\"category\":\"Other\"}]}", "fake", 0, 0, 0, true)), CancellationToken.None);
        Check.That(abstained.Count == 0, "Other abstention became a persistent rule.");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await service.SuggestWithClientAsync(entries, "fake", Generate, cancellation.Token); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { }
        try { await service.SuggestAsync(entries, "https://external.example", "fake", CancellationToken.None); throw new Exception("External AI endpoint accepted."); }
        catch (ArgumentException) { }
    }

    private static void Reject(Action action)
    {
        try { action(); throw new Exception("Invalid preference accepted."); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { }
    }
}
