using System.Security.Cryptography;
using System.Text;
using System.IO;

namespace PCActivityTracker.Services;

/// <summary>
/// Owns the SQLCipher key. The random key is never stored as plaintext and can
/// only be unwrapped by the same Windows user through DPAPI.
/// </summary>
internal static class DatabaseKeyService
{
    private const string KeyFileName = "database.key";
    private static readonly object Gate = new();

    public static string GetOrCreateKeyHex(string databasePath)
    {
        lock (Gate)
        {
            var dataDirectory = FindDataDirectory(databasePath);
            Directory.CreateDirectory(dataDirectory);
            var keyPath = Path.Combine(dataDirectory, KeyFileName);
            if (File.Exists(keyPath))
            {
                var protectedValue = File.ReadAllText(keyPath, Encoding.UTF8).Trim();
                if (!ProtectedSecretService.TryUnprotectDatabaseKey(protectedValue, out var keyBase64))
                    throw new InvalidDataException("暗号化DBの鍵を現在のWindowsユーザーで復号できません。database.keyを置き換えないでください。");
                var existing = Convert.FromBase64String(keyBase64);
                if (existing.Length != 32) throw new InvalidDataException("暗号化DBの鍵が不正です。");
                return Convert.ToHexString(existing);
            }

            if (FindEncryptedDatabase(databasePath, dataDirectory) is not null)
                throw new InvalidDataException("暗号化DBは存在しますがdatabase.keyがありません。元の鍵ファイルを戻してください。");

            var key = RandomNumberGenerator.GetBytes(32);
            var protectedKey = ProtectedSecretService.ProtectDatabaseKey(Convert.ToBase64String(key));
            var temporary = keyPath + $".tmp-{Environment.ProcessId}";
            try
            {
                File.WriteAllText(temporary, protectedKey, new UTF8Encoding(false));
                File.Move(temporary, keyPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(keyPath))
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                return GetOrCreateKeyHex(databasePath);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                CryptographicOperations.ZeroMemory(key);
            }
            return Convert.ToHexString(Convert.FromBase64String(
                ProtectedSecretService.TryUnprotectDatabaseKey(protectedKey, out var created) ? created :
                throw new InvalidDataException("作成した暗号化DB鍵を確認できませんでした。")));
        }
    }

    public static string KeyPathFor(string databasePath) => Path.Combine(FindDataDirectory(databasePath), KeyFileName);

    private static bool HasPlaintextSqliteHeader(string path)
    {
        Span<byte> header = stackalloc byte[16];
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return stream.Read(header) == header.Length && header.SequenceEqual("SQLite format 3\0"u8);
    }

    private static string? FindEncryptedDatabase(string databasePath, string dataDirectory)
    {
        var candidates = new List<string> { databasePath, Path.Combine(dataDirectory, "pending-restore.db") };
        var backupDirectory = Path.Combine(dataDirectory, "Backups");
        if (Directory.Exists(backupDirectory))
            candidates.AddRange(Directory.EnumerateFiles(backupDirectory, "activity-*.db", SearchOption.TopDirectoryOnly));
        return candidates.FirstOrDefault(path => File.Exists(path) && new FileInfo(path).Length >= 16 &&
            !HasPlaintextSqliteHeader(path));
    }

    private static string FindDataDirectory(string databasePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath))
            ?? throw new InvalidOperationException("DB保存先を取得できませんでした。");
        if (Path.GetFileName(directory).Equals("Backups", StringComparison.OrdinalIgnoreCase))
            return Directory.GetParent(directory)?.FullName ?? directory;
        return directory;
    }
}
