using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PCActivityTracker.Data;

internal static class NativeDatabaseRuntime
{
    internal const string LibraryName = "pcactivity_sqlcipher";
    private static readonly IntPtr Library;

    static NativeDatabaseRuntime()
    {
        var runtime = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException("The native database supports Windows x64 and Arm64 only.")
        };
        var path = Path.Combine(AppContext.BaseDirectory, "runtimes", runtime, "native", LibraryName + ".dll");
        if (!File.Exists(path)) throw new DllNotFoundException("The packaged native database is missing. Re-extract the complete application package.");
        // Load our own bundled library only; never search PATH or a system SQLite.
        Library = NativeLibrary.Load(path);
        var version = Marshal.GetDelegateForFunctionPointer<VersionNumber>(NativeLibrary.GetExport(Library, "sqlite3_libversion_number"));
        if (version() != 3053004 || !NativeLibrary.TryGetExport(Library, "sqlite3_key", out _))
            throw new InvalidOperationException("An unexpected native database engine was packaged.");
        NativeLibrary.SetDllImportResolver(typeof(NativeDatabaseRuntime).Assembly, Resolve);
    }

    internal static void Initialize() { _ = Library; }
    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath) =>
        name == LibraryName ? Library : IntPtr.Zero;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int VersionNumber();
}
