using PCActivityTracker.Data;
using PCActivityTracker.Domain;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PCActivityTracker.Services;

public sealed class ActivityExplorerService
{
    private static readonly TimeSpan FocusGap = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MinimumFocus = TimeSpan.FromMinutes(25);
    private const int MaximumEntries = 2000;
    private readonly ActivityRepository _activities;
    private readonly ActivityAnnotationRepository _annotations;
    private readonly SettingsService _settings;

    public ActivityExplorerService(ActivityRepository activities, ActivityAnnotationRepository annotations,
        SettingsService settings)
    {
        _activities = activities;
        _annotations = annotations;
        _settings = settings;
    }

    public ActivitySearchResult Search(ActivitySearchQuery query)
    {
        if (query.End <= query.Start) throw new ArgumentOutOfRangeException(nameof(query));
        var zoom = NormalizeZoom(query.ZoomMinutes);
        var sessions = _activities.GetRange(query.Start, query.End);
        var annotations = _annotations.GetRange(query.Start, query.End);
        var segments = BuildSegments(sessions, annotations, query.Start, query.End);
        MarkFocusAndSwitches(segments);

        var filtered = segments.Where(segment => Matches(query, segment)).ToList();
        var entries = Bucketize(filtered, query.Start, query.End, zoom);
        var truncated = entries.Count > MaximumEntries;
        if (truncated) entries = entries.Skip(entries.Count - MaximumEntries).ToList();
        return new ActivitySearchResult(entries, filtered.Sum(item => item.Seconds),
            filtered.Select(item => item.Session.Id).Distinct().Count(), truncated);
    }

    public CategoryRulePreview PreviewRule(CategoryRule candidate, DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start) return new CategoryRulePreview(0, 0, 0, Array.Empty<string>());
        var existing = _settings.CategoryRules.Where(rule => rule.Id != candidate.Id).ToList();
        var matches = new List<ActivitySession>();
        var conflicts = 0;
        long seconds = 0;
        foreach (var session in _activities.GetRange(start, end).Where(item => !item.IsIdle))
        {
            if (!ActivityCategorizer.MatchesRule(candidate, session)) continue;
            matches.Add(session);
            var clippedStart = session.StartTime < start ? start : session.StartTime;
            var clippedEnd = session.EndTime > end ? end : session.EndTime;
            seconds += Math.Max(0, (long)(clippedEnd - clippedStart).TotalSeconds);
            if (existing.Any(rule => ActivityCategorizer.MatchesRule(rule, session))) conflicts++;
        }

