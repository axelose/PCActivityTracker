using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace PCActivityTracker.Services;

/// <summary>Protects local secrets with Windows DPAPI for the current Windows user.</summary>
internal static class ProtectedSecretService
{
    private const int CryptProtectUiForbidden = 0x1;
    private const string Prefix = "dpapi:v1:";
    private const string DatabaseKeyPrefix = "dpapi:database:v1:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PCActivityTracker.BrowserBridgeToken.v1");
    private static readonly byte[] DatabaseKeyEntropy = Encoding.UTF8.GetBytes("PCActivityTracker.DatabaseKey.v1");

    public static string Protect(string value)
    {
        var protectedBytes = Transform(Encoding.UTF8.GetBytes(value), Entropy, protect: true);
        return Prefix + Convert.ToBase64String(protectedBytes);
    }

    public static bool TryUnprotect(string? value, out string secret)
    {
        secret = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        try
        {
            var bytes = Convert.FromBase64String(value[Prefix.Length..]);
            secret = Encoding.UTF8.GetString(Transform(bytes, Entropy, protect: false));
            return !string.IsNullOrWhiteSpace(secret);
        }
        catch (Exception exception) when (exception is FormatException or Win32Exception or ExternalException)
        {
            return false;
        }
    }

    public static string ProtectDatabaseKey(string value) => DatabaseKeyPrefix +
        Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(value), DatabaseKeyEntropy, protect: true));

    public static bool TryUnprotectDatabaseKey(string? value, out string secret)
    {
        secret = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(DatabaseKeyPrefix, StringComparison.Ordinal)) return false;
        try
        {
            var bytes = Convert.FromBase64String(value[DatabaseKeyPrefix.Length..]);
            secret = Encoding.UTF8.GetString(Transform(bytes, DatabaseKeyEntropy, protect: false));
            return !string.IsNullOrWhiteSpace(secret);
        }
        catch (Exception exception) when (exception is FormatException or Win32Exception or ExternalException)
        {
            return false;
        }
    }

    private static byte[] Transform(byte[] inputBytes, byte[] entropyBytes, bool protect)
    {
        var input = Allocate(inputBytes);
        var entropy = Allocate(entropyBytes);
        DataBlob output = default;
        try
        {
            var succeeded = protect
                ? CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output);
            if (!succeeded) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Size];
            if (output.Size > 0) Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            if (input.Data != IntPtr.Zero) Marshal.FreeHGlobal(input.Data);
            if (entropy.Data != IntPtr.Zero) Marshal.FreeHGlobal(entropy.Data);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }

    private static DataBlob Allocate(byte[] bytes)
    {
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return new DataBlob { Size = bytes.Length, Data = pointer };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, ref DataBlob optionalEntropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob optionalEntropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
