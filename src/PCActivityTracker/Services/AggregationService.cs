using PCActivityTracker.Data;
using PCActivityTracker.Domain;

namespace PCActivityTracker.Services;

public sealed class AggregationService
{
    private static readonly TimeSpan FocusGap = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MinimumFocusBlock = TimeSpan.FromMinutes(25);
    private readonly ActivityRepository _repository;
    private readonly SettingsService? _settings;
    private readonly ActivityAnnotationRepository? _annotations;

    public AggregationService(ActivityRepository repository, SettingsService? settings = null,
        ActivityAnnotationRepository? annotations = null)
    {
        _repository = repository;
        _settings = settings;
        _annotations = annotations;
    }

    public ActivityReport BuildReport(DateTimeOffset start, DateTimeOffset end, bool includeTimeline = true)
    {
        var totals = _repository.GetTotals(start, end);
        var apps = _repository.GetAppUsage(start, end).Select(item => new UsageTotal(item.Name, item.Seconds)).ToList();
        var sites = _repository.GetSiteUsage(start, end).Select(item => new UsageTotal(item.Name, item.Seconds)).ToList();
        var creators = _repository.GetCreatorUsage(start, end).Select(item => new UsageTotal(item.Name, item.Seconds, item.Identity)).ToList();
        var categoryRules = _settings?.CategoryRules;
        var annotations = _annotations?.GetRange(start, end) ?? Array.Empty<ActivityAnnotation>();
        var useLiveCategories = (categoryRules?.Count ?? 0) > 0 || annotations.Any(item => !string.IsNullOrWhiteSpace(item.CategoryOverride));
        var sessions = includeTimeline || useLiveCategories
            ? _repository.GetRange(start, end)
                .Select(session => new ClippedActivity(session, Max(session.StartTime, start), Min(session.EndTime, end)))
                .Where(item => item.Seconds > 0)
                .ToList()
            : new List<ClippedActivity>();
        var pieces = includeTimeline || useLiveCategories
            ? sessions.SelectMany(item => SplitAnnotated(item, categoryRules, annotations)).ToList()
            : new List<AnnotatedPiece>();
        var categories = useLiveCategories
            ? pieces.Where(item => !item.Session.IsIdle)
                .GroupBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
                .Select(group => new UsageTotal(group.Key, group.Sum(item => item.Seconds)))
                .OrderByDescending(item => item.Seconds).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList()
            : _repository.GetCategoryUsage(start, end).Select(item => new UsageTotal(item.Name, item.Seconds)).ToList();
        IReadOnlyList<TimelineEntry> timeline = includeTimeline
            ? pieces.Select(item => new TimelineEntry(
                item.Start.LocalDateTime,
                item.End.LocalDateTime,
                item.Session.ActivityType == ActivityType.Website ? item.Session.Domain ?? item.Session.AppName : item.Session.AppName,
                BuildSubtitle(item.Session, item.Annotation),
                item.Seconds,
                item.Session.IsIdle,
                item.Session.ActivityType == ActivityType.Website)).ToList()
            : Array.Empty<TimelineEntry>();

        var daily = _repository.GetDailyUsage(start, end)
            .Select(item => new DailyTotal(item.DayStart.LocalDateTime.Date, item.Seconds, item.IdleSeconds))
            .ToList();

        return new ActivityReport(start, end, totals.PcSeconds, totals.IdleSeconds, apps, sites, creators, categories, daily, timeline);
    }

