using Microsoft.Win32;
using System.IO;
using Windows.ApplicationModel;

namespace PCActivityTracker.Services;

public sealed class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PCActivityTracker";
    private const string StartupTaskId = "PCActivityTrackerStartup";

    public async Task ApplyAsync(bool enabled)
    {
        if (DataPathService.IsPackaged)
        {
            await ApplyPackagedAsync(enabled);
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Windowsの自動起動設定を開けませんでした。");
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("実行ファイルの場所を取得できませんでした。");
        var launcher = FindProjectFile("scripts", "run-silent.vbs");
        var command = launcher is null
            ? BuildInstalledCommand(executable)
            : BuildDeveloperCommand(executable, launcher);
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }

    private static async Task ApplyPackagedAsync(bool enabled)
    {
        var task = await StartupTask.GetAsync(StartupTaskId);
        if (!enabled)
        {
            task.Disable();
            return;
        }

        var state = await task.RequestEnableAsync();
        if (state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy) return;

        throw new InvalidOperationException(state switch
        {
            StartupTaskState.DisabledByUser =>
                "自動起動はWindows側で無効です。設定 > アプリ > スタートアップからPC Activity Trackerを有効にしてください。",
            StartupTaskState.DisabledByPolicy => "組織のWindowsポリシーにより自動起動を有効にできません。",
            _ => $"自動起動を有効にできませんでした（{state}）。"
        });
    }

    internal static string BuildInstalledCommand(string executable) => $"{Quote(executable)} --background";

    internal static string BuildDeveloperCommand(string executable, string launcher)
    {
        var projectRoot = Directory.GetParent(Path.GetDirectoryName(launcher)!)?.FullName
            ?? throw new InvalidOperationException("プロジェクトの場所を取得できませんでした。");
        return $"wscript.exe {Quote(launcher)} {Quote(executable)} {Quote(Path.Combine(projectRoot, ".dotnet"))}";
    }

    private static string? FindProjectFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
