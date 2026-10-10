using System.Runtime.InteropServices;
using System.Text;
using System.IO;
using PCActivityTracker.Services;

namespace PCActivityTracker.Data;

/// <summary>
/// Small serialized SQLCipher access layer. The key is supplied immediately after
/// opening each connection and is protected separately by Windows DPAPI.
/// </summary>
public sealed class SqliteDatabase : IDisposable
{
    public const int CurrentSchemaVersion = 6;
    private readonly string _path;
    private readonly bool _createMigrationBackup;
    private readonly string? _keyHex;
    private readonly object _gate = new();
    private SqliteConnection? _connection;

    static SqliteDatabase() => NativeDatabaseRuntime.Initialize();

    public SqliteDatabase(string path, bool createMigrationBackup = true, bool encryptionEnabled = true)
    {
        _path = path;
        _createMigrationBackup = createMigrationBackup;
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        if (encryptionEnabled)
        {
            _keyHex = DatabaseKeyService.GetOrCreateKeyHex(path);
            RecoverInterruptedEncryptionMigration();
            if (IsPlaintextSqlite(path)) MigratePlaintextDatabase();
        }
    }

    public string Path => _path;
    public bool IsEncrypted => _keyHex is not null;

    public void Initialize()
    {
        const string configuration = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA foreign_keys=ON;
            PRAGMA secure_delete=ON;
            PRAGMA busy_timeout=5000;
            """;

        const string schema = """
            CREATE TABLE IF NOT EXISTS activity_sessions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                start_utc_ms INTEGER NOT NULL,
                end_utc_ms INTEGER NOT NULL,
                duration_seconds INTEGER NOT NULL,
                activity_type TEXT NOT NULL,
                app_name TEXT NOT NULL,
                process_name TEXT NOT NULL,
                window_title TEXT NULL,
                executable_name TEXT NULL,
                browser_name TEXT NULL,
                url TEXT NULL,
                domain TEXT NULL,
                page_title TEXT NULL,
                creator_name TEXT NULL,
                creator_id TEXT NULL,
                is_idle INTEGER NOT NULL DEFAULT 0,
                category TEXT NOT NULL DEFAULT 'Other',
                created_utc_ms INTEGER NOT NULL,
                updated_utc_ms INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_activity_sessions_time
                ON activity_sessions(start_utc_ms, end_utc_ms);
            CREATE INDEX IF NOT EXISTS ix_activity_sessions_app
                ON activity_sessions(app_name, start_utc_ms);
            CREATE INDEX IF NOT EXISTS ix_activity_sessions_domain
                ON activity_sessions(domain, start_utc_ms);
            CREATE INDEX IF NOT EXISTS ix_activity_sessions_end
                ON activity_sessions(end_utc_ms);

            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS exclusions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                exclusion_type TEXT NOT NULL,
                value TEXT NOT NULL COLLATE NOCASE,
                UNIQUE(exclusion_type, value)
            );

            CREATE TABLE IF NOT EXISTS category_rules (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                target_type TEXT NOT NULL,
                pattern TEXT NOT NULL COLLATE NOCASE,
                category TEXT NOT NULL COLLATE NOCASE,
                match_mode TEXT NOT NULL DEFAULT 'Wildcard',
                color TEXT NOT NULL DEFAULT '#64748B',
                priority INTEGER NOT NULL DEFAULT 100,
                created_utc_ms INTEGER NOT NULL,
                UNIQUE(target_type, pattern)
            );

            CREATE INDEX IF NOT EXISTS ix_category_rules_target
                ON category_rules(target_type, pattern);

            CREATE TABLE IF NOT EXISTS ai_reports (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                report_type TEXT NOT NULL,
                period_start_utc_ms INTEGER NOT NULL,
                period_end_utc_ms INTEGER NOT NULL,
                generated_utc_ms INTEGER NOT NULL,
                model_name TEXT NOT NULL,
                content TEXT NOT NULL,
                prompt_version INTEGER NOT NULL DEFAULT 1,
                UNIQUE(report_type, period_start_utc_ms)
            );

            CREATE INDEX IF NOT EXISTS ix_ai_reports_period
                ON ai_reports(period_start_utc_ms, period_end_utc_ms);

            CREATE TABLE IF NOT EXISTS activity_annotations (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                start_utc_ms INTEGER NOT NULL,
                end_utc_ms INTEGER NOT NULL,
                project_name TEXT NOT NULL DEFAULT '',
                task_name TEXT NOT NULL DEFAULT '',
                note TEXT NOT NULL DEFAULT '',
                category_override TEXT NOT NULL DEFAULT '',
                created_utc_ms INTEGER NOT NULL,
                updated_utc_ms INTEGER NOT NULL,
                CHECK(end_utc_ms > start_utc_ms)
            );

