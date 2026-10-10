using System.IO;
using PCActivityTracker.Data;
using PCActivityTracker.Services;

namespace CoreRegression;

internal static class Check
{
    internal static void That(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed class Fixture : IDisposable
{
    internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "PCActivityTracker-CoreTest-" + Guid.NewGuid().ToString("N"));
    internal SqliteDatabase Database { get; }
    internal ActivityRepository Activities { get; }
    internal SettingsService Settings { get; }
    internal ActivityAnnotationRepository Annotations { get; }
    internal Fixture()
    {
        Database = new SqliteDatabase(Path.Combine(DirectoryPath, "test.db"));
        Database.Initialize();
        Activities = new ActivityRepository(Database);
        Annotations = new ActivityAnnotationRepository(Database);
        Settings = new SettingsService(new SettingsRepository(Database));
        Settings.Load();
    }
    public void Dispose()
    {
        Database.Dispose();
        // Exact unique directory owned by this synthetic-data fixture only.
        Directory.Delete(DirectoryPath, recursive: true);
    }
}
