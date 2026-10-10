using System.Text;
using System.IO;

namespace PCActivityTracker.Services;

/// <summary>
/// Small local-only rolling diagnostic log. Activity titles and URLs are never
/// intentionally written here; it is reserved for component/error information.
/// </summary>
public static class DiagnosticLogService
{
    private const long MaximumBytes = 512 * 1024;
    private static readonly object Gate = new();
    private static string? _path;

    public static string Path => _path ?? string.Empty;

    public static void Initialize(string dataDirectory)
    {
        lock (Gate) _path = System.IO.Path.Combine(dataDirectory, "diagnostics.log");
    }

    public static void Error(string component, Exception exception)
    {
        try
        {
            lock (Gate)
            {
                if (string.IsNullOrWhiteSpace(_path)) return;
                RotateIfNeeded(_path);
                // Exception messages and stack traces can contain URLs, SQL values,
                // tokens, and user paths. Keep only structured error identifiers.
                var line = $"{DateTimeOffset.Now:O} [ERROR] {component}: {exception.GetType().Name} (0x{exception.HResult:X8}){Environment.NewLine}";
                File.AppendAllText(_path, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch
        {
            // Diagnostics must never interfere with activity tracking.
        }
    }

    public static void DeleteAll()
    {
        lock (Gate)
        {
            if (string.IsNullOrWhiteSpace(_path)) return;
            DeleteIfPresent(_path);
            DeleteIfPresent(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_path)!, "diagnostics.previous.log"));
            DeleteIfPresent(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_path)!, "restore-error.log"));
        }
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void RotateIfNeeded(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < MaximumBytes) return;
        var previous = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "diagnostics.previous.log");
        File.Move(path, previous, overwrite: true);
    }
}
