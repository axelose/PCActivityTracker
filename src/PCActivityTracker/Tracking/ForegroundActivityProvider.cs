using PCActivityTracker.Domain;

namespace PCActivityTracker.Tracking;

public sealed class ForegroundActivityProvider
{
    private static readonly Dictionary<string, string> FriendlyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["explorer"] = "Windows Explorer",
        ["code"] = "Visual Studio Code",
        ["devenv"] = "Visual Studio",
        ["chrome"] = "Google Chrome",
        ["msedge"] = "Microsoft Edge",
        ["brave"] = "Brave",
        ["powershell"] = "PowerShell",
        ["WindowsTerminal"] = "Windows Terminal",
        ["ApplicationFrameHost"] = "Windows App"
    };

    private readonly IdleDetector _idleDetector;
    private readonly BrowserStateStore _browserState;
    private readonly Dictionary<uint, (string ProcessName, string ExecutableName, DateTimeOffset Expires)> _processCache = new();
    private DateTimeOffset _nextProcessCacheCleanup = DateTimeOffset.MinValue;

    public ForegroundActivityProvider(IdleDetector idleDetector, BrowserStateStore browserState)
    {
        _idleDetector = idleDetector;
        _browserState = browserState;
    }

    public ForegroundSnapshot Capture(int idleThresholdMinutes)
    {
        var now = DateTimeOffset.Now;
        if (_idleDetector.GetIdleTime() >= TimeSpan.FromMinutes(idleThresholdMinutes))
            return new ForegroundSnapshot(now, 0, "Idle", string.Empty, string.Empty, string.Empty, true, null);

        var window = NativeMethods.GetForegroundWindow();
        if (window == IntPtr.Zero)
            return new ForegroundSnapshot(now, 0, "Unknown", string.Empty, string.Empty, string.Empty, false, null);

        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        var process = FindProcess(processId, now);
        var appName = FriendlyNames.GetValueOrDefault(process.ProcessName, process.ProcessName.Length == 0 ? "Unknown" : process.ProcessName);
        var windowTitle = GetWindowTitle(window);
        var browser = _browserState.GetFresh(process.ProcessName, windowTitle, now);
        return new ForegroundSnapshot(now, processId, appName, process.ProcessName, windowTitle, process.ExecutableName, false, browser);
    }

    private (string ProcessName, string ExecutableName) FindProcess(uint processId, DateTimeOffset now)
    {
        PruneProcessCache(now);
        if (_processCache.TryGetValue(processId, out var cached) && cached.Expires > now)
            return (cached.ProcessName, cached.ExecutableName);

        var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.Th32csSnapProcess, 0);
        if (snapshot == NativeMethods.InvalidHandleValue) return (string.Empty, string.Empty);
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ProcessEntry32>(), ExecutableFile = string.Empty };
            if (NativeMethods.Process32FirstW(snapshot, ref entry))
            {
                do
                {
                    if (entry.ProcessId != processId) continue;
                    var executable = entry.ExecutableFile ?? string.Empty;
                    var name = System.IO.Path.GetFileNameWithoutExtension(executable);
                    _processCache[processId] = (name, executable, now.AddMinutes(10));
                    return (name, executable);
                } while (NativeMethods.Process32NextW(snapshot, ref entry));
            }
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }
        return (string.Empty, string.Empty);
    }

    private void PruneProcessCache(DateTimeOffset now)
    {
        if (now < _nextProcessCacheCleanup) return;
        foreach (var processId in _processCache.Where(item => item.Value.Expires <= now).Select(item => item.Key).ToArray())
            _processCache.Remove(processId);
        _nextProcessCacheCleanup = now.AddMinutes(10);
    }

    private static string GetWindowTitle(IntPtr window)
    {
        var length = Math.Min(NativeMethods.GetWindowTextLength(window), 2048);
        if (length <= 0) return string.Empty;
        var buffer = new char[length + 1];
        var copied = NativeMethods.GetWindowText(window, buffer, buffer.Length);
        return copied > 0 ? new string(buffer, 0, copied) : string.Empty;
    }
}
