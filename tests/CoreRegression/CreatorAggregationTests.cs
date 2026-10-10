using PCActivityTracker.Domain;
using PCActivityTracker.Services;

namespace CoreRegression;
internal static class CreatorAggregationTests
{
    internal static void Run()
    {
        using var fixture = new Fixture();
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var id = "youtube:channel:UC" + new string('a', 22);
        ActivitySession Add(int minute, string name, string? identity, string domain = "youtube.com")
        {
            var session = new ActivitySession { StartTime = start.AddMinutes(minute), EndTime = start.AddMinutes(minute + 1),
                ActivityType = ActivityType.Website, AppName = "Chrome", ProcessName = "chrome",
                Domain = domain, CreatorName = name, CreatorId = identity };
            fixture.Activities.Save(session);
            return session;
        }
        var first = Add(0, "Before Rename", id);
        Add(1, "After Rename", id);
        Add(2, "After Rename", "twitch:login:other", "twitch.tv");
        Add(3, "Legacy Name", null);
        var totals = fixture.Activities.GetCreatorUsage(start, start.AddMinutes(10));
        Check.That(totals.Count == 3, "Identifiers and legacy rows grouped incorrectly.");
        Check.That(totals.Single(item => item.Identity == id).Seconds == 120, "Channel rename split the ranking.");
        Check.That(totals.Single(item => item.Identity == id).Name == "After Rename", "Latest display name not used.");
        first.EndTime = start.AddSeconds(30);
        fixture.Activities.Save(first);
        Check.That(fixture.Activities.GetRange(start, start.AddMinutes(10))[0].CreatorId == id, "Identifier lost during session update.");
        var report = new AggregationService(fixture.Activities).BuildReport(start.AddSeconds(15), start.AddMinutes(2), false);
        Check.That(report.Creators.Single().Seconds == 75 && report.Creators.Single().Identity == id, "Clipped creator usage incorrect.");
        var explorer = new ActivityExplorerService(fixture.Activities, fixture.Annotations, fixture.Settings);
        Check.That(explorer.Search(new ActivitySearchQuery(start, start.AddMinutes(10), Creator: id)).RawSessionCount == 2,
            "Stable ID drilldown missed renamed sessions.");
        Check.That(first.IsSameActivity(new ActivitySession { ActivityType = ActivityType.Website, AppName = "Chrome", ProcessName = "chrome",
            Domain = "youtube.com", CreatorName = "Before Rename", CreatorId = "youtube:handle:@different", Category = first.Category }) == false,
            "Different identifiers were merged into a session.");
        fixture.Activities.DeleteAll();
        Check.That(fixture.Activities.GetCreatorUsage(start, start.AddDays(1)).Count == 0, "Identifiers survived full history deletion.");
    }
}
