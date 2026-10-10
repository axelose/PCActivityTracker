using System.Net.Http.Json;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAi.Runtime;

public sealed class OllamaClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly GpuExecutionCoordinator _coordinator;
    private readonly Uri _endpoint;
    private readonly bool _ownsHttpClient;

    public OllamaClient(string endpoint, GpuExecutionCoordinator coordinator)
        : this(CreateLocalHttpClient(), coordinator, ParseLocalEndpoint(endpoint), ownsHttpClient: true) { }

    internal OllamaClient(HttpClient httpClient, GpuExecutionCoordinator coordinator, Uri endpoint)
        : this(httpClient, coordinator, ValidateLocalEndpoint(endpoint), ownsHttpClient: false) { }

    private OllamaClient(HttpClient httpClient, GpuExecutionCoordinator coordinator, Uri endpoint, bool ownsHttpClient)
    {
        _httpClient = httpClient;
        _coordinator = coordinator;
        _endpoint = endpoint;
        _ownsHttpClient = ownsHttpClient;
    }

    public Uri Endpoint => _endpoint;

    public async Task<OllamaStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var version = await GetAsync<VersionResponse>("api/version", timeout.Token).ConfigureAwait(false);
        var tags = await GetAsync<TagsResponse>("api/tags", timeout.Token).ConfigureAwait(false);
        var runtime = await GetRuntimeStatusCoreAsync(timeout.Token).ConfigureAwait(false);
        var models = (tags.Models ?? Array.Empty<ModelResponse>())
            .Select(item => new OllamaModel(item.Model, item.Size, item.Details?.ParameterSize, item.Details?.QuantizationLevel))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new OllamaStatus(version.Version, models, runtime.Models);
    }

    public async Task<OllamaRuntimeStatus> GetRuntimeStatusAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        return await GetRuntimeStatusCoreAsync(timeout.Token).ConfigureAwait(false);
    }

    public async Task<OllamaGenerateResult> GenerateAsync(OllamaGenerateRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Ollama model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.", nameof(request));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);
        await using var lease = await _coordinator.AcquireAsync(timeout.Token).ConfigureAwait(false);
        if (request.RequireIdleRuntime)
        {
            var runtime = await GetRuntimeStatusCoreAsync(timeout.Token).ConfigureAwait(false);
            if (runtime.Models.Count > 0)
                throw new OllamaBusyException(runtime.Models);
        }
        var body = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["system"] = request.SystemPrompt,
            ["stream"] = false,
            ["think"] = false,
            ["keep_alive"] = request.UnloadAfterCompletion ? (object)0 : "5m",
            ["options"] = new
            {
                temperature = request.Temperature,
                num_ctx = request.ContextLength,
                num_predict = request.MaximumOutputTokens
            }
        };
        if (request.ResponseFormat is { } format) body["format"] = format;
        using var response = await _httpClient.PostAsJsonAsync(BuildUri("api/generate"), body, timeout.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            throw new OllamaException($"Ollama returned HTTP {(int)response.StatusCode}: {Limit(error, 500)}");
        }
        var result = await response.Content.ReadFromJsonAsync<GenerateResponse>(cancellationToken: timeout.Token)
            .ConfigureAwait(false) ?? throw new OllamaException("Ollama returned an empty response.");
        if (string.IsNullOrWhiteSpace(result.Response)) throw new OllamaException("Ollama generated no report text.");
        return new OllamaGenerateResult(result.Response.Trim(), result.Model, result.TotalDuration,
            result.PromptEvalCount, result.EvalCount, request.UnloadAfterCompletion, result.DoneReason);
    }

    public static Uri ParseLocalEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            throw new ArgumentException("Ollama endpoint is not a valid absolute URL.", nameof(endpoint));
        return ValidateLocalEndpoint(uri);
    }

    private static Uri ValidateLocalEndpoint(Uri endpoint)
    {
        var localHost = endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                        endpoint.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                        endpoint.Host.Equals("::1", StringComparison.OrdinalIgnoreCase) ||
                        endpoint.Host.Equals("[::1]", StringComparison.OrdinalIgnoreCase);
        if (endpoint.Scheme != Uri.UriSchemeHttp || !localHost || !string.IsNullOrEmpty(endpoint.UserInfo))
            throw new ArgumentException("For privacy, the Ollama endpoint must be a local HTTP address.", nameof(endpoint));
        return new UriBuilder(endpoint) { Path = "/", Query = string.Empty, Fragment = string.Empty }.Uri;
    }

    private async Task<T> GetAsync<T>(string relativePath, CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<T>(BuildUri(relativePath), cancellationToken)
                .ConfigureAwait(false) ?? throw new OllamaException("Ollama returned an empty response.");
        }
        catch (HttpRequestException exception)
        {
            throw new OllamaException("Could not connect to local Ollama. Start Ollama and try again.", exception);
        }
    }

    private async Task<OllamaRuntimeStatus> GetRuntimeStatusCoreAsync(CancellationToken cancellationToken)
    {
        var response = await GetAsync<RunningModelsResponse>("api/ps", cancellationToken).ConfigureAwait(false);
        var models = (response.Models ?? Array.Empty<RunningModelResponse>()).Select(item => new OllamaRunningModel(
                item.Model, item.Size, item.SizeVram, item.ExpiresAt, item.ContextLength))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new OllamaRuntimeStatus(models);
    }

    private Uri BuildUri(string relativePath) => new(_endpoint, relativePath);

    private static HttpClient CreateLocalHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 2,
            ConnectCallback = ConnectLoopbackAsync
        };
        return new HttpClient(handler, disposeHandler: true);
    }

    private static async ValueTask<Stream> ConnectLoopbackAsync(SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var addresses = IPAddress.TryParse(context.DnsEndPoint.Host, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
        var loopbackAddresses = addresses.Where(IPAddress.IsLoopback).ToArray();
        if (loopbackAddresses.Length == 0)
            throw new HttpRequestException("The Ollama host did not resolve to a loopback address.");

        Exception? lastError = null;
        foreach (var address in loopbackAddresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken)
                    .ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                lastError = exception;
                if (exception is OperationCanceledException) throw;
            }
        }
        throw new HttpRequestException("Could not connect to local Ollama.", lastError);
    }

    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
    public void Dispose() { if (_ownsHttpClient) _httpClient.Dispose(); }

    private sealed record VersionResponse([property: JsonPropertyName("version")] string Version);
    private sealed record TagsResponse([property: JsonPropertyName("models")] IReadOnlyList<ModelResponse>? Models);
    private sealed record ModelResponse(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("details")] ModelDetailsResponse? Details);
    private sealed record ModelDetailsResponse(
        [property: JsonPropertyName("parameter_size")] string? ParameterSize,
        [property: JsonPropertyName("quantization_level")] string? QuantizationLevel);
    private sealed record GenerateResponse(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("response")] string Response,
        [property: JsonPropertyName("total_duration")] long TotalDuration,
        [property: JsonPropertyName("prompt_eval_count")] int PromptEvalCount,
        [property: JsonPropertyName("eval_count")] int EvalCount,
        [property: JsonPropertyName("done_reason")] string? DoneReason);
    private sealed record RunningModelsResponse(
        [property: JsonPropertyName("models")] IReadOnlyList<RunningModelResponse>? Models);
    private sealed record RunningModelResponse(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("size_vram")] long SizeVram,
        [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
        [property: JsonPropertyName("context_length")] long ContextLength);
}

