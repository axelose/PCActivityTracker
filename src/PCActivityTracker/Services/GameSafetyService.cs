using PCActivityTracker.Domain;
using PCActivityTracker.Tracking;

namespace PCActivityTracker.Services;

public sealed class GameSafetyService
{
    private readonly ActivityTrackerService _tracker;

    public GameSafetyService(ActivityTrackerService tracker) => _tracker = tracker;

    public GameSafetyState Evaluate(AppSettings settings)
    {
        if (!settings.GameSafeModeEnabled)
            return new GameSafetyState(false, null, "ゲームセーフモードはOFFです");

        var foreground = Normalize(_tracker.CurrentForegroundProcessName);
        var games = Parse(settings.GameProcessNames);
        var match = games.FirstOrDefault(game => string.Equals(game, foreground, StringComparison.OrdinalIgnoreCase));
        return match is null
            ? new GameSafetyState(false, null, "ゲームセーフモード監視中")
            : new GameSafetyState(true, foreground, $"ゲームを検出: {foreground}");
    }

    public async Task<string?> MonitorAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (!settings.GameSafeModeEnabled) return null;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var state = Evaluate(settings);
            if (state.IsGameForeground) return state.ProcessName;
        }
        return null;
    }

    internal static IReadOnlySet<string> Parse(string? value) =>
        (value ?? string.Empty).Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize)
            .Where(item => item.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string Normalize(string? value) =>
        System.IO.Path.GetFileNameWithoutExtension(value?.Trim() ?? string.Empty);
}

public sealed record GameSafetyState(bool IsGameForeground, string? ProcessName, string Message);
