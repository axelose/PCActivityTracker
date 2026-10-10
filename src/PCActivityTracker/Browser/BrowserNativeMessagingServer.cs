using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PCActivityTracker.Services;
using PCActivityTracker.Tracking;
using PCActivityTracker.Domain;

namespace PCActivityTracker.Browser;

/// <summary>
/// Receives browser data from the small Chromium Native Messaging host. The
/// pipe is restricted by Windows to the current user and every request is also
/// bound to an explicitly registered extension origin and a DPAPI-held token.
/// </summary>
public sealed class BrowserNativeMessagingServer : IAsyncDisposable
{
    public const string PipeName = "PCActivityTracker.Browser.Native.v1";
    public const string ExpectedExtensionVersion = "3.2.3";
    private const int MaximumMessageBytes = 64 * 1024;
    private const int MaximumConcurrentClients = 4;
    private readonly BrowserStateStore _store;
    private readonly SettingsService _settings;
    private readonly string _pipeName;
    private readonly object _statusGate = new();
    private readonly SemaphoreSlim _clientSlots = new(MaximumConcurrentClients, MaximumConcurrentClients);
    private readonly ConcurrentDictionary<long, Task> _activeClients = new();
    private CancellationTokenSource? _cancellation;
    private Task? _acceptLoop;
    private long _nextClientId;
    private long _acceptedCount;
    private long _rejectedCount;
    private DateTimeOffset? _lastReceivedAt;
    private string? _lastDomain;
    private string? _lastBrowser;
    private string? _lastCreator;
    private string? _lastExtensionVersion;
    private string _lastResult = "受信待ち";

    public BrowserNativeMessagingServer(BrowserStateStore store, SettingsService settings, string? pipeName = null)
    {
        _store = store;
        _settings = settings;
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? PipeName : pipeName;
    }

    public string Status { get; private set; } = "停止中";
    public string DisplayStatus
    {
        get
        {
            lock (_statusGate)
                return _lastReceivedAt is null ? Status + " / サイト情報は未受信" :
                    $"{Status} / 最終受信 {_lastReceivedAt:HH:mm:ss}  {_lastDomain}";
        }
    }