            CREATE INDEX IF NOT EXISTS ix_activity_annotations_time
                ON activity_annotations(start_utc_ms, end_utc_ms);
            """;

        lock (_gate)
        {
            var databaseExisted = File.Exists(_path);
            Connection.ExecuteScript(configuration);
            var version = ReadUserVersionLocked();
            if (version > CurrentSchemaVersion)
                throw new InvalidDataException($"このDBは新しいアプリ用です（DB v{version} / 対応 v{CurrentSchemaVersion}）。");
            if (_createMigrationBackup && databaseExisted && version < CurrentSchemaVersion) CreateMigrationBackupLocked(version);

            Connection.ExecuteScript("BEGIN IMMEDIATE;");
            try
            {
                Connection.ExecuteScript(schema);
                var columns = Connection.Query("PRAGMA table_info(activity_sessions);", Array.Empty<object?>());
                if (!columns.Any(row => string.Equals(row.String("name"), "creator_name", StringComparison.OrdinalIgnoreCase)))
                    Connection.ExecuteScript("ALTER TABLE activity_sessions ADD COLUMN creator_name TEXT NULL;");
                if (!columns.Any(row => string.Equals(row.String("name"), "creator_id", StringComparison.OrdinalIgnoreCase)))
                    Connection.ExecuteScript("ALTER TABLE activity_sessions ADD COLUMN creator_id TEXT NULL;");

                var categoryColumns = Connection.Query("PRAGMA table_info(category_rules);", Array.Empty<object?>());
                if (!categoryColumns.Any(row => string.Equals(row.String("name"), "match_mode", StringComparison.OrdinalIgnoreCase)))
                    Connection.ExecuteScript("ALTER TABLE category_rules ADD COLUMN match_mode TEXT NOT NULL DEFAULT 'Wildcard';");
                if (!categoryColumns.Any(row => string.Equals(row.String("name"), "color", StringComparison.OrdinalIgnoreCase)))
                    Connection.ExecuteScript("ALTER TABLE category_rules ADD COLUMN color TEXT NOT NULL DEFAULT '#64748B';");
                if (!categoryColumns.Any(row => string.Equals(row.String("name"), "priority", StringComparison.OrdinalIgnoreCase)))
                    Connection.ExecuteScript("ALTER TABLE category_rules ADD COLUMN priority INTEGER NOT NULL DEFAULT 100;");

                Connection.ExecuteScript($"""
                    CREATE INDEX IF NOT EXISTS ix_activity_sessions_creator
                        ON activity_sessions(creator_name, start_utc_ms);
                    CREATE INDEX IF NOT EXISTS ix_activity_sessions_creator_id
                        ON activity_sessions(creator_id, start_utc_ms);
                    PRAGMA user_version={CurrentSchemaVersion};
                    COMMIT;
                    """);
            }
            catch
            {
                try { Connection.ExecuteScript("ROLLBACK;"); } catch { }
                throw;
            }
        }
    }

    public int UserVersion
    {
        get { lock (_gate) return ReadUserVersionLocked(); }
    }

    private int ReadUserVersionLocked() => checked((int)(Connection.Query("PRAGMA user_version;", Array.Empty<object?>())
        .FirstOrDefault()?.Int64("user_version") ?? 0));

    private void CreateMigrationBackupLocked(int version)
    {
        var backupDirectory = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_path)!, "Backups");
        Directory.CreateDirectory(backupDirectory);
        var destination = System.IO.Path.Combine(backupDirectory,
            $"activity-pre-migration-v{version}-{DateTime.Now:yyyyMMdd-HHmmssfff}.db");
        Connection.BackupTo(destination, _keyHex);
    }

    public long Execute(string sql, params object?[] parameters)
    {
        lock (_gate)
        {
            return Connection.Execute(sql, parameters);
        }
    }

    public IReadOnlyList<SqliteRow> Query(string sql, params object?[] parameters)
    {
        lock (_gate)
        {
            return Connection.Query(sql, parameters);
        }
    }

    public void ExecuteScript(string sql)
    {
        lock (_gate) Connection.ExecuteScript(sql);
    }

    public void BackupTo(string destinationPath)
    {
        lock (_gate) Connection.BackupTo(destinationPath, _keyHex);
    }

    private SqliteConnection Connection => _connection ??= new SqliteConnection(_path, _keyHex);

    private void RecoverInterruptedEncryptionMigration()
    {
        var plaintextBackup = _path + ".plaintext-migration";
        var encryptedTemporary = _path + ".encrypted-migration";
        if (!File.Exists(_path) && File.Exists(plaintextBackup))
            File.Move(plaintextBackup, _path);
        if (File.Exists(_path) && File.Exists(plaintextBackup)) SecureDeleteBestEffort(plaintextBackup);
        if (File.Exists(encryptedTemporary)) File.Delete(encryptedTemporary);
    }

    private void MigratePlaintextDatabase()
    {
        if (_keyHex is null) return;
        var encryptedTemporary = _path + ".encrypted-migration";
        var plaintextBackup = _path + ".plaintext-migration";
        DeleteIfPresent(encryptedTemporary);
        DeleteIfPresent(plaintextBackup);

        try
        {
            int version;
            using (var source = new SqliteConnection(_path, keyHex: null))
            {
                source.ExecuteScript("PRAGMA wal_checkpoint(TRUNCATE);");
                version = checked((int)(source.Query("PRAGMA user_version;", Array.Empty<object?>())
                    .FirstOrDefault()?.Int64("user_version") ?? 0));
                var destination = EscapeSqlString(encryptedTemporary);
                source.ExecuteScript($"""
                    ATTACH DATABASE '{destination}' AS encrypted KEY "x'{_keyHex}'";
                    SELECT sqlcipher_export('encrypted');
                    PRAGMA encrypted.user_version={version};
                    DETACH DATABASE encrypted;
                    """);
            }

            using (var probe = new SqliteConnection(encryptedTemporary, _keyHex))
            {
                var result = probe.Query("PRAGMA quick_check;", Array.Empty<object?>()).FirstOrDefault()?.String("quick_check");
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("暗号化後のDB整合性確認に失敗しました。");
            }

            File.Move(_path, plaintextBackup);
            File.Move(encryptedTemporary, _path);
            DeleteIfPresent(_path + "-wal");
            DeleteIfPresent(_path + "-shm");
            SecureDeleteBestEffort(plaintextBackup);
        }
        catch
        {
            if (!File.Exists(_path) && File.Exists(plaintextBackup)) File.Move(plaintextBackup, _path);
            throw;
        }
        finally
        {
            DeleteIfPresent(encryptedTemporary);
        }
    }

    private static bool IsPlaintextSqlite(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < 16) return false;
        Span<byte> header = stackalloc byte[16];
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return stream.Read(header) == header.Length && header.SequenceEqual("SQLite format 3\0"u8);
    }

    private static string EscapeSqlString(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void SecureDeleteBestEffort(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, 1024 * 1024,
                FileOptions.WriteThrough | FileOptions.SequentialScan);
            var zeros = new byte[1024 * 1024];
            var remaining = stream.Length;
            stream.Position = 0;
            while (remaining > 0)
            {
                var count = (int)Math.Min(remaining, zeros.Length);
                stream.Write(zeros, 0, count);
                remaining -= count;
            }
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            File.Delete(path);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _connection?.Dispose();
            _connection = null;
        }
    }
}

public sealed class SqliteRow
{
    private readonly Dictionary<string, object?> _values;

    internal SqliteRow(Dictionary<string, object?> values) => _values = values;

    public string? String(string name) => _values.TryGetValue(name, out var value) ? value as string : null;
    public long Int64(string name) => _values.TryGetValue(name, out var value) && value is long number ? number : 0;
    public bool Boolean(string name) => Int64(name) != 0;
}

internal sealed class SqliteConnection : IDisposable
{
    private const int Ok = 0;
    private const int Row = 100;
    private const int Done = 101;
    private const int OpenReadWrite = 0x00000002;
    private const int OpenCreate = 0x00000004;
    private const int OpenFullMutex = 0x00010000;
    private static readonly IntPtr Transient = new(-1);
    private IntPtr _db;

    public SqliteConnection(string path, string? keyHex)
    {
        var result = Native.sqlite3_open_v2(path, out _db, OpenReadWrite | OpenCreate | OpenFullMutex, null);
        if (result != Ok)
        {
            var message = ErrorMessage();
            Dispose();
            throw new InvalidOperationException($"SQLite database could not be opened: {message} ({result})");
        }
        if (!string.IsNullOrWhiteSpace(keyHex)) ExecuteScript($"PRAGMA key=\"x'{keyHex}'\";");
        ExecuteScript("PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL; PRAGMA secure_delete=ON;");
    }

    public void ExecuteScript(string sql)
    {
        var result = Native.sqlite3_exec(_db, sql, IntPtr.Zero, IntPtr.Zero, out var error);
        if (result == Ok) return;
        var message = error == IntPtr.Zero ? ErrorMessage() : Marshal.PtrToStringUTF8(error) ?? "Unknown SQLite error";
        if (error != IntPtr.Zero) Native.sqlite3_free(error);
        throw new InvalidOperationException($"SQLite script failed: {message} ({result})");
    }

    public long Execute(string sql, object?[] parameters)
    {
        var statement = Prepare(sql);
        try
        {
            BindAll(statement, parameters);
            var result = Native.sqlite3_step(statement);
            if (result != Done) Throw(result);
            return Native.sqlite3_last_insert_rowid(_db);
        }
        finally
        {
            Native.sqlite3_finalize(statement);
        }
    }

    public IReadOnlyList<SqliteRow> Query(string sql, object?[] parameters)
    {
        var statement = Prepare(sql);
        var rows = new List<SqliteRow>();
        try
        {
            BindAll(statement, parameters);
            while (true)
            {
                var result = Native.sqlite3_step(statement);
                if (result == Done) break;
                if (result != Row) Throw(result);

                var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                var count = Native.sqlite3_column_count(statement);
                for (var i = 0; i < count; i++)
                {
                    var name = Marshal.PtrToStringUTF8(Native.sqlite3_column_name(statement, i)) ?? $"column{i}";
                    values[name] = ReadColumn(statement, i);
                }
                rows.Add(new SqliteRow(values));
            }
            return rows;
        }
        finally
        {
            Native.sqlite3_finalize(statement);
        }
    }

    public void BackupTo(string destinationPath, string? destinationKeyHex)
    {
        using var destination = new SqliteConnection(destinationPath, destinationKeyHex);
        var backup = Native.sqlite3_backup_init(destination._db, "main", _db, "main");
        if (backup == IntPtr.Zero)
            throw new InvalidOperationException($"SQLite backup could not start: {destination.ErrorMessage()}");

        var stepResult = Native.sqlite3_backup_step(backup, -1);
        var finishResult = Native.sqlite3_backup_finish(backup);
        if (stepResult != Done || finishResult != Ok)
            throw new InvalidOperationException($"SQLite backup failed ({stepResult}/{finishResult}).");
    }

    private IntPtr Prepare(string sql)
    {
        var result = Native.sqlite3_prepare_v2(_db, sql, -1, out var statement, IntPtr.Zero);
        if (result != Ok) Throw(result);
        return statement;
    }

    private static void BindAll(IntPtr statement, IReadOnlyList<object?> parameters)
    {
        for (var index = 0; index < parameters.Count; index++)
        {
            var position = index + 1;
            var value = parameters[index];
            var result = value switch
            {
                null => Native.sqlite3_bind_null(statement, position),
                bool boolean => Native.sqlite3_bind_int64(statement, position, boolean ? 1 : 0),
                int integer => Native.sqlite3_bind_int64(statement, position, integer),
                long integer => Native.sqlite3_bind_int64(statement, position, integer),
                DateTimeOffset date => Native.sqlite3_bind_int64(statement, position, date.ToUnixTimeMilliseconds()),
                _ => Native.sqlite3_bind_text(statement, position, value.ToString() ?? string.Empty, -1, Transient)
            };
            if (result != Ok) throw new InvalidOperationException($"SQLite parameter {position} could not be bound ({result}).");
        }
    }

    private static object? ReadColumn(IntPtr statement, int index)
    {
        return Native.sqlite3_column_type(statement, index) switch
        {
            1 => Native.sqlite3_column_int64(statement, index),
            2 => Native.sqlite3_column_double(statement, index),
            3 => Marshal.PtrToStringUTF8(Native.sqlite3_column_text(statement, index)),
            5 => null,
            _ => Marshal.PtrToStringUTF8(Native.sqlite3_column_text(statement, index))
        };
    }

    private string ErrorMessage() => _db == IntPtr.Zero
        ? "Unknown SQLite error"
        : Marshal.PtrToStringUTF8(Native.sqlite3_errmsg(_db)) ?? "Unknown SQLite error";

    private void Throw(int result) => throw new InvalidOperationException($"SQLite operation failed: {ErrorMessage()} ({result})");

    public void Dispose()
    {
        if (_db == IntPtr.Zero) return;
        Native.sqlite3_close_v2(_db);
        _db = IntPtr.Zero;
    }

    private static class Native
    {
        private const string Library = NativeDatabaseRuntime.LibraryName;

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out IntPtr db, int flags, string? vfs);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_close_v2(IntPtr db);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_exec(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr arg, out IntPtr error);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void sqlite3_free(IntPtr pointer);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out IntPtr statement, IntPtr tail);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_step(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_finalize(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_bind_null(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_bind_text(IntPtr statement, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int bytes, IntPtr destructor);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern long sqlite3_last_insert_rowid(IntPtr db);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_column_count(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr sqlite3_column_name(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_column_type(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern long sqlite3_column_int64(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern double sqlite3_column_double(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr sqlite3_column_text(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr sqlite3_errmsg(IntPtr db);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr sqlite3_backup_init(IntPtr destinationDb,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string destinationName, IntPtr sourceDb,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string sourceName);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_backup_step(IntPtr backup, int pages);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_backup_finish(IntPtr backup);
    }
}
