using PCActivityTracker.Data;
using PCActivityTracker.Domain;
using PCActivityTracker.Services;
using PCActivityTracker.Browser;
using PCActivityTracker.Tracking;
using System.Net.Http;
using System.Text;
using System.IO;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Collections.Specialized;
using LocalAi.Runtime;

namespace PCActivityTracker;

// Shared synthetic fixture transport; suite logic remains in the integration runner.
internal static partial class SelfTestRunner
{
    private static async Task<byte[]> RoundTripThroughNativeHostAsync(string origin, string pipeName, object message)
    {
        var nativeHostDll = FindNativeHostFile("PCActivityTracker.NativeHost.dll")
            ?? throw new FileNotFoundException("The built Native Messaging host DLL was not found.");
        var nativeHostExe = Path.Combine(Path.GetDirectoryName(nativeHostDll)!, "PCActivityTracker.NativeHost.exe");
        var currentExecutable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The current .NET executable path is unavailable.");
        var launchWithDotnet = Path.GetFileNameWithoutExtension(currentExecutable)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = launchWithDotnet ? currentExecutable : nativeHostExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (launchWithDotnet) startInfo.ArgumentList.Add(nativeHostDll);
        startInfo.ArgumentList.Add(origin + "/");
        startInfo.Environment["PC_ACTIVITY_TRACKER_SELF_TEST_PIPE"] = pipeName;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The Native Messaging host process could not be started.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await WriteNativeFrameAsync(process.StandardInput.BaseStream,
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(message), timeout.Token).ConfigureAwait(false);
            var response = await ReadNativeFrameAsync(process.StandardOutput.BaseStream, timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
                throw new InvalidOperationException($"Native Messaging host exited with {process.ExitCode}: {error}");
            }
            return response;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private static bool FileContainsUtf8(string path, string value)
    {
        if (!File.Exists(path)) return false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        if (stream.Length > int.MaxValue) throw new InvalidDataException("Self-test database is unexpectedly large.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(value)) >= 0;
    }

    private static string? FindNativeHostFile(string fileName)
    {
        var installed = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(installed)) return installed;
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 9 && directory is not null; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "PCActivityTracker.NativeHost", "bin", "Release",
                "net10.0-windows", fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static async Task WriteNativeFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, checked((uint)payload.Length));
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadNativeFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header));
        if (length <= 0 || length > 64 * 1024)
            throw new InvalidDataException("The Native Messaging host returned an invalid frame length.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    private sealed class OllamaBusyProbeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/api/ps")
            {
                const string json = """
                    {"models":[{"model":"busy-model","size":1000,"size_vram":900,"expires_at":"2026-01-15T12:00:00Z","context_length":4096}]}
                    """;
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }

            throw new InvalidOperationException("Ollama generation endpoint was called despite a busy runtime.");
        }
    }
}