    public void Start()
    {
        if (_acceptLoop is not null) return;
        _cancellation = new CancellationTokenSource();
        Status = "Native Messaging（ユーザー専用Pipe）で待受中";
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cancellation.Token));
    }

    public BrowserBridgeDiagnostics GetDiagnostics()
    {
        lock (_statusGate)
        {
            var connectionState = _lastReceivedAt is null ? "拡張機能からの受信待ち" :
                DateTimeOffset.Now - _lastReceivedAt <= TimeSpan.FromMinutes(2) ? "接続済み" : "最終受信から2分以上経過";
            return new BrowserBridgeDiagnostics(Status, connectionState, _lastReceivedAt, _lastBrowser,
                _lastDomain, _lastCreator, _lastExtensionVersion, ExpectedExtensionVersion,
                Interlocked.Read(ref _acceptedCount), Interlocked.Read(ref _rejectedCount),
                _activeClients.Count, _lastResult);
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, MaximumConcurrentClients,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                    4096, 4096);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                if (!_clientSlots.Wait(0))
                {
                    pipe.Dispose();
                    pipe = null;
                    Interlocked.Increment(ref _rejectedCount);
                    continue;
                }
                var id = Interlocked.Increment(ref _nextClientId);
                var accepted = pipe;
                pipe = null;
                var task = HandleClientSafeAsync(accepted, cancellationToken);
                _activeClients[id] = task;
                _ = ObserveClientAsync(id, task);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (IOException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                lock (_statusGate) _lastResult = "待受エラー: " + exception.GetType().Name;
                if (!cancellationToken.IsCancellationRequested)
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
            finally { pipe?.Dispose(); }
        }
    }

    private async Task ObserveClientAsync(long id, Task task)
    {
        try { await task.ConfigureAwait(false); }
        finally { _activeClients.TryRemove(id, out _); }
    }

    private async Task HandleClientSafeAsync(NamedPipeServerStream pipe, CancellationToken shutdownToken)
    {
        await using (pipe)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(4));
                var request = await ReadFrameAsync(pipe, timeout.Token).ConfigureAwait(false);
                if (request is null) return;
                var response = ProcessEnvelope(request);
                await WriteFrameAsync(pipe, response, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                Interlocked.Increment(ref _rejectedCount);
                lock (_statusGate) _lastResult = "受信エラー: " + exception.GetType().Name;
            }
            finally { _clientSlots.Release(); }
        }
    }

    internal byte[] ProcessEnvelope(byte[] request)
    {
        try
        {
            using var document = JsonDocument.Parse(request);
            var root = document.RootElement;
            var origin = root.TryGetProperty("origin", out var originElement) ? originElement.GetString() : null;
            if (origin is null || !_settings.IsBrowserOriginPaired(origin)) return Reject("origin_not_allowed", "拡張IDが未登録です。");
            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                return Reject("invalid_message", "要求形式が不正です。");
            var type = message.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;

            if (type == "challenge")
            {
                var nonce = message.TryGetProperty("nonce", out var nonceElement) ? nonceElement.GetString() ?? string.Empty : string.Empty;
                if (!IsValidNonce(nonce)) return Reject("invalid_nonce", "認証要求が不正です。");
                return JsonSerializer.SerializeToUtf8Bytes(new { ok = true, proof = CreateChallengeProof(nonce) });
            }

            if (type != "activeTab") return Reject("unsupported_message", "未対応の要求です。");
            var token = message.TryGetProperty("token", out var tokenElement) ? tokenElement.GetString() ?? string.Empty : string.Empty;
            if (!TokenMatches(token)) return Reject("unauthorized", "連携トークンが一致しません。");
            if (!message.TryGetProperty("payload", out var payloadElement)) return Reject("invalid_payload", "ブラウザ情報がありません。");
            var payload = payloadElement.Deserialize<ActiveTabPayload>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var browser = NormalizeBrowser(payload?.Browser);
            if (payload is null || browser is null) return Reject("invalid_payload", "ブラウザ情報が不正です。");

            var receivedAt = DateTimeOffset.Now;
            var observedAt = ResolveObservedAt(payload.ObservedAtUnixMs, receivedAt);
            if (!payload.Active || payload.Incognito)
            {
                _store.Clear(browser, observedAt);
                Accept(receivedAt, browser, payload.Incognito ? "プライベートウィンドウ（記録なし）" : "Webタブなし",
                    null, payload.ExtensionVersion, payload.Incognito ? "プライベートウィンドウは記録しません" : "Webタブなし");
                return JsonSerializer.SerializeToUtf8Bytes(new { ok = true });
            }

            if (string.IsNullOrWhiteSpace(payload.Url) || !Uri.TryCreate(payload.Url, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https")) return Reject("invalid_payload", "URL形式が不正です。");
            var sanitizedUrl = UrlPrivacyService.Sanitize(payload.Url);
            if (sanitizedUrl is null) return Reject("invalid_payload", "URLを安全に処理できません。");
            var creator = NormalizeText(payload.CreatorName, 200);
            var domain = uri.IdnHost.ToLowerInvariant();
            _store.Update(new BrowserActivity(browser, sanitizedUrl, domain,
                NormalizeText(payload.Title, 500) ?? string.Empty, creator, observedAt, false));
            Accept(receivedAt, browser, domain, creator, payload.ExtensionVersion, "アクティブタブを受信");
            return JsonSerializer.SerializeToUtf8Bytes(new { ok = true });
        }
        catch (JsonException) { return Reject("invalid_json", "要求形式が不正です。"); }
    }

    private void Accept(DateTimeOffset receivedAt, string browser, string domain, string? creator,
        string? version, string result)
    {
        lock (_statusGate)
        {
            _lastReceivedAt = receivedAt;
            _lastBrowser = browser;
            _lastDomain = domain;
            _lastCreator = creator;
            _lastExtensionVersion = NormalizeText(version, 32);
            _lastResult = result;
        }
        Interlocked.Increment(ref _acceptedCount);
    }

    private byte[] Reject(string error, string message)
    {
        Interlocked.Increment(ref _rejectedCount);
        lock (_statusGate) _lastResult = message;
        return JsonSerializer.SerializeToUtf8Bytes(new { ok = false, error, message });
    }

    private bool TokenMatches(string supplied)
    {
        var expected = Encoding.UTF8.GetBytes(_settings.Current.BrowserBridgeToken);
        var actual = Encoding.UTF8.GetBytes(supplied);
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private string CreateChallengeProof(string nonce) => Convert.ToBase64String(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(_settings.Current.BrowserBridgeToken), Encoding.UTF8.GetBytes(nonce)))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool IsValidNonce(string value) => value.Length is >= 16 and <= 64 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string? NormalizeBrowser(string? value)
    {
        if (value?.Equals("Chrome", StringComparison.OrdinalIgnoreCase) == true) return "Chrome";
        if (value?.Equals("Edge", StringComparison.OrdinalIgnoreCase) == true) return "Edge";
        if (value?.Equals("Brave", StringComparison.OrdinalIgnoreCase) == true) return "Brave";
        return null;
    }

    private static string? NormalizeText(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }

    private static DateTimeOffset ResolveObservedAt(long? milliseconds, DateTimeOffset receivedAt)
    {
        if (milliseconds is null) return receivedAt;
        try
        {
            var candidate = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds.Value);
            return (candidate - receivedAt).Duration() <= TimeSpan.FromMinutes(10) ? candidate : receivedAt;
        }
        catch (ArgumentOutOfRangeException) { return receivedAt; }
    }

    private static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var first = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (first == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1, 3), cancellationToken).ConfigureAwait(false);
        var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header));
        if (length <= 0 || length > MaximumMessageBytes) throw new InvalidDataException("Native message is too large.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, checked((uint)payload.Length));
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation?.Cancel();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        var clients = _activeClients.Values.ToArray();
        if (clients.Length > 0)
        {
            try { await Task.WhenAll(clients).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _cancellation?.Dispose();
        _cancellation = null;
        _acceptLoop = null;
        Status = "停止中";
    }

    private sealed record ActiveTabPayload(string Browser, string? Url, string? Title, bool Incognito,
        bool Active = true, string? CreatorName = null, long? ObservedAtUnixMs = null, string? ExtensionVersion = null);
}
