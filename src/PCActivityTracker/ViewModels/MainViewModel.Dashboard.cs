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
    private void ApplyReport(ActivityReport report, bool includeTimeline)
    {
        TodayPcTime = FormatDuration(report.TotalPcSeconds);
        IdleTime = FormatDuration(report.IdleSeconds);
        TopApp = report.Apps.FirstOrDefault()?.Name ?? "—";
        TopSite = report.Sites.FirstOrDefault()?.Name ?? "—";
        PeriodTotal = FormatDuration(report.TotalPcSeconds);
        PeriodLabel = $"Idle {FormatDuration(report.IdleSeconds)}";

        if (includeTimeline)
            Replace(Timeline, report.Timeline.Select(item => new TimelineItem(
                $"{item.Start:HH:mm} – {item.End:HH:mm}", item.Title, item.Subtitle,
                FormatDuration(item.Seconds), item.IsIdle, item.IsWebsite)));
        else
            Timeline.Clear();
        HasNoTimeline = Timeline.Count == 0;
        Replace(AppRankings, ToRanking(report.Apps));
        Replace(SiteRankings, ToRanking(report.Sites));
        HasNoSites = SiteRankings.Count == 0;
        Replace(CreatorRankings, ToRanking(report.Creators));
        HasNoCreators = CreatorRankings.Count == 0;
        Replace(CategoryRankings, ToRanking(report.Categories));

        ApplyDaily(report.Daily);
    }

    private void ApplyDaily(IReadOnlyList<DailyTotal> daily)
    {
        var maxDaily = Math.Max(1, daily.Count == 0 ? 0 : daily.Max(item => item.Seconds));
        Replace(DailyUsage, daily.Select(item => new DailyItem(
            item.Date.ToString("M/d (ddd)"), FormatDuration(item.Seconds), $"Idle {FormatDuration(item.IdleSeconds)}",
            320d * item.Seconds / maxDaily)));
    }

    private void ApplyInsights(DashboardInsights insights)
    {
        FocusTime = FormatDuration(insights.Focus.TotalSeconds);
        FocusDetail = insights.Focus.BlockCount == 0
            ? "25分以上の集中ブロックはありません"
            : $"最長 {FormatDuration(insights.Focus.LongestSeconds)} ・ {insights.Focus.BlockCount}回";

        if (insights.PreviousWeekSeconds <= 0)
            WeekComparison = insights.CurrentWeekSeconds > 0 ? "前週データなし" : "比較データなし";
        else
        {
            var percent = (insights.CurrentWeekSeconds - insights.PreviousWeekSeconds) * 100d / insights.PreviousWeekSeconds;
            WeekComparison = $"前週同期間比 {percent:+0;-0;±0}%";
        }
        WeekComparisonDetail = $"今週 {FormatDuration(insights.CurrentWeekSeconds)} / 前週 {FormatDuration(insights.PreviousWeekSeconds)}";

        var maximum = Math.Max(1, insights.Hourly.Count == 0 ? 0 : insights.Hourly.Max(item => item.Seconds));
        var byHour = insights.Hourly.ToDictionary(item => (item.HourStart.Date, item.HourStart.Hour));
        var firstDay = insights.Hourly.FirstOrDefault()?.HourStart.Date ?? DateTime.Today.AddDays(-6);
        var days = Enumerable.Range(0, 7).Select(offset =>
        {
            var date = firstDay.AddDays(offset);
            var cells = Enumerable.Range(0, 24).Select(hour =>
            {
                var seconds = byHour.GetValueOrDefault((date, hour))?.Seconds ?? 0;
                var opacity = seconds == 0 ? 0.08 : 0.18 + 0.82 * seconds / maximum;
                return new HeatmapCellItem(hour, opacity,
                    $"{date:M/d (ddd)} {hour:00}:00–{hour + 1:00}:00  {FormatDuration(seconds)}");
            }).ToList();
            return new HeatmapDayItem(date.ToString("M/d (ddd)"), cells);
        });
        Replace(HeatmapDays, days);
    }

    private void ApplyBehaviorAnalysis(BehaviorAnalysis analysis)
    {
        FocusQualityScore = analysis.FocusQuality.Score.ToString();
        FocusQualityGrade = analysis.FocusQuality.Grade;
        FocusQualityDetail = analysis.FocusQuality.Detail;
        ContextSwitchCount = $"{analysis.Context.SwitchCount:N0}回";
        SwitchesPerHour = $"{analysis.Context.SwitchesPerHour:0.0}回/時";
        AverageSessionDuration = FormatDuration(analysis.Context.AverageSessionSeconds);
        ShortSessionSummary = $"{analysis.Context.ShortSessionCount:N0}回（{analysis.Context.ShortSessionRate:0}%）";
        Replace(SwitchDestinations, analysis.Context.TopDestinations.Select(item =>
            new AnalysisMetricItem(item.Name, $"{item.Seconds:N0}回")));

        var deepDive = analysis.DeepDive;
        AverageRecoveryTime = deepDive.ReturnCount == 0 ? "—" : FormatDuration(deepDive.AverageRecoverySeconds);
        RecoverySummary = deepDive.InterruptionCount == 0
            ? "5分以上の作業後に発生した割り込みはありません"
            : $"割り込み {deepDive.InterruptionCount:N0}回中 {deepDive.ReturnCount:N0}回で30分以内に復帰";
        Replace(InterruptionSources, deepDive.Interrupters.Select(item => new AnalysisMetricItem(
            item.Name, $"{item.Count:N0}回 ・ 平均復帰 {FormatDuration(item.AverageRecoverySeconds)}")));
        Replace(FocusHours, deepDive.FocusHours.Select(item => new AnalysisMetricItem(
            $"{item.Hour:00}:00〜{(item.Hour + 1) % 24:00}:00",
            $"集中率 {item.Rate:0}% ・ 実作業 {FormatDuration(item.ActiveSeconds)}")));
        Replace(FocusDistribution, deepDive.DurationDistribution.Select(item => new AnalysisMetricItem(
            item.Label, $"{item.Count:N0}回 ・ {FormatDuration(item.TotalSeconds)}")));
        Replace(FocusScoreComponents, deepDive.ScoreComponents.Select(item => new AnalysisMetricItem(
            item.Name, $"{item.Points:+0;-0;0}点 ・ {item.Detail}")));

        var comparison = analysis.Comparison;
        if (comparison.PreviousSeconds <= 0)
            AnalysisComparisonSummary = comparison.CurrentSeconds > 0 ? "前期間の比較データがありません" : "比較データなし";
        else
        {
            var percent = (comparison.CurrentSeconds - comparison.PreviousSeconds) * 100d / comparison.PreviousSeconds;
            AnalysisComparisonSummary = $"前週比 {percent:+0;-0;±0}%　今週 {FormatDuration(comparison.CurrentSeconds)} / 前週 {FormatDuration(comparison.PreviousSeconds)}";
        }
        Replace(PeriodChanges, comparison.Changes.Select(item => new ComparisonItem(
            item.Scope, item.Name, FormatDuration(item.PreviousSeconds), FormatDuration(item.CurrentSeconds),
            $"{(item.DeltaSeconds >= 0 ? "+" : "−")}{FormatDuration(Math.Abs(item.DeltaSeconds))}", item.DeltaSeconds >= 0)));
    }

    private static IEnumerable<RankingItem> ToRanking(IReadOnlyList<UsageTotal> values)
    {
        var max = Math.Max(1, values.FirstOrDefault()?.Seconds ?? 1);
        return values.Take(10).Select((item, index) => new RankingItem(index + 1, item.Name, FormatDuration(item.Seconds), 240d * item.Seconds / max, item.Identity));
    }

}
