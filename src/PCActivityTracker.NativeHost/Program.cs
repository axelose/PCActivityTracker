using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

namespace PCActivityTracker.NativeHost;

internal static class Program
{
    private const string DefaultPipeName = "PCActivityTracker.Browser.Native.v1";
    private const string SelfTestPipeEnvironmentVariable = "PC_ACTIVITY_TRACKER_SELF_TEST_PIPE";
    private const int MaximumMessageBytes = 64 * 1024;

    private static async Task<int> Main(string[] args)
    {
        var origin = args.FirstOrDefault(IsExtensionOrigin);
        if (origin is null) return 2;

        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();
        while (true)
        {
            byte[]? message;
            try { message = await ReadFrameAsync(input, MaximumMessageBytes, CancellationToken.None); }
            catch { return 3; }
            if (message is null) return 0;

            byte[] response;
            try
            {
                using var document = JsonDocument.Parse(message);
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Message must be an object.");
                response = await ForwardAsync(origin, document.RootElement);
            }
            catch
            {
                response = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    ok = false,
                    error = "native_host_error",
                    message = "PC Activity Trackerへ接続できません。デスクトップアプリを起動してください。"
                });
            }

            await WriteFrameAsync(output, response, CancellationToken.None);
        }
    }

    private static async Task<byte[]> ForwardAsync(string origin, JsonElement message)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var pipeName = Environment.GetEnvironmentVariable(SelfTestPipeEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(pipeName)) pipeName = DefaultPipeName;
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        var envelope = JsonSerializer.SerializeToUtf8Bytes(new { origin, message });
        await WriteFrameAsync(pipe, envelope, timeout.Token);
        return await ReadFrameAsync(pipe, MaximumMessageBytes, timeout.Token)
            ?? throw new EndOfStreamException();
    }

    private static bool IsExtensionOrigin(string value)
    {
        const string prefix = "chrome-extension://";
        var normalized = value.Trim().TrimEnd('/').ToLowerInvariant();
        if (!normalized.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var id = normalized[prefix.Length..];
        return id.Length == 32 && id.All(character => character is >= 'a' and <= 'p');
    }

    private static async Task<byte[]?> ReadFrameAsync(Stream stream, int maximum, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var first = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken);
        if (first == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1, 3), cancellationToken);
        var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header));
        if (length <= 0 || length > maximum) throw new InvalidDataException("Native message is too large.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return payload;
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        if (payload.Length > MaximumMessageBytes) throw new InvalidDataException("Native response is too large.");
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, checked((uint)payload.Length));
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
