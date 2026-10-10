using System.IO;
using PCActivityTracker.Data;
using PCActivityTracker.Domain;

namespace CoreRegression;
internal static class MigrationTests
{
    internal static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PCActivityTracker-MigrationTest-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "test.db");
        try
        {
            using (var legacy = new SqliteDatabase(path))
            {
                legacy.Initialize();
                new ActivityRepository(legacy).Save(new ActivitySession
                {
                    StartTime = DateTimeOffset.UtcNow.AddMinutes(-1), EndTime = DateTimeOffset.UtcNow,
                    ActivityType = ActivityType.Website, AppName = "Chrome", ProcessName = "chrome",
                    Domain = "youtube.com", CreatorName = "Existing Name"
                });
                legacy.ExecuteScript("""
                    DROP INDEX ix_activity_sessions_creator_id;
                    ALTER TABLE activity_sessions DROP COLUMN creator_id;
                    PRAGMA user_version=5;
                    """);
            }
            using (var migrated = new SqliteDatabase(path))
            {
                migrated.Initialize();
                Check.That(migrated.UserVersion == 6, "Schema 5 migration did not reach schema 6.");
                var rows = new ActivityRepository(migrated).GetRange(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
                Check.That(rows.Count == 1 && rows[0].CreatorName == "Existing Name" && rows[0].CreatorId is null,
                    "Migration lost or guessed existing creator information.");
                migrated.Initialize();
                Check.That(new ActivityRepository(migrated).GetRange(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1)).Count == 1,
                    "Repeated initialization duplicated history.");
            }
            var backup = Directory.GetFiles(Path.Combine(directory, "Backups"), "activity-pre-migration-v5-*.db").Single();
            using var probe = new SqliteDatabase(backup, createMigrationBackup: false);
            Check.That(probe.Query("PRAGMA user_version;").Single().Int64("user_version") == 5, "Pre-migration backup was changed.");
            Check.That(!probe.Query("PRAGMA table_info(activity_sessions);").Any(row => row.String("name") == "creator_id"),
                "Backup no longer preserves the old schema.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
