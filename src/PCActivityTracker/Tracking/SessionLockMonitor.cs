using Microsoft.Win32;

namespace PCActivityTracker.Tracking;

/// <summary>
/// Observes documented Windows session notifications. It does not inspect or open
/// any foreground process and requires no administrator privileges.
/// </summary>
public sealed class SessionLockMonitor : IDisposable
{
    private volatile bool _isLocked;

    public SessionLockMonitor() => SystemEvents.SessionSwitch += SystemEvents_SessionSwitch;

    public bool IsLocked => _isLocked;
    public event Action<bool>? LockStateChanged;

    private void SystemEvents_SessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock) SetLockState(true);
        else if (e.Reason == SessionSwitchReason.SessionUnlock) SetLockState(false);
    }

    internal void SetLockStateForTest(bool locked) => SetLockState(locked);

    private void SetLockState(bool locked)
    {
        if (_isLocked == locked) return;
        _isLocked = locked;
        LockStateChanged?.Invoke(locked);
    }

    public void Dispose() => SystemEvents.SessionSwitch -= SystemEvents_SessionSwitch;
}
