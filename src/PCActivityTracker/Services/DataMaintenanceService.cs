using PCActivityTracker.Data;

namespace PCActivityTracker.Services;

/// <summary>
/// Enforces retention while the application remains resident for weeks. Database
/// access is serialized by SqliteDatabase, so maintenance cannot race a tracker write.
/// </summary>
public sealed class DataMaintenanceService : IAsyncDisposable
{
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromHours(12);
    private readonly ActivityRepository _activities;
    private readonly SettingsService _settings;
    private readonly BackupService _backups;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _loop;

    public DataMaintenanceService(ActivityRepository activities, SettingsService settings, BackupService backups)
    {
        _activities = activities;
        _settings = settings;
        _backups = backups;
    }

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cancellation.Token));

    internal void RunOnce(DateTimeOffset now)
    {
        var cutoff = now.AddDays(-_settings.Current.RetentionDays);
        _activities.DeleteBefore(cutoff);
        _backups.ApplyRetention(cutoff);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(MaintenanceInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try { RunOnce(DateTimeOffset.Now); }
            catch (Exception exception) { DiagnosticLogService.Error("DataMaintenance.Retention", exception); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _cancellation.Dispose();
    }
}
