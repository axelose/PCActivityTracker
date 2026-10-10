using System.Runtime.InteropServices;
using System.Text;
using System.IO;

namespace PCActivityTracker.Services;

public static class DataPathService
{
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;

    public static string GetDefaultDataDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var packageFamilyName = TryGetPackageFamilyName();
        return packageFamilyName is null
            ? Path.Combine(localAppData, "PCActivityTracker")
            : Path.Combine(localAppData, "Packages", packageFamilyName, "LocalState", "PCActivityTracker");
    }

    public static bool IsPackaged => TryGetPackageFamilyName() is not null;

    private static string? TryGetPackageFamilyName()
    {
        uint length = 0;
        var result = GetCurrentPackageFamilyName(ref length, null);
        if (result == AppModelErrorNoPackage) return null;
        if (result != ErrorInsufficientBuffer || length == 0) return null;

        var value = new StringBuilder(checked((int)length));
        result = GetCurrentPackageFamilyName(ref length, value);
        return result == 0 && value.Length > 0 ? value.ToString() : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref uint packageFamilyNameLength,
        StringBuilder? packageFamilyName);
}
