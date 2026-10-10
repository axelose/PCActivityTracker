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
    /// Releases the dashboard visual tree after the window is closed, then asks Windows
    /// to remove unused pages from this process' working set. The recorder keeps running.
    /// </summary>
    public static void ReleaseUnusedUiMemory()
    {
        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);

            using var current = Process.GetCurrentProcess();
            _ = EmptyWorkingSet(current.Handle);
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

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr process);
}
