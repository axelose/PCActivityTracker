using PCActivityTracker.Data;
using PCActivityTracker.Domain;
using PCActivityTracker.Services;

namespace CoreRegression;

internal static class CategorizationTests
{
    internal static void Run()
    {
        static void Category(string expected, string app, string? domain = null, string? process = null) =>
            Check.That(ActivityCategorizer.Categorize(app, domain, false, processName: process) == expected,
                $"Classification mismatch: {app}/{domain}/{process}.");
        Category("Development", "Visual Studio Code");
        Category("Development", "Localized editor", process: "Code.exe");
        Category("Other", "decode-helper");
        Category("Other", "Slack Off");
        Category("Other", "Chrome", "github.com.evil.test");
        Category("Other", "Chrome", "notyoutube.com");
        Category("Other", "Chrome", "docs.unknown.test");
        Category("Research", "Chrome", "DOCS.GITHUB.COM.");
        Category("Development", "Chrome", "github.com");
        Category("Entertainment", "Chrome", "www.youtube.com");
        Category("Gaming", "deadlock", process: "deadlock");
        Category("Gaming", "Game", process: "VALORANT-Win64-Shipping.exe");
        Category("Other", "Visual Studio Code", "unknown.test", "code");
        Category("Other", "Brave");
        Check.That(ActivityCategorizer.NormalizeProcessName("Host.Editor.exe") == "Host.Editor" &&
            ActivityCategorizer.NormalizeProcessName("Host.Editor") == "Host.Editor", "Dotted process identity changed.");
        var exact = new CategoryRule { Target = CategoryRuleTarget.Domain, Pattern = "youtube.com", Category = "Education", MatchMode = CategoryMatchMode.Exact };
        Check.That(ActivityCategorizer.MatchesRule(exact, "Chrome", "youtube.com", null) &&
            !ActivityCategorizer.MatchesRule(exact, "Chrome", "www.youtube.com", null), "Exact domain silently broadened.");
        exact.MatchMode = CategoryMatchMode.Wildcard;
        Check.That(ActivityCategorizer.MatchesRule(exact, "Chrome", "www.youtube.com", null) &&
            !ActivityCategorizer.MatchesRule(exact, "Chrome", "youtube.com.evil.test", null), "Legacy domain/subdomain rule broke.");

        using var fixture = new Fixture();
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddHours(1);
        var idA = "youtube:channel:UC" + new string('A', 22);
        var idB = "youtube:channel:UC" + new string('a', 22);
        ActivitySession Add(int minute, int duration, string app, string process, string? domain = null,
            string? creatorId = null, string? name = null, bool idle = false)
        {
            var session = new ActivitySession { StartTime = start.AddMinutes(minute), EndTime = start.AddMinutes(minute + duration),
                ActivityType = domain is null ? ActivityType.Application : ActivityType.Website, AppName = app,
                ProcessName = process, Domain = domain, CreatorId = creatorId, CreatorName = name,
                IsIdle = idle, Category = "Other", Url = domain is null ? null : "https://" + domain + "/private",
                WindowTitle = "SYNTHETIC PRIVATE TITLE" };
            fixture.Activities.Save(session); return session;
        }
        var oldName = Add(0, 5, "Chrome", "chrome", "www.youtube.com", idA, "Old display name");
        Add(5, 5, "Chrome", "chrome", "www.youtube.com", idA, "Renamed channel");
        Add(10, 5, "Chrome", "chrome", "www.youtube.com", idB, "Renamed channel");
        var unknown = Add(15, 10, "Custom Editor", "Host.Editor");
        Add(25, 5, "Chrome", "chrome", "unknown.test");
        Add(30, 5, "Idle", "", idle: true);
        var explorer = new ActivityExplorerService(fixture.Activities, fixture.Annotations, fixture.Settings);
        var aggregator = new AggregationService(fixture.Activities, fixture.Settings, fixture.Annotations);
        var before = fixture.Activities.GetRange(start, end).Select(item => (item.Id, item.Category, item.Url, item.WindowTitle)).ToArray();

        fixture.Settings.AddCategoryRule(CategoryRuleTarget.Domain, "youtube.com", "Entertainment", priority: 100);
        var review = explorer.PreviewCategoryRules([new(CategoryRuleTarget.CreatorId, idA, "Education")], start, end);
        Check.That(review.SessionCount == 2 && review.MatchingSeconds == 600 && review.ChangedSeconds == 600 && review.ConflictCount == 2,
            "ID review matched wrong channel or miscounted conflicts.");
        explorer.ApplyCategoryRules(review);
        Check.That(aggregator.BuildReport(start, end, false).Categories.Single(item => item.Name == "Education").Seconds == 600,
            "Renamed channel split classification or report totals diverged.");
        fixture.Settings.AddCategoryRule(CategoryRuleTarget.CreatorId, idB, "Research", CategoryMatchMode.Exact);
        var loaded = new SettingsService(new SettingsRepository(fixture.Database)); loaded.Load();
        Check.That(loaded.CategoryRules.Count(rule => rule.Target == CategoryRuleTarget.CreatorId) == 2 &&
            loaded.CategoryRules.Any(rule => rule.Pattern == idA) && loaded.CategoryRules.Any(rule => rule.Pattern == idB),
            "SQLite NOCASE merged case-sensitive channel IDs.");
        Check.That(ActivityCategorizer.Evaluate(oldName, loaded.CategoryRules).Category == "Education",
            "Stored stable ID rule was not decoded or prioritized.");

        fixture.Annotations.Save(new ActivityAnnotation { StartTime = start.AddMinutes(17), EndTime = start.AddMinutes(19), CategoryOverride = "Manual" });
        var unknowns = explorer.GetUnclassified(start, end);
        Check.That(unknowns.Count == 2 && unknowns[0].Pattern == "host.editor" && unknowns[0].Seconds == 480 && unknowns[0].SessionCount == 1,
            "Unknown review included manual time, Idle or known sites, or failed duration ordering.");
        var batch = explorer.PreviewCategoryRules(unknowns.Select(item => new CategoryRuleDraft(item.Target, item.Pattern, "Work > Custom")).ToArray(), start, end);
        Check.That(batch.ChangedSeconds == 780 && batch.ProtectedSeconds == 120 && batch.SessionCount == 2,
            "Bulk review did not protect manual annotations.");
        explorer.ApplyCategoryRules(batch);
        Check.That(explorer.GetUnclassified(start, end).Count == 0, "Classified rows remained in unknown list.");
        var report = aggregator.BuildReport(start, end, false);
        Check.That(report.Categories.Single(item => item.Name == "Manual").Seconds == 120 &&
            report.Categories.Single(item => item.Name == "Work > Custom").Seconds == 780 &&
            report.Categories.Sum(item => item.Seconds) == report.TotalPcSeconds, "Manual priority or conservation of active time failed.");
        Check.That(fixture.Activities.GetRange(start, end).Select(item => (item.Id, item.Category, item.Url, item.WindowTitle)).SequenceEqual(before),
            "Classification rewrote the original history.");

        foreach (var rule in fixture.Settings.CategoryRules.Where(rule => rule.Category == "Work > Custom")) fixture.Settings.RemoveCategoryRule(rule.Id);
        Check.That(explorer.GetUnclassified(start, end).Count == 2, "Deleting rules did not revert unknown classification.");
        var stale = explorer.PreviewCategoryRules([new(CategoryRuleTarget.ProcessName, "host.editor", "Work")], start, end);
        fixture.Settings.AddCategoryRule(CategoryRuleTarget.Application, "Custom Editor", "Other", CategoryMatchMode.Exact);
        Reject(() => explorer.ApplyCategoryRules(stale));
        Check.That(fixture.Settings.CategoryRules.All(rule => rule.Target != CategoryRuleTarget.ProcessName), "Stale review partially saved rules.");
        Check.That(explorer.GetUnclassified(start, end).All(item => item.Pattern != "host.editor"), "Explicit Other rule was treated as unclassified.");
        fixture.Settings.RemoveCategoryRule(fixture.Settings.CategoryRules.Single(rule => rule.Target == CategoryRuleTarget.Application).Id);
        stale = explorer.PreviewCategoryRules([new(CategoryRuleTarget.ProcessName, "host.editor", "Work")], start, end);
        unknown.EndTime = unknown.EndTime.AddMinutes(1); fixture.Activities.Save(unknown);
        Reject(() => explorer.ApplyCategoryRules(stale));
        Check.That(fixture.Settings.CategoryRules.All(rule => rule.Target != CategoryRuleTarget.ProcessName), "Changed history review was applied.");
        var count = fixture.Settings.CategoryRules.Count;
        Reject(() => fixture.Settings.AddCategoryRules([new(CategoryRuleTarget.ProcessName, "safe", "Work"), new(CategoryRuleTarget.ProcessName, "", "Work")]));
        Check.That(fixture.Settings.CategoryRules.Count == count, "Invalid batch partially saved rules.");
        Reject(() => explorer.PreviewCategoryRules(Enumerable.Range(0, 101).Select(index => new CategoryRuleDraft(CategoryRuleTarget.Application, "app" + index, "Work")).ToArray(), start, end));
        Check.That(ActivityCategorizer.Evaluate(new ActivitySession { IsIdle = true, AppName = "Custom Editor" },
            [new CategoryRule { Pattern = "*", Category = "Work" }]).Category == "Idle", "A broad rule reclassified Idle.");
        var clipped = fixture.Activities.GetClassificationUsage(start.AddMinutes(1), start.AddMinutes(4));
        Check.That(clipped.Sum(item => item.Seconds) == 180, "Grouped labels overcounted clipped time.");
        Add(35, 5, "Chrome", "chrome", "youtube.com");
        var sameKey = explorer.PreviewCategoryRules([new(CategoryRuleTarget.Domain, "YOUTUBE.COM", "Work")], start, end);
        Check.That(sameKey.SessionCount == 1 && sameKey.ConflictCount == 0,
            "Replacing case-insensitive domain key counted itself as a conflict.");
        var serializedLabels = System.Text.Json.JsonSerializer.Serialize(fixture.Activities.GetClassificationUsage(start, end));
        Check.That(!serializedLabels.Contains("/private") && !serializedLabels.Contains("PRIVATE TITLE"), "Grouped classification leaked URLs/titles.");
        stale = explorer.PreviewCategoryRules([new(CategoryRuleTarget.ProcessName, "host.editor", "Work")], start, end);
        var annotation = fixture.Annotations.GetRange(start, end).Single(); annotation.CategoryOverride = "Changed Manual";
        fixture.Annotations.Save(annotation);
        Reject(() => explorer.ApplyCategoryRules(stale));
        var repository = new SettingsRepository(fixture.Database);
        count = repository.GetCategoryRules().Count;
        try
        {
            repository.AddCategoryRules([new CategoryRule { Pattern = "atomic-safe", Category = "Work" }, new CategoryRule { Pattern = null!, Category = "Work" }]);
            throw new Exception("Invalid SQL batch was accepted.");
        }
        catch (InvalidOperationException) { }
        Check.That(repository.GetCategoryRules().Count == count, "SQL statement failure left a partially saved batch.");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { return; }
        throw new Exception("Invalid/stale category operation was accepted.");
    }
}