public sealed record OllamaGenerateRequest(
    string Model, string Prompt, string SystemPrompt, TimeSpan Timeout,
    bool UnloadAfterCompletion = true, double Temperature = 0.2,
    int ContextLength = 4096, int MaximumOutputTokens = 1200,
    bool RequireIdleRuntime = false, JsonElement? ResponseFormat = null);

public sealed record OllamaGenerateResult(
    string Text, string Model, long TotalDurationNanoseconds,
    int PromptTokens, int OutputTokens, bool ModelUnloaded, string? DoneReason = null);

public sealed record OllamaStatus(string Version, IReadOnlyList<OllamaModel> Models,
    IReadOnlyList<OllamaRunningModel> RunningModels);
public sealed record OllamaModel(string Name, long SizeBytes, string? ParameterSize, string? QuantizationLevel);
public sealed record OllamaRuntimeStatus(IReadOnlyList<OllamaRunningModel> Models);
public sealed record OllamaRunningModel(string Name, long SizeBytes, long SizeVramBytes,
    DateTimeOffset ExpiresAt, long ContextLength);

public class OllamaException : Exception
{
    public OllamaException(string message) : base(message) { }
    public OllamaException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class OllamaBusyException : OllamaException
{
    public OllamaBusyException(IReadOnlyList<OllamaRunningModel> models)
        : base($"Ollama already has {models.Count} running model(s): {string.Join(", ", models.Select(model => model.Name))}")
    {
        Models = models;
    }

    public IReadOnlyList<OllamaRunningModel> Models { get; }
}
