using PCActivityTracker.Data;
using PCActivityTracker.Domain;
using System.IO;

namespace PCActivityTracker.Services;

public sealed class BackupService : IAsyncDisposable
{
    private const int MaximumBackups = 20;
    private static readonly TimeSpan AutomaticInterval = TimeSpan.FromDays(1);
    private readonly SqliteDatabase _database;
    private readonly SettingsService _settings;
    private readonly string _backupDirectory;
    private readonly object _backupGate = new();
    private CancellationTokenSource? _cancellation;
    private Task? _loop;

    public BackupService(SqliteDatabase database, SettingsService settings)
    {
        _database = database;
        _settings = settings;
        _backupDirectory = Path.Combine(Path.GetDirectoryName(database.Path)!, "Backups");
    }

    public string BackupDirectory => _backupDirectory;

    public void Start()
    {
        if (_loop is not null) return;
        _cancellation = new CancellationTokenSource();
        _loop = Task.Run(() => RunLoopAsync(_cancellation.Token));
    }

    public BackupFile CreateBackup(bool automatic = false)
    {
        lock (_backupGate)
        {
            Directory.CreateDirectory(_backupDirectory);
            var prefix = automatic ? "auto" : "manual";
            var name = $"activity-{prefix}-{DateTime.Now:yyyyMMdd-HHmmssfff}.db";
            var destination = Path.Combine(_backupDirectory, name);
            var temporary = destination + ".tmp";
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                _database.BackupTo(temporary);
                VerifyDatabase(temporary);
                File.Move(temporary, destination);
                PruneOldBackups();
                return ToBackupFile(destination);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    public IReadOnlyList<BackupFile> ListBackups()
    {
        lock (_backupGate)
        {
            if (!Directory.Exists(_backupDirectory)) return Array.Empty<BackupFile>();
            return Directory.EnumerateFiles(_backupDirectory, "activity-*.db", SearchOption.TopDirectoryOnly)
                .Select(ToBackupFile)
                .OrderByDescending(item => item.CreatedAt)
                .ToList();
        }
    }

    public void DeleteAllBackups()
    {
        lock (_backupGate)
        {
            if (Directory.Exists(_backupDirectory))
            {
                foreach (var path in Directory.EnumerateFiles(_backupDirectory, "*", SearchOption.TopDirectoryOnly))
                    File.Delete(path);
            }
            var root = Path.GetDirectoryName(_database.Path)!;
            DeleteIfPresent(Path.Combine(root, "pending-restore.db"));
            DeleteIfPresent(Path.Combine(root, "pending-restore.db.tmp"));
        }
    }

    public void ApplyRetention(DateTimeOffset cutoff)
    {
        lock (_backupGate)
        {
            if (!Directory.Exists(_backupDirectory)) return;
            foreach (var path in Directory.EnumerateFiles(_backupDirectory, "activity-*.db", SearchOption.TopDirectoryOnly).ToArray())
            {
                var file = new FileInfo(path);
                if (file.LastWriteTimeUtc < cutoff.UtcDateTime)
                {
                    file.Delete();
                    continue;
                }

                using (var backup = new SqliteDatabase(path, createMigrationBackup: false))
                {
                    backup.Initialize();
                    new ActivityRepository(backup).DeleteBefore(cutoff);
                    backup.ExecuteScript("VACUUM; PRAGMA wal_checkpoint(TRUNCATE);");
                }
                DeleteIfPresent(path + "-wal");
                DeleteIfPresent(path + "-shm");
            }
        }
    }

    public void StageRestore(string backupPath)
    {
        lock (_backupGate)
        {
            var fullBackupPath = Path.GetFullPath(backupPath);
            var fullDirectory = Path.GetFullPath(_backupDirectory) + Path.DirectorySeparatorChar;
            if (!fullBackupPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(fullBackupPath) || !fullBackupPath.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("バックアップフォルダー内の有効なDBだけを復元できます。");

            VerifyDatabase(fullBackupPath);
            var dataDirectory = Path.GetDirectoryName(_database.Path)!;
            var pending = Path.Combine(dataDirectory, "pending-restore.db");
            var temporary = pending + ".tmp";
            try
            {
                File.Copy(fullBackupPath, temporary, overwrite: true);
                VerifyDatabase(temporary);
                File.Move(temporary, pending, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    public static bool ApplyPendingRestore(string dataDirectory)
    {
        var root = Path.GetFullPath(dataDirectory);
        var databasePath = Path.Combine(root, "activity.db");
        var pending = Path.Combine(root, "pending-restore.db");
        if (!File.Exists(pending)) return false;
        VerifyDatabase(pending);

        Directory.CreateDirectory(Path.Combine(root, "Backups"));
        if (File.Exists(databasePath))
        {
            var safetyCopy = Path.Combine(root, "Backups", $"activity-before-restore-{DateTime.Now:yyyyMMdd-HHmmssfff}.db");
            File.Copy(databasePath, safetyCopy, overwrite: false);
        }
        DeleteIfPresent(databasePath + "-wal");
        DeleteIfPresent(databasePath + "-shm");
        File.Move(pending, databasePath, overwrite: true);
        return true;
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_settings.Current.AutomaticBackupsEnabled && IsAutomaticBackupDue())
                {
                    try { CreateBackup(automatic: true); }
                    catch (Exception exception) { DiagnosticLogService.Error("Backup.Automatic", exception); }
                }
                await Task.Delay(TimeSpan.FromHours(6), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private bool IsAutomaticBackupDue()
    {
        var latest = ListBackups().Where(item => item.IsAutomatic).MaxBy(item => item.CreatedAt);
        return latest is null || DateTimeOffset.Now - latest.CreatedAt >= AutomaticInterval;
    }

    private void PruneOldBackups()
    {
        var obsolete = Directory.EnumerateFiles(_backupDirectory, "activity-*.db")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Skip(MaximumBackups);
        foreach (var file in obsolete) file.Delete();
    }

    private static BackupFile ToBackupFile(string path)
    {
        var file = new FileInfo(path);
        return new BackupFile(file.Name, file.FullName, file.LastWriteTime, file.Length,
            file.Name.StartsWith("activity-auto-", StringComparison.OrdinalIgnoreCase));
    }

    private static void VerifyDatabase(string path)
    {
        using var probe = new SqliteDatabase(path, createMigrationBackup: false);
        var result = probe.Query("PRAGMA quick_check;", Array.Empty<object?>()).FirstOrDefault()?.String("quick_check");
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("バックアップDBの整合性確認に失敗しました。");
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation?.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _cancellation?.Dispose();
        _cancellation = null;
        _loop = null;
    }
}

public sealed record BackupFile(string Name, string FullPath, DateTimeOffset CreatedAt, long SizeBytes, bool IsAutomatic);
