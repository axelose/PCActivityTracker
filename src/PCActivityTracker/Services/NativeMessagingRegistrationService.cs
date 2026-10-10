using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using System.IO;

namespace PCActivityTracker.Services;

public sealed class NativeMessagingRegistrationService
{
    public const string HostName = "jp.local.pcactivitytracker.browser";
    private const string ChromeKey = @"Software\Google\Chrome\NativeMessagingHosts\" + HostName;
    private const string EdgeKey = @"Software\Microsoft\Edge\NativeMessagingHosts\" + HostName;
    private const string BraveKey = @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\" + HostName;
    private static readonly string[] BrowserRegistryKeys = [ChromeKey, EdgeKey, BraveKey];
    private readonly SettingsService _settings;
    private readonly string _dataDirectory;

    public NativeMessagingRegistrationService(SettingsService settings, string dataDirectory)
    {
        _settings = settings;
        _dataDirectory = dataDirectory;
    }

    public void Register(string extensionId)
    {
        if (DataPathService.IsPackaged)
            throw new InvalidOperationException("MSIX版のNative Messaging登録には配布インストーラーが必要です。現在はSelf-contained ZIP版を使用してください。");
        var origin = SettingsService.NormalizeBrowserOrigin("chrome-extension://" + extensionId.Trim())
            ?? throw new ArgumentException("拡張IDはa～pの英小文字32文字で入力してください。", nameof(extensionId));
        _settings.PairBrowserOrigin(origin);
        WriteRegistration();
    }

    public void RefreshRegistration()
    {
        if (DataPathService.IsPackaged || GetOrigins().Count == 0) return;
        WriteRegistration();
    }

    public void Unregister()
    {
        foreach (var keyPath in BrowserRegistryKeys)
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        var manifest = GetManifestPath();
        if (File.Exists(manifest)) File.Delete(manifest);
    }

    private void WriteRegistration()
    {
        var hostPath = FindNativeHost()
            ?? throw new FileNotFoundException("PCActivityTracker.NativeHost.exeが見つかりません。先にReleaseビルドしてください。");
        var origins = GetOrigins();
        if (origins.Count == 0) throw new InvalidOperationException("登録する拡張IDがありません。");
        var directory = Path.Combine(_dataDirectory, "NativeMessaging");
        Directory.CreateDirectory(directory);
        var manifestPath = GetManifestPath();
        var temporary = manifestPath + $".tmp-{Environment.ProcessId}";
        var json = JsonSerializer.Serialize(new
        {
            name = HostName,
            description = "PC Activity Tracker local browser bridge",
            path = hostPath,
            type = "stdio",
            allowed_origins = origins.Select(origin => origin + "/").ToArray()
        }, new JsonSerializerOptions { WriteIndented = true });
        try
        {
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            File.Move(temporary, manifestPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }

        foreach (var keyPath in BrowserRegistryKeys)
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)
                ?? throw new InvalidOperationException("Native Messagingのユーザー設定を作成できませんでした。");
            key.SetValue(null, manifestPath, RegistryValueKind.String);
        }
    }

    internal static IReadOnlyList<string> SupportedBrowserRegistryKeys => BrowserRegistryKeys;

    private IReadOnlyList<string> GetOrigins() => _settings.Current.BrowserBridgeAllowedOrigins
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(SettingsService.NormalizeBrowserOrigin).Where(value => value is not null).Cast<string>()
        .Distinct(StringComparer.Ordinal).ToArray();

    private string GetManifestPath() => Path.Combine(_dataDirectory, "NativeMessaging", HostName + ".json");

    private static string? FindNativeHost()
    {
        var installed = Path.Combine(AppContext.BaseDirectory, "PCActivityTracker.NativeHost.exe");
        if (File.Exists(installed)) return installed;
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 9 && directory is not null; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "PCActivityTracker.NativeHost", "bin", "Release",
                "net10.0-windows", "PCActivityTracker.NativeHost.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
