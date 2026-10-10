using PCActivityTracker.Data;
using PCActivityTracker.Domain;
using PCActivityTracker.Services;

namespace PCActivityTracker.Tracking;

public sealed class ActivityTrackerService : IAsyncDisposable
{
    private static readonly HashSet<string> BrowserProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "brave", "firefox", "opera", "opera_gx", "vivaldi", "waterfox"
    };
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaximumContinuousGap = SampleInterval + SampleInterval;
    private static readonly uint CurrentProcessId = (uint)Environment.ProcessId;
    private readonly ForegroundActivityProvider _provider;
    private readonly ActivityRepository _repository;
    private readonly SettingsService _settings;
    private readonly SessionLockMonitor _sessionLock;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _gate = new();
    private Task? _loop;
    private ActivitySession? _current;
    private DateTimeOffset _lastPersisted = DateTimeOffset.MinValue;
    private DateTimeOffset? _lastSuccessfulSample;
    private string _currentForegroundProcessName = string.Empty;
    private DateTimeOffset? _lastErrorAt;
    private string? _lastError;
    private volatile bool _suspended;

    public ActivityTrackerService(ForegroundActivityProvider provider, ActivityRepository repository, SettingsService settings,
        SessionLockMonitor sessionLock)
    {
        _provider = provider;
        _repository = repository;
        _settings = settings;
        _sessionLock = sessionLock;
        _sessionLock.LockStateChanged += SessionLock_StateChanged;
    }

    public event EventHandler? ActivityChanged;

    public string DisplayStatus
    {
        get
        {
            lock (_gate)
            {
                if (_sessionLock.IsLocked) return "Windowsロック中 / 記録待機";
                if (_lastErrorAt is not null && (_lastSuccessfulSample is null || _lastErrorAt.Value > _lastSuccessfulSample.Value))
                    return $"記録エラー {_lastErrorAt:HH:mm:ss} / {_lastError}";
                return _lastSuccessfulSample is null
                    ? "記録エンジン起動中"
                    : $"正常 / 最終取得 {_lastSuccessfulSample:HH:mm:ss} / 最終保存 {FormatTime(_lastPersisted)}";
            }
        }
    }

    public string CurrentForegroundProcessName
    {
        get { lock (_gate) return _currentForegroundProcessName; }
    }

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cancellation.Token));

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(SampleInterval);
        await SampleAsync();
        while (await timer.WaitForNextTickAsync(cancellationToken)) await SampleAsync();
    }

    private Task SampleAsync()
    {
        try
        {
            if (_suspended || _sessionLock.IsLocked) return Task.CompletedTask;
            var settings = _settings.Current;
            if (!settings.RecordingEnabled)
            {
                CloseCurrent(DateTimeOffset.Now);
                return Task.CompletedTask;
            }

            var snapshot = _provider.Capture(settings.IdleThresholdMinutes);
            ProcessSnapshot(snapshot, settings);
            lock (_gate)
            {
                _lastSuccessfulSample = snapshot.ObservedAt;
                _lastError = null;
            }
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _lastErrorAt = DateTimeOffset.Now;
                _lastError = exception.Message;
            }
            DiagnosticLogService.Error("ActivityTracker.Sample", exception);
        }
        return Task.CompletedTask;
    }

    internal void ProcessSnapshot(ForegroundSnapshot snapshot, AppSettings? settingsOverride = null)
    {
        if (_sessionLock.IsLocked)
        {
            lock (_gate) _currentForegroundProcessName = string.Empty;
            CloseCurrent(snapshot.ObservedAt);
            return;
        }

        // Reuse the normal foreground observation for game-safe AI control. This
        // does not enumerate, open, inject into, or inspect the target process.
        lock (_gate) _currentForegroundProcessName = snapshot.ProcessName;

        var settings = settingsOverride ?? _settings.Current;
        var next = CreateSession(snapshot, settings);
        var changed = false;
        lock (_gate)
        {
            // SuspendAndDiscard waits on this same gate. Re-check here so a
            // sample captured just before suspension cannot be saved after a
            // privacy deletion has started.
            if (_suspended)
            {
                _current = null;
                return;
            }
            if (next is null)
            {
                changed = CloseCurrentLocked(CappedEndTime(snapshot.ObservedAt));
            }
            else if (_current is not null && _current.IsSameActivity(next) &&
                     IsContinuous(snapshot.ObservedAt, _current.EndTime))
            {
                _current.EndTime = snapshot.ObservedAt;
                if (snapshot.ObservedAt - _lastPersisted >= HeartbeatInterval)
                {
                    _repository.Save(_current);
                    _lastPersisted = snapshot.ObservedAt;
                    changed = true;
                }
            }
            else
            {
                if (_current is not null)
                {
                    // Sampling pauses while Windows sleeps. Never charge that unobserved
                    // interval to the app that happened to be active before suspension.
                    _current.EndTime = CappedEndTime(snapshot.ObservedAt);
                    _repository.Save(_current);
                }
                _current = next;
                _repository.Save(_current);
                _lastPersisted = snapshot.ObservedAt;
                changed = true;
            }
        }

        if (changed) ActivityChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SuspendAndDiscard()
    {
        _suspended = true;
        lock (_gate) _current = null;
    }

    public void Resume() => _suspended = false;

    private ActivitySession? CreateSession(ForegroundSnapshot snapshot, AppSettings settings)
    {
        if (snapshot.ProcessId == CurrentProcessId) return null;
        if (_settings.IsApplicationExcluded(snapshot.AppName, snapshot.ProcessName, snapshot.ExecutableName)) return null;
        if (snapshot.BrowserActivity is { } browser)
        {
            if (_settings.IsDomainExcluded(browser.Domain)) return null;
            return new ActivitySession
            {
                StartTime = snapshot.ObservedAt,
                EndTime = snapshot.ObservedAt,
                ActivityType = ActivityType.Website,
                AppName = snapshot.AppName,
                ProcessName = snapshot.ProcessName,
                WindowTitle = settings.RecordWindowTitles ? snapshot.WindowTitle : null,
                ExecutableName = snapshot.ExecutableName,
                BrowserName = browser.Browser,
                Url = settings.RecordUrls ? UrlPrivacyService.Sanitize(browser.Url) : null,
                Domain = browser.Domain,
                PageTitle = settings.RecordPageTitles ? browser.PageTitle : null,
                CreatorName = settings.RecordCreatorNames ? NormalizeCreatorName(browser.CreatorName) : null,
                CreatorId = settings.RecordCreatorNames && !string.IsNullOrWhiteSpace(browser.CreatorName)
                    ? CreatorIdentityService.Normalize(browser.CreatorId, browser.Domain) : null,
                // Persist the deterministic built-in category. User rules are applied
                // at query time, so deleting a rule also restores historical results.
                Category = ActivityCategorizer.Categorize(snapshot.AppName, browser.Domain, false, browser.CreatorName)
            };
        }

        return new ActivitySession
        {
            StartTime = snapshot.ObservedAt,
            EndTime = snapshot.ObservedAt,
            ActivityType = snapshot.IsIdle ? ActivityType.Idle : ActivityType.Application,
            AppName = snapshot.IsIdle ? "Idle" : snapshot.AppName,
            ProcessName = snapshot.ProcessName,
            // A browser title can reveal the current site even when the extension is
            // unavailable or deliberately disabled in an Incognito/InPrivate window.
            // Browser page metadata is therefore accepted only through the extension,
            // whose manifest disables private-window access.
            WindowTitle = snapshot.IsIdle || !settings.RecordWindowTitles || IsBrowserProcess(snapshot.ProcessName)
                ? null
                : snapshot.WindowTitle,
            ExecutableName = snapshot.ExecutableName,
            IsIdle = snapshot.IsIdle,
            Category = ActivityCategorizer.Categorize(snapshot.AppName, null, snapshot.IsIdle)
        };
    }

    private void CloseCurrent(DateTimeOffset end)
    {
        var changed = false;
        lock (_gate)
        {
            changed = CloseCurrentLocked(CappedEndTime(end));
        }
        if (changed) ActivityChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool CloseCurrentLocked(DateTimeOffset end)
    {
        if (_current is null) return false;
        _current.EndTime = end < _current.StartTime ? _current.StartTime : end;
        _repository.Save(_current);
        _current = null;
        return true;
    }

    private DateTimeOffset CappedEndTime(DateTimeOffset observedAt)
    {
        if (_current is null) return observedAt;
        if (observedAt <= _current.EndTime) return _current.EndTime;
        return IsContinuous(observedAt, _current.EndTime)
            ? observedAt
            : _current.EndTime.Add(SampleInterval);
    }

    private static bool IsContinuous(DateTimeOffset observedAt, DateTimeOffset previousObservedAt)
    {
        var gap = observedAt - previousObservedAt;
        return gap >= TimeSpan.Zero && gap <= MaximumContinuousGap;
    }

    private static string? NormalizeCreatorName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 200 ? normalized : normalized[..200];
    }

    private static bool IsBrowserProcess(string? processName) =>
        !string.IsNullOrWhiteSpace(processName) && BrowserProcessNames.Contains(processName);

    private void SessionLock_StateChanged(bool locked)
    {
        if (locked) CloseCurrent(DateTimeOffset.Now);
    }

    private static string FormatTime(DateTimeOffset value) =>
        value == DateTimeOffset.MinValue ? "未保存" : value.ToString("HH:mm:ss");

    public async ValueTask DisposeAsync()
    {
        _sessionLock.LockStateChanged -= SessionLock_StateChanged;
        _cancellation.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        CloseCurrent(DateTimeOffset.Now);
        _cancellation.Dispose();
    }
}