        var examples = matches.Select(session => candidate.Target switch
            {
                CategoryRuleTarget.Domain => session.Domain,
                CategoryRuleTarget.Creator => session.CreatorName,
                CategoryRuleTarget.ProcessName => session.ProcessName,
                CategoryRuleTarget.CreatorId => session.CreatorId,
                _ => session.AppName
            })
            .Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
        return new CategoryRulePreview(matches.Count, seconds, conflicts, examples);
    }

    public IReadOnlyList<UnclassifiedEntry> GetUnclassified(DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start) throw new ArgumentOutOfRangeException(nameof(end));
        var annotations = _annotations.GetRange(start, end);
        IEnumerable<(ActivitySession Session, long Seconds, int Count)> source;
        var rules = _settings.CategoryRules;
        if (annotations.Any(item => !string.IsNullOrWhiteSpace(item.CategoryOverride)))
        {
            source = BuildSegments(_activities.GetRange(start, end), annotations, start, end, rules)
                .Where(item => !item.Session.IsIdle && string.IsNullOrWhiteSpace(item.Annotation?.CategoryOverride))
                .GroupBy(item => item.Session.Id)
                .Select(group => (group.First().Session, group.Sum(item => item.Seconds), 1));
        }
        else
        {
            source = _activities.GetClassificationUsage(start, end).Select(row =>
                (new ActivitySession { AppName = row.AppName, ProcessName = row.ProcessName, Domain = row.Domain,
                    CreatorName = row.CreatorName, CreatorId = row.CreatorId }, row.Seconds, row.SessionCount));
        }
        return source.Where(item =>
            {
                var result = ActivityCategorizer.Evaluate(item.Session, rules);
                // An explicit user rule assigning Other is an intentional choice.
                return result.Category == "Other" && result.MatchedRule is null && item.Seconds > 0;
            }).Select(item => new { Draft = CategoryRuleDraft.FromSession(item.Session, "Other"),
                Name = item.Session.Domain ?? item.Session.AppName, item.Seconds, item.Count })
            .GroupBy(item => (item.Draft.Target, item.Draft.Pattern))
            .Select(group => new UnclassifiedEntry(group.Key.Target, group.Key.Pattern, group.First().Name,
                group.Sum(item => item.Seconds), group.Sum(item => item.Count)))
            .OrderByDescending(item => item.Seconds).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public CategoryRuleReview PreviewCategoryRules(IReadOnlyList<CategoryRuleDraft> drafts,
        DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start || drafts.Count is < 1 or > 100) throw new ArgumentException("期間と選択対象（1～100件）を確認してください。");
        var desired = Array.AsReadOnly(drafts.Select(draft => draft with
            { Pattern = draft.Pattern.Trim(), Category = SettingsService.NormalizeCategoryPath(draft.Category),
                Priority = Math.Clamp(draft.Priority, 0, 1000) }).ToArray());
        foreach (var draft in desired)
            if (!Enum.IsDefined(draft.Target) || string.IsNullOrWhiteSpace(draft.Pattern) || draft.Pattern.Length > 200 ||
                string.IsNullOrWhiteSpace(draft.Category) || draft.Category.Length > 120 || draft.Pattern.Any(char.IsControl))
                throw new ArgumentException("分類パターンは200文字、カテゴリは120文字以内で指定してください。");
        if (desired.Select((draft, index) => desired.Take(index).Any(prior => CategoryRuleDraft.SameKey(prior.ToRule(), draft.ToRule()))).Any(duplicate => duplicate))
            throw new ArgumentException("同じ分類対象が重複しています。");
        var sessions = _activities.GetRange(start, end);
        var annotations = _annotations.GetRange(start, end);
        var existing = _settings.CategoryRules;
        var candidates = desired.Select(draft =>
        {
            var rule = draft.ToRule();
            rule.Id = existing.FirstOrDefault(old => CategoryRuleDraft.SameKey(old, rule))?.Id ?? long.MaxValue;
            return rule;
        }).ToArray();
        var simulated = existing.Where(old => !candidates.Any(candidate => CategoryRuleDraft.SameKey(candidate, old)))
            .Concat(candidates).ToArray();
        var matching = sessions.Where(session => !session.IsIdle && candidates.Any(rule => ActivityCategorizer.MatchesRule(rule, session))).ToArray();
        var conflicts = matching.Count(session => existing.Any(rule =>
            !candidates.Any(candidate => CategoryRuleDraft.SameKey(candidate, rule)) &&
            ActivityCategorizer.MatchesRule(rule, session)));
        long seconds = 0, changed = 0, protectedSeconds = 0;
        foreach (var segment in BuildSegments(sessions, annotations, start, end, existing))
        {
            if (segment.Session.IsIdle || !candidates.Any(rule => ActivityCategorizer.MatchesRule(rule, segment.Session))) continue;
            seconds += segment.Seconds;
            if (!string.IsNullOrWhiteSpace(segment.Annotation?.CategoryOverride)) protectedSeconds += segment.Seconds;
            else if (!string.Equals(segment.Category, ActivityCategorizer.Evaluate(segment.Session, simulated).Category,
                         StringComparison.OrdinalIgnoreCase)) changed += segment.Seconds;
        }
        var evidence = JsonSerializer.Serialize(new
        {
            start, end, desired, existing,
            sessions = sessions.Select(item => new { item.Id, item.StartTime, item.EndTime, item.AppName,
                item.ProcessName, item.Domain, item.CreatorName, item.CreatorId, item.IsIdle }),
            annotations = annotations.Select(item => new { item.Id, item.StartTime, item.EndTime, item.UpdatedAt, item.CategoryOverride })
        });
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidence)));
        return new(start, end, desired, matching.Length, seconds, changed, protectedSeconds, conflicts, fingerprint);
    }

    public void ApplyCategoryRules(CategoryRuleReview review)
    {
        var current = PreviewCategoryRules(review.Drafts, review.Start, review.End);
        if (current.Fingerprint != review.Fingerprint)
            throw new InvalidOperationException("履歴または分類ルールが変更されました。再度プレビューしてください。");
        _settings.AddCategoryRules(current.Drafts);
    }

    private IReadOnlyList<SearchSegment> BuildSegments(IReadOnlyList<ActivitySession> sessions,
        IReadOnlyList<ActivityAnnotation> annotations, DateTimeOffset rangeStart, DateTimeOffset rangeEnd,
        IReadOnlyList<CategoryRule>? rulesOverride = null)
    {
        var rules = rulesOverride ?? _settings.CategoryRules;
        var result = new List<SearchSegment>();
        foreach (var session in sessions)
        {
            var start = Max(session.StartTime, rangeStart);
            var end = Min(session.EndTime, rangeEnd);
            if (end <= start) continue;
            var overlaps = annotations.Where(annotation => annotation.EndTime > start && annotation.StartTime < end).ToList();
            var boundaries = new List<DateTimeOffset> { start, end };
            foreach (var annotation in overlaps)
            {
                boundaries.Add(Max(start, annotation.StartTime));
                boundaries.Add(Min(end, annotation.EndTime));
            }

            var ordered = boundaries.Distinct().OrderBy(item => item).ToList();
            for (var index = 0; index + 1 < ordered.Count; index++)
            {
                var pieceStart = ordered[index];
                var pieceEnd = ordered[index + 1];
                if (pieceEnd <= pieceStart) continue;
                var midpoint = pieceStart + TimeSpan.FromTicks((pieceEnd - pieceStart).Ticks / 2);
                var annotation = overlaps.Where(item => item.StartTime <= midpoint && item.EndTime > midpoint)
                    .OrderByDescending(item => item.UpdatedAt).ThenByDescending(item => item.Id).FirstOrDefault();
                var categorization = ActivityCategorizer.Evaluate(session, rules);
                var category = string.IsNullOrWhiteSpace(annotation?.CategoryOverride)
                    ? categorization.Category
                    : annotation.CategoryOverride;
                var reason = string.IsNullOrWhiteSpace(annotation?.CategoryOverride)
                    ? categorization.Reason
                    : $"手動補正「{annotation.CategoryOverride}」";
                result.Add(new SearchSegment(session, pieceStart, pieceEnd, category, reason, annotation));
            }
        }
        return result.OrderBy(item => item.Start).ToList();
    }

    private static void MarkFocusAndSwitches(IReadOnlyList<SearchSegment> source)
    {
        var active = source.Where(item => !item.Session.IsIdle && item.End > item.Start)
            .OrderBy(item => item.Start).ToList();
        var block = new List<SearchSegment>();
        DateTimeOffset? lastEnd = null;

        void FinishBlock()
        {
            if (block.Sum(item => item.Seconds) >= MinimumFocus.TotalSeconds)
                foreach (var item in block) item.IsFocus = true;
            block.Clear();
            lastEnd = null;
        }

        for (var index = 0; index < active.Count; index++)
        {
            var current = active[index];
            if (lastEnd is not null && current.Start - lastEnd.Value >= FocusGap) FinishBlock();
            block.Add(current);
            if (lastEnd is null || current.End > lastEnd) lastEnd = current.End;

            if (index == 0) continue;
            var previous = active[index - 1];
            if (current.Start - previous.End < FocusGap &&
                !string.Equals(current.Identity, previous.Identity, StringComparison.OrdinalIgnoreCase))
            {
                current.IsSwitch = true;
                previous.IsSwitch = true;
            }
        }
        FinishBlock();
    }

    private static bool Matches(ActivitySearchQuery query, SearchSegment item)
    {
        if (!query.IncludeIdle && item.Session.IsIdle) return false;
        if (query.FocusOnly && !item.IsFocus) return false;
        if (query.SwitchesOnly && !item.IsSwitch) return false;
        if (!Contains(item.Session.AppName, query.App)) return false;
        if (!Contains(item.Session.Domain, query.Domain)) return false;
        if (!Contains(item.Session.CreatorName, query.Creator) && !Contains(item.Session.CreatorId, query.Creator)) return false;
        if (!Contains(item.Category, query.Category)) return false;
        if (!Contains(item.Annotation?.ProjectName, query.Project)) return false;
        if (string.IsNullOrWhiteSpace(query.Text)) return true;
        var values = new[]
        {
            item.Session.AppName, item.Session.ProcessName, item.Session.WindowTitle, item.Session.Domain,
            item.Session.PageTitle, item.Session.Url, item.Session.CreatorName, item.Session.CreatorId, item.Category,
            item.Annotation?.ProjectName, item.Annotation?.TaskName, item.Annotation?.Note
        };
        return values.Any(value => Contains(value, query.Text));
    }

    private static bool Contains(string? value, string? filter) => string.IsNullOrWhiteSpace(filter) ||
        (!string.IsNullOrWhiteSpace(value) && value.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase));

    private static List<ExplorerEntry> Bucketize(IReadOnlyList<SearchSegment> segments,
        DateTimeOffset rangeStart, DateTimeOffset rangeEnd, int zoomMinutes)
    {
        var intervalMilliseconds = zoomMinutes * 60_000L;
        var buckets = new SortedDictionary<long, BucketAccumulator>();
        foreach (var segment in segments)
        {
            var cursor = segment.Start;
            while (cursor < segment.End)
            {
                var bucketMilliseconds = Math.DivRem(cursor.ToUnixTimeMilliseconds(), intervalMilliseconds, out _) * intervalMilliseconds;
                var bucketStart = DateTimeOffset.FromUnixTimeMilliseconds(bucketMilliseconds);
                var bucketEnd = bucketStart.AddMinutes(zoomMinutes);
                var pieceEnd = Min(segment.End, bucketEnd);
                var seconds = Math.Max(0, (long)(pieceEnd - cursor).TotalSeconds);
                if (seconds == 0) { cursor = pieceEnd; continue; }
                if (!buckets.TryGetValue(bucketMilliseconds, out var accumulator))
                {
                    accumulator = new BucketAccumulator(Max(bucketStart, rangeStart), Min(bucketEnd, rangeEnd));
                    buckets.Add(bucketMilliseconds, accumulator);
                }
                accumulator.Add(segment, seconds);
                cursor = pieceEnd;
            }
        }

        return buckets.Values.Where(bucket => bucket.End > bucket.Start).Select(bucket => bucket.Build()).ToList();
    }

    private static int NormalizeZoom(int zoom) => zoom is 1 or 5 or 10 or 15 or 30 or 60 ? zoom : 5;
    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;
    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;

    private sealed class SearchSegment
    {
        public SearchSegment(ActivitySession session, DateTimeOffset start, DateTimeOffset end,
            string category, string categoryReason, ActivityAnnotation? annotation)
        {
            Session = session;
            Start = start;
            End = end;
            Category = category;
            CategoryReason = categoryReason;
            Annotation = annotation;
        }

        public ActivitySession Session { get; }
        public DateTimeOffset Start { get; }
        public DateTimeOffset End { get; }
        public string Category { get; }
        public string CategoryReason { get; }
        public ActivityAnnotation? Annotation { get; }
        public bool IsFocus { get; set; }
        public bool IsSwitch { get; set; }
        public long Seconds => Math.Max(0, (long)(End - Start).TotalSeconds);
        public string Identity => Session.ActivityType == ActivityType.Website && !string.IsNullOrWhiteSpace(Session.Domain)
            ? "web:" + Session.Domain : "app:" + Session.AppName;
    }

    private sealed class BucketAccumulator
    {
        private readonly Dictionary<string, Contribution> _contributions = new(StringComparer.OrdinalIgnoreCase);
        private bool _containsFocus;
        private bool _containsSwitch;
        public BucketAccumulator(DateTimeOffset start, DateTimeOffset end) { Start = start; End = end; }
        public DateTimeOffset Start { get; }
        public DateTimeOffset End { get; }

        public void Add(SearchSegment segment, long seconds)
        {
            _containsFocus |= segment.IsFocus;
            _containsSwitch |= segment.IsSwitch;
            if (!_contributions.TryGetValue(segment.Identity, out var contribution))
            {
                contribution = new Contribution(segment);
                _contributions.Add(segment.Identity, contribution);
            }
            contribution.Seconds += seconds;
            contribution.SegmentCount++;
            contribution.IsFocus |= segment.IsFocus;
            contribution.IsSwitch |= segment.IsSwitch;
        }

        public ExplorerEntry Build()
        {
            var dominant = _contributions.Values.OrderByDescending(item => item.Seconds).First();
            var segment = dominant.Segment;
            var session = segment.Session;
            var title = session.ActivityType == ActivityType.Website ? session.Domain ?? session.AppName : session.AppName;
            var sourceSubtitle = session.ActivityType == ActivityType.Website
                ? FirstNotEmpty(session.CreatorName, session.PageTitle, session.Url, session.BrowserName)
                : FirstNotEmpty(session.WindowTitle, session.ProcessName);
            var context = string.Join(" › ", new[] { segment.Annotation?.ProjectName, segment.Annotation?.TaskName }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            var subtitle = string.IsNullOrWhiteSpace(context)
                ? sourceSubtitle
                : string.IsNullOrWhiteSpace(sourceSubtitle) ? context : $"{context}  ·  {sourceSubtitle}";
            var totalSeconds = _contributions.Values.Sum(item => item.Seconds);
            return new ExplorerEntry(Start, End, title, subtitle, totalSeconds, session.IsIdle,
                session.ActivityType == ActivityType.Website, session.AppName, session.Domain ?? string.Empty,
                session.CreatorName ?? string.Empty, segment.Category, segment.CategoryReason,
                segment.Annotation?.ProjectName ?? string.Empty, segment.Annotation?.TaskName ?? string.Empty,
                segment.Annotation?.Note ?? string.Empty, segment.Annotation?.Id ?? 0,
                segment.Annotation?.StartTime, segment.Annotation?.EndTime,
                _containsFocus, _containsSwitch, _contributions.Count, session.ProcessName, session.CreatorId ?? string.Empty);
        }

        private static string FirstNotEmpty(params string?[] values) =>
            values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private sealed class Contribution
    {
        public Contribution(SearchSegment segment) => Segment = segment;
        public SearchSegment Segment { get; }
        public long Seconds { get; set; }
        public int SegmentCount { get; set; }
        public bool IsFocus { get; set; }
        public bool IsSwitch { get; set; }
    }
}

public sealed record ActivitySearchQuery(DateTimeOffset Start, DateTimeOffset End, int ZoomMinutes = 5,
    string Text = "", string App = "", string Domain = "", string Creator = "", string Category = "",
    string Project = "", bool IncludeIdle = true, bool FocusOnly = false, bool SwitchesOnly = false);

public sealed record ActivitySearchResult(IReadOnlyList<ExplorerEntry> Entries, long MatchingSeconds,
    int RawSessionCount, bool IsTruncated);

public sealed record ExplorerEntry(DateTimeOffset Start, DateTimeOffset End, string Title, string Subtitle,
    long Seconds, bool IsIdle, bool IsWebsite, string AppName, string Domain, string Creator, string Category,
    string CategoryReason, string Project, string Task, string Note, long AnnotationId,
    DateTimeOffset? AnnotationStart, DateTimeOffset? AnnotationEnd,
    bool IsFocus, bool IsSwitch, int ActivityCount, string ProcessName = "", string CreatorId = "");

public sealed record CategoryRulePreview(int SessionCount, long Seconds, int ConflictCount,
    IReadOnlyList<string> Examples);
