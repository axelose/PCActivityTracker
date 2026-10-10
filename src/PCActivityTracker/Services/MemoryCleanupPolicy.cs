namespace PCActivityTracker.Services;

/// <summary>Pure policy; process-local measurements only, no activity data or polling.</summary>
public static class MemoryCleanupPolicy
{
    public const long MinimumManagedBytes = 32L * 1024 * 1024;
    public const long MinimumIntervalMilliseconds = 2L * 60 * 1000;

    public static bool ShouldCollect(long managedBytes, long nowMilliseconds, long lastCollectionMilliseconds) =>
        managedBytes >= MinimumManagedBytes &&
        (lastCollectionMilliseconds == long.MinValue ||
         (nowMilliseconds >= lastCollectionMilliseconds &&
          nowMilliseconds - lastCollectionMilliseconds >= MinimumIntervalMilliseconds));
}
