using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO;
using PCActivityTracker.Browser;
using PCActivityTracker.Data;

namespace PCActivityTracker.Services;

/// <summary>Creates a support archive that never copies the activity DB or raw logs.</summary>
public sealed partial class DiagnosticExportService
{
    private readonly SqliteDatabase _database;
    private readonly SettingsService _settings;
    private readonly Func<BrowserBridgeDiagnostics> _browserDiagnostics;

    public DiagnosticExportService(SqliteDatabase database, SettingsService settings,
        Func<BrowserBridgeDiagnostics> browserDiagnostics)
    {
        _database = database;
        _settings = settings;
        _browserDiagnostics = browserDiagnostics;
    }

    public bool DatabaseEncrypted => _database.IsEncrypted;

    public void Export(string destinationPath)
    {
        var fullPath = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + $".tmp-{Environment.ProcessId}";
        try
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                WriteJson(archive, "environment.json", new
                {
                    formatVersion = 1,
                    createdUtc = DateTimeOffset.UtcNow,
                    appVersion = typeof(DiagnosticExportService).Assembly.GetName().Version?.ToString(3),
                    os = RuntimeInformation.OSDescription,
                    runtime = RuntimeInformation.FrameworkDescription,
                    processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                    databaseEncrypted = _database.IsEncrypted,
                    databaseSchemaVersion = _database.UserVersion,
                    databaseBytes = File.Exists(_database.Path) ? new FileInfo(_database.Path).Length : 0,
                    activityRows = _database.Query("SELECT COUNT(*) AS count FROM activity_sessions;").FirstOrDefault()?.Int64("count") ?? 0,
                    aiReportRows = _database.Query("SELECT COUNT(*) AS count FROM ai_reports;").FirstOrDefault()?.Int64("count") ?? 0,
                    privacy = "No activity rows, URLs, titles, domains, creator names, exclusion values, tokens, database files, or user paths are included."
                });
                var settings = _settings.Current;
                WriteJson(archive, "settings-summary.json", new
                {
                    settings.RecordingEnabled,
                    settings.RecordUrls,
                    settings.RecordPageTitles,
                    settings.RecordWindowTitles,
                    settings.RecordCreatorNames,
                    settings.IdleThresholdMinutes,
                    settings.RetentionDays,
                    settings.StartWithWindows,
                    settings.StartMinimized,
                    settings.AutomaticBackupsEnabled,
                    settings.UnloadOllamaAfterGeneration,
                    settings.OllamaTimeoutMinutes,
                    settings.GameSafeModeEnabled,
                    settings.RequireIdleOllama,
                    themeMode = settings.ThemeMode,
                    pairedExtensionCount = settings.BrowserBridgeAllowedOrigins.Split(';', StringSplitOptions.RemoveEmptyEntries).Length
                });
                var browser = _browserDiagnostics();
                WriteJson(archive, "browser-summary.json", new
                {
                    listenerStatus = SafeListenerStatus(browser.ListenerStatus),
                    connectionState = SafeConnectionState(browser.ConnectionState),
                    browser.LastReceivedAt,
                    browser.ExtensionVersion,
                    browser.ExpectedExtensionVersion,
                    browser.AcceptedCount,
                    browser.RejectedCount,
                    browser.ActiveClients,
                    lastResult = SafeStatus(browser.LastResult)
                });
                WriteText(archive, "errors-redacted.log", BuildRedactedErrorSummary());
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void WriteJson(ZipArchive archive, string name, object value) =>
        WriteText(archive, name, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));

    private static void WriteText(ZipArchive archive, string name, string value)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(value);
    }

    private static string BuildRedactedErrorSummary()
    {
        if (string.IsNullOrWhiteSpace(DiagnosticLogService.Path) || !File.Exists(DiagnosticLogService.Path))
            return "No diagnostic errors were recorded.";
        var result = new StringBuilder();
        foreach (var line in File.ReadLines(DiagnosticLogService.Path).TakeLast(200))
        {
            var match = ErrorLine().Match(line);
            if (match.Success)
                result.Append(match.Groups["time"].Value).Append(" [ERROR] ")
                    .Append(match.Groups["component"].Value).Append(": ")
                    .Append(match.Groups["type"].Value).AppendLine(": [message omitted for privacy]");
        }
        return result.Length == 0 ? "No safely parseable diagnostic errors were recorded." : result.ToString();
    }

    private static string SafeStatus(string value) => value switch
    {
        "受信待ち" or "アクティブタブを受信" or "Webタブなし" or "プライベートウィンドウは記録しません" => value,
        _ => "詳細はプライバシー保護のため省略"
    };

    private static string SafeListenerStatus(string value) => value switch
    {
        "Native Messaging（ユーザー専用Pipe）で待受中" => "Native Messaging listener active",
        "停止中" => "Listener stopped",
        _ => "Listener unavailable"
    };

    private static string SafeConnectionState(string value) => value switch
    {
        "拡張機能からの受信待ち" or "接続済み" or "最終受信から2分以上経過" => value,
        _ => "状態の詳細は省略"
    };

    [GeneratedRegex("^(?<time>\\S+) \\[ERROR\\] (?<component>[A-Za-z0-9_.-]+): (?<type>[A-Za-z0-9_.+]+):")]
    private static partial Regex ErrorLine();
}
