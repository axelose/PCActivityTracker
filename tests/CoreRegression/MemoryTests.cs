using PCActivityTracker.Services;

namespace CoreRegression;
internal static class MemoryTests
{
    internal static void Run()
    {
        var threshold = MemoryCleanupPolicy.MinimumManagedBytes;
        Check.That(!MemoryCleanupPolicy.ShouldCollect(threshold - 1, 0, long.MinValue), "Small heap must not trigger collection.");
        Check.That(MemoryCleanupPolicy.ShouldCollect(threshold, 0, long.MinValue), "Large released UI can request collection.");
        Check.That(!MemoryCleanupPolicy.ShouldCollect(threshold, 119999, 0), "Rapid UI close/reopen must not repeatedly collect.");
        Check.That(MemoryCleanupPolicy.ShouldCollect(threshold, 120000, 0), "Collection cooldown boundary is wrong.");
        Check.That(!MemoryCleanupPolicy.ShouldCollect(threshold, 0, 100), "Clock regression must not bypass cooldown.");
        var before = GC.CollectionCount(GC.MaxGeneration);
        PerformanceModeService.ReleaseUnusedUiMemory();
        var measurement = PerformanceModeService.LastCleanup;
        Check.That(measurement is not null, "Memory cleanup measurement is missing.");
        Check.That(measurement!.PrivateBytes > 0 && measurement.WorkingSetBytes > 0, "Memory counters unavailable.");
        Check.That(GC.CollectionCount(GC.MaxGeneration) - before <= 1, "Cleanup requested multiple full collections.");
        Console.WriteLine($"MEMORY managed={measurement.ManagedBytes} private={measurement.PrivateBytes} workingSet={measurement.WorkingSetBytes} requestMs={measurement.RequestMilliseconds:F3} requested={measurement.CollectionRequested}");
    }
}