    public DashboardInsights BuildDashboardInsights(DateTimeOffset now)
    {
        var today = new DateTimeOffset(DateTime.SpecifyKind(now.LocalDateTime.Date, DateTimeKind.Local));
        var heatmapStart = today.AddDays(-6);
        var heatmapEnd = today.AddDays(1);
        var weekStartLocal = StartOfWeek(now.LocalDateTime);
        var currentWeekStart = new DateTimeOffset(DateTime.SpecifyKind(weekStartLocal, DateTimeKind.Local));
        var currentWeekEnd = now > currentWeekStart ? now : currentWeekStart;
        var previousWeekStart = currentWeekStart.AddDays(-7);
        var previousWeekEnd = previousWeekStart + (currentWeekEnd - currentWeekStart);

        var hourly = _repository.GetHourlyUsage(heatmapStart, heatmapEnd)
            .Select(item => new HourlyTotal(item.HourStart.LocalDateTime, item.Seconds))
            .ToList();
        var daily = _repository.GetDailyUsage(heatmapStart, heatmapEnd)
            .Select(item => new DailyTotal(item.DayStart.LocalDateTime.Date, item.Seconds, item.IdleSeconds))
            .ToList();
        var focus = CalculateFocus(_repository.GetIntervals(today, now), today, now);
        var currentWeek = _repository.GetTotals(currentWeekStart, currentWeekEnd).PcSeconds;
        var previousWeek = _repository.GetTotals(previousWeekStart, previousWeekEnd).PcSeconds;
        return new DashboardInsights(hourly, daily, focus, currentWeek, previousWeek,
            currentWeekStart, currentWeekEnd, previousWeekStart, previousWeekEnd);
    }

    public ReportAnalysis BuildReportAnalysis(DateTimeOffset start, DateTimeOffset end)
    {
        var report = BuildReport(start, end, includeTimeline: false);
        var hourly = _repository.GetHourlyUsage(start, end)
            .Select(item => new HourlyTotal(item.HourStart.LocalDateTime, item.Seconds))
            .ToList();
        var focus = CalculateFocus(_repository.GetIntervals(start, end), start, end);
        return new ReportAnalysis(report, hourly, focus);
    }

