using System.Runtime.InteropServices;
using System.Text;

// A separate process is used for each engine. No real application DB/key is read.
if (args.Length != 6) throw new ArgumentException("DLL, temp directory, file, action, engine version, row count are required.");
var directory = Path.GetFullPath(args[1]);
if (!directory.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
    !Path.GetFileName(directory).StartsWith("PCActivityTracker-native-compat-", StringComparison.Ordinal))
    throw new ArgumentException("Only isolated native-compat test directories are permitted.");
if (args[2] is not ("database.db" or "wal.db" or "new.db" or "corrupt.db"))
    throw new ArgumentException("Unexpected test fixture filename.");
var path = Path.Combine(directory, args[2]);
const string key = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";
var action = args[3];
if (action == "create" && File.Exists(path)) throw new InvalidOperationException("Refusing to overwrite a fixture.");
if (action != "create" && !File.Exists(path)) throw new InvalidOperationException("Missing test fixture.");
using var db = new Engine(Path.GetFullPath(args[0]), path, action == "wrong-key" ? new string('F', 64) : key);
var version = db.Scalar("PRAGMA cipher_version;") ?? "missing";
if (!version.StartsWith(args[4] + " ", StringComparison.Ordinal) && version != args[4])
    throw new InvalidOperationException($"Unexpected SQLCipher version: {version}");
if (action == "wrong-key" || action == "corrupt")
{
    var rejected = false;
    try { _ = db.Scalar("SELECT count(*) FROM payload;"); }
    catch (InvalidOperationException) { rejected = true; }
    if (!rejected) throw new InvalidOperationException("An invalid fixture/key was accepted.");
    Console.WriteLine($"PASS {action}: SQLCipher {version}");
    return;
}
if (action == "create")
{
    db.Execute("PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE payload(id INTEGER PRIMARY KEY, value TEXT); INSERT INTO payload VALUES(1,'SYNTHETIC_PRIVATE_MARKER'); PRAGMA wal_checkpoint(TRUNCATE); INSERT INTO payload VALUES(2,'SYNTHETIC_PRIVATE_MARKER_2');");
    if (args[2] == "database.db")
    {
        // No writer runs while copying: the second committed row exists only in WAL.
        CopyShared(path, Path.Combine(directory, "wal.db"));
        CopyShared(path + "-wal", Path.Combine(directory, "wal.db-wal"));
    }
}
else if (action == "append") db.Execute("INSERT INTO payload VALUES(3,'SYNTHETIC_NEW_ENGINE_WRITE');");
else if (action != "verify") throw new ArgumentException("Unexpected test action.");
if (db.Scalar("SELECT count(*) FROM payload;") != args[5]) throw new InvalidOperationException("Fixture rows changed.");
if (db.Scalar("PRAGMA integrity_check;") != "ok") throw new InvalidOperationException("SQLite integrity check failed.");
if (db.Scalar("PRAGMA cipher_integrity_check;") != null) throw new InvalidOperationException("Cipher integrity check failed.");
Console.WriteLine($"PASS {action} {args[2]}: SQLCipher {version}, SQLite {db.Scalar("SELECT sqlite_version();")}, rows={args[5]}");

static void CopyShared(string source, string destination)
{
    using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
    input.CopyTo(output);
}

sealed class Engine : IDisposable
{
    private readonly IntPtr library;
    private IntPtr handle;
    private readonly Close close;
    private readonly Prepare prepare;
    private readonly Step step;
    private readonly Column column;
    private readonly FinalizeStatement finalize;
    private readonly Exec exec;
    private readonly Free free;
    public Engine(string dll, string path, string key)
    {
        library = NativeLibrary.Load(dll);
        close = Export<Close>("sqlite3_close_v2");
        prepare = Export<Prepare>("sqlite3_prepare_v2");
        step = Export<Step>("sqlite3_step");
        column = Export<Column>("sqlite3_column_text");
        finalize = Export<FinalizeStatement>("sqlite3_finalize");
        exec = Export<Exec>("sqlite3_exec");
        free = Export<Free>("sqlite3_free");
        if (Export<Open>("sqlite3_open_v2")(path, out handle, 2 | 4 | 0x10000, IntPtr.Zero) != 0)
            throw new InvalidOperationException("Native fixture open failed.");
        Execute($"PRAGMA key=\"x'{key}'\";");
    }
    private T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
    public void Execute(string sql)
    {
        var result = exec(handle, sql, IntPtr.Zero, IntPtr.Zero, out var error);
        if (error != IntPtr.Zero) free(error);
        if (result != 0) throw new InvalidOperationException($"Fixture SQL failed: {result}");
    }
    public string? Scalar(string sql)
    {
        var result = prepare(handle, sql, -1, out var statement, IntPtr.Zero);
        if (result != 0) throw new InvalidOperationException($"Fixture prepare failed: {result}");
        try
        {
            result = step(statement);
            if (result == 101) return null;
            if (result != 100) throw new InvalidOperationException($"Fixture read failed: {result}");
            return Marshal.PtrToStringUTF8(column(statement, 0));
        }
        finally { finalize(statement); }
    }
    public void Dispose()
    {
        if (handle != IntPtr.Zero) { close(handle); handle = IntPtr.Zero; }
        NativeLibrary.Free(library);
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Close(IntPtr db);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Prepare(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out IntPtr statement, IntPtr tail);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Step(IntPtr statement);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Column(IntPtr statement, int index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FinalizeStatement(IntPtr statement);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Exec(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr state, out IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Free(IntPtr memory);
}
