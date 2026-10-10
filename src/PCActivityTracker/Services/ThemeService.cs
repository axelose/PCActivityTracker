using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace PCActivityTracker.Services;

public static class ThemeService
{
    private static readonly IReadOnlyDictionary<string, string> LightPalette = new Dictionary<string, string>
    {
        ["Ink"] = "#172033", ["MutedInk"] = "#6C7486", ["Accent"] = "#5B5CE2",
        ["AccentSoft"] = "#ECECFF", ["Panel"] = "#FFFFFF", ["Canvas"] = "#F3F5F9",
        ["Stroke"] = "#E3E7EF", ["SubtlePanel"] = "#F7F8FB", ["Track"] = "#EDF0F6",
        ["ControlBackground"] = "#FFFFFF", ["Sidebar"] = "#182039", ["SidebarHover"] = "#35415F",
        ["SidebarPressed"] = "#465476", ["SidebarCard"] = "#202A47", ["SidebarText"] = "#F2F4FF",
        ["SidebarMuted"] = "#96A1C1", ["Success"] = "#267A61", ["WebAccent"] = "#32B8B0",
        ["CreatorAccent"] = "#9B65D1", ["IdleAccent"] = "#A9B0BF", ["Danger"] = "#B72945",
        ["DangerBackground"] = "#FFF0F2", ["DangerBorder"] = "#F4CBD3"
    };

    private static readonly IReadOnlyDictionary<string, string> DarkPalette = new Dictionary<string, string>
    {
        ["Ink"] = "#E7EAF2", ["MutedInk"] = "#A7B0C2", ["Accent"] = "#8B8CF5",
        ["AccentSoft"] = "#25264A", ["Panel"] = "#1B2130", ["Canvas"] = "#111722",
        ["Stroke"] = "#343C4D", ["SubtlePanel"] = "#242B3A", ["Track"] = "#30394A",
        ["ControlBackground"] = "#202737", ["Sidebar"] = "#0D1324", ["SidebarHover"] = "#293451",
        ["SidebarPressed"] = "#374563", ["SidebarCard"] = "#17213A", ["SidebarText"] = "#F2F4FF",
        ["SidebarMuted"] = "#A4AEC8", ["Success"] = "#5CD4AE", ["WebAccent"] = "#48C9C1",
        ["CreatorAccent"] = "#B78AE4", ["IdleAccent"] = "#7F899B", ["Danger"] = "#FF8298",
        ["DangerBackground"] = "#3A1F29", ["DangerBorder"] = "#6D3443"
    };

    public static string Apply(string? mode)
    {
        var effective = ResolveEffectiveTheme(mode);
        if (Application.Current is null) return effective;
        var palette = effective == "Dark" ? DarkPalette : LightPalette;
        foreach (var (key, value) in palette)
            Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        foreach (Window window in Application.Current.Windows) ApplyWindowTheme(window, effective);
        return effective;
    }

    public static string ResolveEffectiveTheme(string? mode)
    {
        if (string.Equals(mode, "Dark", StringComparison.OrdinalIgnoreCase)) return "Dark";
        if (string.Equals(mode, "Light", StringComparison.OrdinalIgnoreCase)) return "Light";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: false);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0 ? "Dark" : "Light";
        }
        catch { return "Light"; }
    }

    public static void ApplyWindowTheme(Window window, string? effectiveTheme = null)
    {
        effectiveTheme ??= ResolveEffectiveTheme("System");
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var enabled = string.Equals(effectiveTheme, "Dark", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
            _ = DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
