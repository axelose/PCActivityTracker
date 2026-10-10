using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PCActivityTracker.Services;

/// <summary>
/// Lowers only this application's scheduling/QoS. It never opens or modifies another process.
/// </summary>
public static class PerformanceModeService
{
    private const int ProcessPowerThrottling = 4;
    private const uint CurrentVersion = 1;
    private const uint ExecutionSpeed = 0x1;
    private static long _lastCollectionTick = long.MinValue;
    public static MemoryCleanupMeasurement? LastCleanup { get; private set; }

    public static void ApplyLowImpactMode()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            current.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch
        {
            // Priority reduction is a best-effort optimization.
        }

        try
        {
            var state = new ProcessPowerThrottlingState
            {
                Version = CurrentVersion,
                ControlMask = ExecutionSpeed,
                StateMask = ExecutionSpeed
            };
            _ = SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling,
                ref state, (uint)Marshal.SizeOf<ProcessPowerThrottlingState>());
        }
        catch
        {
            // Unsupported Windows builds continue with BelowNormal priority only.
        }
    }

    /// <summary>
    /// Requests at most one non-blocking, optimized collection after a sizeable UI
    /// was released. Never force finalization or evict the recorder's resident pages.
    /// </summary>
    public static void ReleaseUnusedUiMemory()
    {
        try
        {
            var managedBytes = GC.GetTotalMemory(forceFullCollection: false);
            var now = Environment.TickCount64;
            var requested = MemoryCleanupPolicy.ShouldCollect(managedBytes, now, _lastCollectionTick);
            var elapsed = Stopwatch.StartNew();
            if (requested)
            {
                _lastCollectionTick = now;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: false);
            }
            using var current = Process.GetCurrentProcess();
            LastCleanup = new MemoryCleanupMeasurement(managedBytes, current.PrivateMemorySize64,
                current.WorkingSet64, elapsed.Elapsed.TotalMilliseconds, requested);
        }
        catch
        {
            // Memory trimming is best effort and must never interrupt activity recording.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(IntPtr process, int informationClass,
        ref ProcessPowerThrottlingState information, uint informationSize);

}

public sealed record MemoryCleanupMeasurement(long ManagedBytes, long PrivateBytes,
    long WorkingSetBytes, double RequestMilliseconds, bool CollectionRequested);