    public BehaviorAnalysis BuildBehaviorAnalysis(DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start) throw new ArgumentOutOfRangeException(nameof(end));
        var now = DateTimeOffset.Now;
        if (start >= now)
        {
            var emptyContext = CalculateContext(Array.Empty<AnalysisActivity>());
            var emptyQuality = CalculateFocusQuality(emptyContext, new FocusSummary(0, 0, 0));
            return new BehaviorAnalysis(emptyContext, emptyQuality,
                new PeriodComparison(0, 0, Array.Empty<ComparisonDelta>()), FocusDeepDive.Empty);
        }
        var effectiveEnd = now > start && now < end ? now : end;
        var duration = effectiveEnd - start;
        var previousStart = start - duration;
        var previousEnd = start;
        var currentSessions = _repository.GetRange(start, effectiveEnd);
        var previousSessions = _repository.GetRange(previousStart, previousEnd);
        var categoryRules = _settings?.CategoryRules;
        var currentActivities = BuildAnalysisActivities(currentSessions, start, effectiveEnd, categoryRules);
        var previousActivities = BuildAnalysisActivities(previousSessions, previousStart, previousEnd, categoryRules);
        var context = CalculateContext(currentActivities);
        var focus = CalculateFocus(currentSessions.Select(session =>
            new ActivityIntervalRow(session.StartTime, session.EndTime, session.IsIdle)).ToList(), start, effectiveEnd);
        var quality = CalculateFocusQuality(context, focus);
        var comparison = BuildComparison(currentActivities, previousActivities);
        var deepDive = BuildFocusDeepDive(currentActivities, quality.Components ?? Array.Empty<ScoreComponent>());
        return new BehaviorAnalysis(context, quality, comparison, deepDive);
    }

    internal static ContextSwitchSummary CalculateContext(IReadOnlyList<AnalysisActivity> source)
    {
        var activities = source.Where(item => item.End > item.Start)
            .OrderBy(item => item.Start).ToList();
        if (activities.Count == 0)
            return new ContextSwitchSummary(0, 0, 0, 0, 0, Array.Empty<UsageTotal>());

        var switchCount = 0;
        var destinations = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < activities.Count; index++)
        {
            var previous = activities[index - 1];
            var current = activities[index];
            if (current.Segment != previous.Segment || current.Start - previous.End > FocusGap ||
                string.Equals(previous.Identity, current.Identity, StringComparison.OrdinalIgnoreCase)) continue;
            switchCount++;
            destinations[current.Label] = destinations.GetValueOrDefault(current.Label) + 1;
        }

        var activeSeconds = activities.Sum(item => item.Seconds);
        var shortCount = activities.Count(item => item.Seconds < TimeSpan.FromMinutes(5).TotalSeconds);
        var averageSeconds = activeSeconds / Math.Max(1, activities.Count);
        var switchesPerHour = activeSeconds <= 0 ? 0 : switchCount / (activeSeconds / 3600d);
        var shortRate = shortCount * 100d / activities.Count;
        var topDestinations = destinations.OrderByDescending(item => item.Value)
            .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Take(8).Select(item => new UsageTotal(item.Key, item.Value)).ToList();
        return new ContextSwitchSummary(switchCount, switchesPerHour, activities.Count,
            averageSeconds, shortCount, topDestinations, shortRate, activeSeconds);
    }

    internal static FocusQualitySummary CalculateFocusQuality(ContextSwitchSummary context, FocusSummary focus)
    {
        if (context.ActiveSeconds < TimeSpan.FromMinutes(10).TotalSeconds)
            return new FocusQualitySummary(0, "データ不足", "採点には10分以上の実作業データが必要です", focus,
                Array.Empty<ScoreComponent>());

        var focusRatio = Math.Clamp(focus.TotalSeconds / (double)context.ActiveSeconds, 0, 1);
        var averageFactor = Math.Clamp(context.AverageSessionSeconds / 900d, 0, 1);
        var longestFactor = Math.Clamp(focus.LongestSeconds / 3600d, 0, 1);
        var switchPenalty = Math.Min(25, context.SwitchesPerHour * 4);
        var shortPenalty = Math.Min(15, context.ShortSessionRate * 0.15);
        var score = (int)Math.Round(Math.Clamp(45 + focusRatio * 30 + averageFactor * 10 +
            longestFactor * 15 - switchPenalty - shortPenalty, 0, 100));
        var grade = score switch
        {
            >= 85 => "非常に安定",
            >= 70 => "安定",
            >= 55 => "やや分散",
            >= 40 => "分散気味",
            _ => "細切れ"
        };
        var detail = $"集中 {focus.BlockCount}回・切替 {context.SwitchesPerHour:0.0}回/時・5分未満 {context.ShortSessionRate:0}%";
        var components = new[]
        {
            new ScoreComponent("基準点", 45, "10分以上の実作業データ"),
            new ScoreComponent("集中時間率", (int)Math.Round(focusRatio * 30), $"実作業の {focusRatio:P0}"),
            new ScoreComponent("平均セッション", (int)Math.Round(averageFactor * 10), $"平均 {FormatSeconds(context.AverageSessionSeconds)}"),
            new ScoreComponent("最長集中", (int)Math.Round(longestFactor * 15), $"最長 {FormatSeconds(focus.LongestSeconds)}"),
            new ScoreComponent("切替ペナルティ", -(int)Math.Round(switchPenalty), $"{context.SwitchesPerHour:0.0}回/時"),
            new ScoreComponent("短時間ペナルティ", -(int)Math.Round(shortPenalty), $"5分未満 {context.ShortSessionRate:0}%")
        };
        return new FocusQualitySummary(score, grade, detail, focus, components);
    }

    private static FocusDeepDive BuildFocusDeepDive(IReadOnlyList<AnalysisActivity> source,
        IReadOnlyList<ScoreComponent> scoreComponents)
    {
        var activities = source.Where(item => item.End > item.Start).OrderBy(item => item.Start).ToList();
        if (activities.Count == 0) return FocusDeepDive.Empty with { ScoreComponents = scoreComponents };

        var interruptionStats = new Dictionary<string, (int Count, long RecoverySeconds)>(StringComparer.OrdinalIgnoreCase);
        var interruptionCount = 0;
        var returnCount = 0;
        long totalRecoverySeconds = 0;
        for (var index = 0; index + 1 < activities.Count; index++)
        {
            var origin = activities[index];
            var interrupter = activities[index + 1];
            if (origin.Seconds < TimeSpan.FromMinutes(5).TotalSeconds ||
                origin.Segment != interrupter.Segment || interrupter.Start - origin.End >= FocusGap ||
                string.Equals(origin.Identity, interrupter.Identity, StringComparison.OrdinalIgnoreCase)) continue;

            interruptionCount++;
            AnalysisActivity? returned = null;
            for (var candidateIndex = index + 2; candidateIndex < activities.Count; candidateIndex++)
            {
                var candidate = activities[candidateIndex];
                if (candidate.Segment != origin.Segment || candidate.Start - interrupter.Start > TimeSpan.FromMinutes(30)) break;
                if (string.Equals(candidate.Identity, origin.Identity, StringComparison.OrdinalIgnoreCase))
                {
                    returned = candidate;
                    break;
                }
            }
            if (returned is null) continue;
            var recovery = Math.Max(0, (long)(returned.Start - interrupter.Start).TotalSeconds);
            returnCount++;
            totalRecoverySeconds += recovery;
            var current = interruptionStats.GetValueOrDefault(interrupter.Label);
            interruptionStats[interrupter.Label] = (current.Count + 1, current.RecoverySeconds + recovery);
        }

        var interrupters = interruptionStats.Select(item => new InterruptionDetail(item.Key,
                item.Value.Count, item.Value.RecoverySeconds,
                item.Value.Count == 0 ? 0 : item.Value.RecoverySeconds / item.Value.Count))
            .OrderByDescending(item => item.Count).ThenByDescending(item => item.TotalRecoverySeconds)
            .Take(8).ToList();

        var blocks = BuildFocusBlocks(activities);
        var activeByHour = new long[24];
        var focusByHour = new long[24];
        foreach (var activity in activities) AddByLocalHour(activeByHour, activity.Start, activity.End);
        foreach (var block in blocks)
        foreach (var activity in activities.Where(item => item.End > block.Start && item.Start < block.End))
            AddByLocalHour(focusByHour, Max(activity.Start, block.Start), Min(activity.End, block.End));
        var focusHours = Enumerable.Range(0, 24)
            .Where(hour => activeByHour[hour] >= TimeSpan.FromMinutes(10).TotalSeconds)
            .Select(hour => new HourlyFocusRate(hour, activeByHour[hour], focusByHour[hour],
                Math.Clamp(focusByHour[hour] * 100d / activeByHour[hour], 0, 100)))
            .OrderByDescending(item => item.Rate).ThenByDescending(item => item.ActiveSeconds).Take(6).ToList();

        var distribution = new[]
        {
            BuildDurationBucket("25〜44分", blocks.Where(item => item.Seconds < 45 * 60)),
            BuildDurationBucket("45〜89分", blocks.Where(item => item.Seconds >= 45 * 60 && item.Seconds < 90 * 60)),
            BuildDurationBucket("90分以上", blocks.Where(item => item.Seconds >= 90 * 60))
        };
        return new FocusDeepDive(returnCount == 0 ? 0 : totalRecoverySeconds / returnCount,
            returnCount, interruptionCount, interrupters, focusHours, distribution, scoreComponents);
    }

    private static IReadOnlyList<FocusBlockDetail> BuildFocusBlocks(IReadOnlyList<AnalysisActivity> activities)
    {
        var result = new List<FocusBlockDetail>();
        DateTimeOffset? start = null;
        DateTimeOffset? end = null;
        long seconds = 0;
        var segment = -1;

        void Finish()
        {
            if (start is not null && end is not null && seconds >= MinimumFocusBlock.TotalSeconds)
                result.Add(new FocusBlockDetail(start.Value, end.Value, seconds));
            start = null;
            end = null;
            seconds = 0;
        }

        foreach (var activity in activities)
        {
            if (start is null || activity.Segment != segment || (end is not null && activity.Start - end.Value >= FocusGap))
            {
                Finish();
                start = activity.Start;
                segment = activity.Segment;
            }
            seconds += activity.Seconds;
            if (end is null || activity.End > end) end = activity.End;
        }
        Finish();
        return result;
    }

    private static void AddByLocalHour(long[] destination, DateTimeOffset start, DateTimeOffset end)
    {
        var cursor = start;
        while (cursor < end)
        {
            var local = cursor.ToLocalTime();
            var nextLocalHour = new DateTimeOffset(local.Year, local.Month, local.Day, local.Hour, 0, 0, local.Offset).AddHours(1);
            var pieceEnd = end < nextLocalHour ? end : nextLocalHour;
            destination[local.Hour] += Math.Max(0, (long)(pieceEnd - cursor).TotalSeconds);
            cursor = pieceEnd;
        }
    }

    private static FocusDurationBucket BuildDurationBucket(string label, IEnumerable<FocusBlockDetail> blocks)
    {
        var selected = blocks.ToList();
        return new FocusDurationBucket(label, selected.Count, selected.Sum(item => item.Seconds));
    }

    private static string FormatSeconds(long seconds) => seconds >= 3600
        ? $"{seconds / 3600}時間{seconds % 3600 / 60}分"
        : $"{Math.Max(0, seconds) / 60}分";

    private PeriodComparison BuildComparison(IReadOnlyList<AnalysisActivity> current,
        IReadOnlyList<AnalysisActivity> previous)
    {
        var currentTotal = current.Sum(item => item.Seconds);
        var previousTotal = previous.Sum(item => item.Seconds);
        var changes = new List<ComparisonDelta>();
        AddChanges(changes, "アプリ", current, previous, item => item.AppName);
        AddChanges(changes, "サイト", current.Where(item => item.IsWebsite), previous.Where(item => item.IsWebsite), item => item.Domain ?? item.Label);
        AddChanges(changes, "カテゴリ", current, previous, item => item.Category);
        return new PeriodComparison(currentTotal, previousTotal,
            changes.Where(item => Math.Abs(item.DeltaSeconds) >= 60)
                .OrderByDescending(item => Math.Abs(item.DeltaSeconds)).Take(12).ToList());
    }

    private static void AddChanges(List<ComparisonDelta> destination, string scope,
        IEnumerable<AnalysisActivity> current, IEnumerable<AnalysisActivity> previous,
        Func<AnalysisActivity, string> selector)
    {
        var currentTotals = current.GroupBy(selector, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Seconds), StringComparer.OrdinalIgnoreCase);
        var previousTotals = previous.GroupBy(selector, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Seconds), StringComparer.OrdinalIgnoreCase);
        foreach (var name in currentTotals.Keys.Union(previousTotals.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var currentSeconds = currentTotals.GetValueOrDefault(name);
            var previousSeconds = previousTotals.GetValueOrDefault(name);
            destination.Add(new ComparisonDelta(scope, name, currentSeconds, previousSeconds,
                currentSeconds - previousSeconds));
        }
    }

    private IReadOnlyList<AnalysisActivity> BuildAnalysisActivities(IEnumerable<ActivitySession> sessions,
        DateTimeOffset start, DateTimeOffset end, IReadOnlyList<CategoryRule>? categoryRules)
    {
        var result = new List<AnalysisActivity>();
        var segment = 0;
        foreach (var session in sessions.OrderBy(item => item.StartTime))
        {
            if (session.IsIdle)
            {
                segment++;
                continue;
            }
            var clippedStart = Max(session.StartTime, start);
            var clippedEnd = Min(session.EndTime, end);
            if (clippedEnd <= clippedStart) continue;
            var website = session.ActivityType == ActivityType.Website && !string.IsNullOrWhiteSpace(session.Domain);
            var identity = website ? $"web:{session.Domain}" : $"app:{session.AppName}";
            var label = website ? session.Domain! : session.AppName;
            var activity = new AnalysisActivity(clippedStart, clippedEnd, identity, label,
                session.AppName, session.Domain, Categorize(session, categoryRules), website, segment);

            if (result.Count > 0)
            {
                var previous = result[^1];
                if (string.Equals(previous.Identity, activity.Identity, StringComparison.OrdinalIgnoreCase) &&
                    previous.Segment == activity.Segment &&
                    activity.Start - previous.End <= TimeSpan.FromSeconds(30))
                {
                    result[^1] = previous with { End = Max(previous.End, activity.End) };
                    continue;
                }
            }
            result.Add(activity);
        }
        return result;
    }

    private static string Categorize(ActivitySession session, IReadOnlyList<CategoryRule>? rules) => rules is null
        ? session.Category
        : ActivityCategorizer.Categorize(session.AppName, session.Domain, session.IsIdle, session.CreatorName, rules);

    internal static FocusSummary CalculateFocus(IReadOnlyList<ActivityIntervalRow> intervals,
        DateTimeOffset rangeStart, DateTimeOffset rangeEnd)
    {
        long blockSeconds = 0;
        long totalQualified = 0;
        long longest = 0;
        var count = 0;
        DateTimeOffset? lastEnd = null;

        void FinishBlock()
        {
            if (blockSeconds >= MinimumFocusBlock.TotalSeconds)
            {
                totalQualified += blockSeconds;
                longest = Math.Max(longest, blockSeconds);
                count++;
            }
            blockSeconds = 0;
            lastEnd = null;
        }

        foreach (var interval in intervals)
        {
            var start = interval.Start < rangeStart ? rangeStart : interval.Start;
            var end = interval.End > rangeEnd ? rangeEnd : interval.End;
            if (end <= start) continue;
            if (interval.IsIdle)
            {
                FinishBlock();
                continue;
            }

            if (lastEnd is not null && start - lastEnd.Value >= FocusGap) FinishBlock();
            var effectiveStart = lastEnd is not null && start < lastEnd.Value ? lastEnd.Value : start;
            if (end > effectiveStart) blockSeconds += (long)(end - effectiveStart).TotalSeconds;
            if (lastEnd is null || end > lastEnd) lastEnd = end;
        }
        FinishBlock();
        return new FocusSummary(totalQualified, longest, count);
    }

    private static string BuildSubtitle(ActivitySession session, ActivityAnnotation? annotation = null)
    {
        var context = string.Join(" › ", new[] { annotation?.ProjectName, annotation?.TaskName }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        if (session.IsIdle) return string.IsNullOrWhiteSpace(context) ? "操作なし" : context;
        string subtitle;
        if (session.ActivityType == ActivityType.Website)
        {
            var details = new[] { session.CreatorName, session.PageTitle, session.Url }
                .Where(value => !string.IsNullOrWhiteSpace(value));
            subtitle = string.Join("  ·  ", details);
            if (string.IsNullOrWhiteSpace(subtitle)) subtitle = session.BrowserName ?? string.Empty;
        }
        else subtitle = session.WindowTitle ?? session.ProcessName;
        return string.IsNullOrWhiteSpace(context) ? subtitle : $"{context}  ·  {subtitle}";
    }

    private static IEnumerable<AnnotatedPiece> SplitAnnotated(ClippedActivity source,
        IReadOnlyList<CategoryRule>? rules, IReadOnlyList<ActivityAnnotation> annotations)
    {
        var overlapping = annotations.Where(item => item.EndTime > source.Start && item.StartTime < source.End).ToList();
        if (overlapping.Count == 0)
        {
            yield return new AnnotatedPiece(source.Session, source.Start, source.End, null,
                Categorize(source.Session, rules));
            yield break;
        }

        var boundaries = new List<DateTimeOffset> { source.Start, source.End };
        foreach (var annotation in overlapping)
        {
            boundaries.Add(Max(source.Start, annotation.StartTime));
            boundaries.Add(Min(source.End, annotation.EndTime));
        }
        var ordered = boundaries.Distinct().OrderBy(item => item).ToList();
        for (var index = 0; index + 1 < ordered.Count; index++)
        {
            var start = ordered[index];
            var end = ordered[index + 1];
            if (end <= start) continue;
            var midpoint = start + TimeSpan.FromTicks((end - start).Ticks / 2);
            var annotation = overlapping.Where(item => item.StartTime <= midpoint && item.EndTime > midpoint)
                .OrderByDescending(item => item.UpdatedAt).ThenByDescending(item => item.Id).FirstOrDefault();
            var category = !string.IsNullOrWhiteSpace(annotation?.CategoryOverride)
                ? annotation.CategoryOverride
                : Categorize(source.Session, rules);
            yield return new AnnotatedPiece(source.Session, start, end, annotation, category);
        }
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static DateTime StartOfWeek(DateTime date)
    {
        var diff = (7 + ((int)date.DayOfWeek - (int)DayOfWeek.Monday)) % 7;
        return date.Date.AddDays(-diff);
    }

    private sealed record ClippedActivity(ActivitySession Session, DateTimeOffset Start, DateTimeOffset End)
    {
        public long Seconds => Math.Max(0, (long)(End - Start).TotalSeconds);
    }
    private sealed record AnnotatedPiece(ActivitySession Session, DateTimeOffset Start, DateTimeOffset End,
        ActivityAnnotation? Annotation, string Category)
    {
        public long Seconds => Math.Max(0, (long)(End - Start).TotalSeconds);
    }
}

public sealed record UsageTotal(string Name, long Seconds, string? Identity = null);
public sealed record DailyTotal(DateTime Date, long Seconds, long IdleSeconds);
public sealed record TimelineEntry(DateTime Start, DateTime End, string Title, string Subtitle, long Seconds, bool IsIdle, bool IsWebsite);
public sealed record HourlyTotal(DateTime HourStart, long Seconds);
public sealed record FocusSummary(long TotalSeconds, long LongestSeconds, int BlockCount);
public sealed record AnalysisActivity(DateTimeOffset Start, DateTimeOffset End, string Identity,
    string Label, string AppName, string? Domain, string Category, bool IsWebsite, int Segment = 0)
{
    public long Seconds => Math.Max(0, (long)(End - Start).TotalSeconds);
}
public sealed record ContextSwitchSummary(int SwitchCount, double SwitchesPerHour,
    int SessionCount, long AverageSessionSeconds, int ShortSessionCount,
    IReadOnlyList<UsageTotal> TopDestinations, double ShortSessionRate = 0, long ActiveSeconds = 0);
public sealed record FocusQualitySummary(int Score, string Grade, string Detail, FocusSummary Focus,
    IReadOnlyList<ScoreComponent>? Components = null);
public sealed record ScoreComponent(string Name, int Points, string Detail);
public sealed record InterruptionDetail(string Name, int Count, long TotalRecoverySeconds, long AverageRecoverySeconds);
public sealed record HourlyFocusRate(int Hour, long ActiveSeconds, long FocusSeconds, double Rate);
public sealed record FocusDurationBucket(string Label, int Count, long TotalSeconds);
public sealed record FocusBlockDetail(DateTimeOffset Start, DateTimeOffset End, long Seconds);
public sealed record FocusDeepDive(long AverageRecoverySeconds, int ReturnCount, int InterruptionCount,
    IReadOnlyList<InterruptionDetail> Interrupters, IReadOnlyList<HourlyFocusRate> FocusHours,
    IReadOnlyList<FocusDurationBucket> DurationDistribution, IReadOnlyList<ScoreComponent> ScoreComponents)
{
    public static FocusDeepDive Empty { get; } = new(0, 0, 0, Array.Empty<InterruptionDetail>(),
        Array.Empty<HourlyFocusRate>(), Array.Empty<FocusDurationBucket>(), Array.Empty<ScoreComponent>());
}
public sealed record ComparisonDelta(string Scope, string Name, long CurrentSeconds,
    long PreviousSeconds, long DeltaSeconds);
public sealed record PeriodComparison(long CurrentSeconds, long PreviousSeconds,
    IReadOnlyList<ComparisonDelta> Changes);
public sealed record BehaviorAnalysis(ContextSwitchSummary Context, FocusQualitySummary FocusQuality,
    PeriodComparison Comparison, FocusDeepDive DeepDive);
public sealed record ReportAnalysis(ActivityReport Report, IReadOnlyList<HourlyTotal> Hourly, FocusSummary Focus);
public sealed record DashboardInsights(
    IReadOnlyList<HourlyTotal> Hourly,
    IReadOnlyList<DailyTotal> Daily,
    FocusSummary Focus,
    long CurrentWeekSeconds,
    long PreviousWeekSeconds,
    DateTimeOffset CurrentWeekStart,
    DateTimeOffset CurrentWeekEnd,
    DateTimeOffset PreviousWeekStart,
    DateTimeOffset PreviousWeekEnd);
public sealed record ActivityReport(
    DateTimeOffset Start,
    DateTimeOffset End,
    long TotalPcSeconds,
    long IdleSeconds,
    IReadOnlyList<UsageTotal> Apps,
    IReadOnlyList<UsageTotal> Sites,
    IReadOnlyList<UsageTotal> Creators,
    IReadOnlyList<UsageTotal> Categories,
    IReadOnlyList<DailyTotal> Daily,
    IReadOnlyList<TimelineEntry> Timeline);
