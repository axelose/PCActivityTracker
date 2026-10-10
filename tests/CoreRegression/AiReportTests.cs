using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using LocalAi.Runtime;
using PCActivityTracker.Browser;
using PCActivityTracker.Data;
using PCActivityTracker.Domain;
using PCActivityTracker.Services;
using PCActivityTracker.Tracking;

namespace CoreRegression;

internal static class AiReportTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();

    internal static void RunOllamaSmoke(string model) => RunOllamaSmokeAsync(model).GetAwaiter().GetResult();

    private static async Task RunOllamaSmokeAsync(string model)
    {
        // Optional real-model check: synthetic data in a unique temporary DB.
        // No existing settings/history, startup registration or tracker loop.
        var provider = new ForegroundActivityProvider(new IdleDetector(), new BrowserStateStore());
        var games = GameSafetyService.Parse(AppSettings.DefaultGameProcessNames + ",deadlock,LobotomyCorp");
        bool SafeForeground()
        {
            var name = provider.Capture(int.MaxValue).ProcessName;
            return !string.IsNullOrWhiteSpace(name) && !games.Contains(name);
        }
        if (!SafeForeground()) throw new InvalidOperationException("Game/unknown foreground; real-model test skipped.");
        using var fixture = new Fixture();
        var start = new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);
        for (var day = 0; day < 7; day++)
            fixture.Activities.Save(new ActivitySession
            {
                StartTime = start.AddDays(day).AddHours(9), EndTime = start.AddDays(day).AddHours(10),
                ActivityType = ActivityType.Application, AppName = "Synthetic Editor", ProcessName = "synthetic"
            });
        var reports = new AiReportRepository(fixture.Database);
        var service = new AiReportService(new AggregationService(fixture.Activities), fixture.Activities, reports,
            new GpuExecutionCoordinator()); // Same cross-app execution lock as production.
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var guard = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(1000, cancellation.Token);
                if (!SafeForeground()) { cancellation.Cancel(); return; }
            }
        });
        try
        {
            var result = await service.GenerateAsync(AiReportType.Weekly, start, start.AddDays(7),
                "http://127.0.0.1:11434", model, true, true, 2, cancellation.Token);
            Check.That(result.Quality.IsValid && reports.Get(AiReportType.Weekly, start) is not null,
                "Real-model output failed save validation.");
            Console.WriteLine($"PASS ollama-smoke: attempts={result.Attempts}; outputTokens={result.OutputTokens}; unloaded={result.ModelUnloaded}");
            Console.WriteLine(result.Report.Content);
        }
        finally
        {
            cancellation.Cancel();
            try { await guard; } catch (OperationCanceledException) { }
        }
    }

    private static string Json(AiReportType type, string suggestion = "必要であれば、時間配分を確認できます。") =>
        JsonSerializer.Serialize(AiReportFormat.Sections(type).ToDictionary(section => section.Key,
            section => section.Key == "suggestions" ? suggestion : "観測した集計値を記載します。データがない項目はデータなしです。"));

    private static OllamaGenerateResult Result(string text, string? reason = "stop") =>
        new(text, "synthetic-model", 100_000_000, 10, 20, true, reason);

    private static void Reject(Action action)
    {
        try { action(); }
        catch (AiReportQualityException) { return; }
        throw new InvalidOperationException("Malformed AI output was accepted.");
    }

    private static async Task RunAsync()
    {
        foreach (var type in new[] { AiReportType.Daily, AiReportType.Weekly })
        {
            var json = Json(type);
            var rendered = AiReportFormat.Render(json, type);
            Check.That(AiReportQualityValidator.Validate(rendered, type).IsValid, "Valid structured report rejected.");
            Check.That(rendered.Contains("## 要約"), "Summary heading absent.");
            Check.That(AiReportQualityValidator.Validate(rendered + " https://example.test/private", type).IsValid == false,
                "URL safety check was weakened.");
            var fields = JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;
            fields.Remove("summary");
            Reject(() => AiReportFormat.Render(JsonSerializer.Serialize(fields), type));
            fields["summary"] = new string('あ', 141);
            Reject(() => AiReportFormat.Render(JsonSerializer.Serialize(fields), type));
            fields["summary"] = " ";
            Reject(() => AiReportFormat.Render(JsonSerializer.Serialize(fields), type));
            fields["summary"] = "要約";
            fields["unexpected"] = "no";
            Reject(() => AiReportFormat.Render(JsonSerializer.Serialize(fields), type));
            Reject(() => AiReportFormat.Render(json[..^1], type));
            Reject(() => AiReportFormat.Render(json[..^1] + ",\"summary\":\"duplicate\"}", type));
            Reject(() => AiReportFormat.Render("[]", type));
            var emptyData = JsonSerializer.Serialize(AiReportFormat.Sections(type).ToDictionary(section => section.Key, _ => "データなし"));
            Check.That(AiReportQualityValidator.Validate(AiReportFormat.Render(emptyData, type), type).IsValid,
                "Short but complete no-data report rejected.");
        }

        using var fixture = new Fixture();
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddDays(7);
        fixture.Activities.Save(new ActivitySession
        {
            StartTime = start, EndTime = start.AddHours(1), ActivityType = ActivityType.Website,
            AppName = "Synthetic Browser", ProcessName = "synthetic", Domain = "example.test",
            Url = "https://example.test/private-path", WindowTitle = "PRIVATE-WINDOW-TITLE",
            PageTitle = "PRIVATE-PAGE-TITLE"
        });
        var aggregation = new AggregationService(fixture.Activities);
        var reports = new AiReportRepository(fixture.Database);
        var coordinator = new GpuExecutionCoordinator(fixture.DirectoryPath);
        var service = new AiReportService(aggregation, fixture.Activities, reports, coordinator);
        var requests = new List<OllamaGenerateRequest>();
        var tokens = new List<CancellationToken>();
        var invalid = Json(AiReportType.Weekly, "PRIVATE-REJECTED-TEXT: 時間配分を修正する必要がある。");
        async Task<AiReportGenerationResult> Generate(Func<OllamaGenerateRequest, CancellationToken, Task<OllamaGenerateResult>> generate,
            bool unload = true, bool requireIdle = true, CancellationToken cancellation = default) =>
            await service.GenerateWithClientAsync(AiReportType.Weekly, start, end, "synthetic-model", unload,
                requireIdle, 1, generate, cancellation);

        var result = await Generate((request, token) =>
        {
            requests.Add(request); tokens.Add(token);
            return Task.FromResult(Result(requests.Count == 1 ? invalid : Json(AiReportType.Weekly)));
        });
        Check.That(requests.Count == 2 && result.Attempts == 2 && result.PromptTokens == 20 && result.OutputTokens == 40,
            "Bounded repair or total token accounting failed.");
        Check.That(tokens[0] == tokens[1] && requests.All(request => request.RequireIdleRuntime && request.UnloadAfterCompletion),
            "Repair changed the shared deadline or bypassed runtime safety.");
        Check.That(result.Report.PromptVersion == 4 && result.Report.Content.Contains("## 要約") && result.Quality.IsValid,
            "Safe rendered report was not saved.");
        foreach (var request in requests)
        {
            Check.That(request.ResponseFormat?.ValueKind == JsonValueKind.Object, "JSON schema missing.");
            Check.That(request.Prompt.Contains("summary") && request.Prompt.Contains("する必要がある"),
                "Prompt and validation contract disagree.");
            Check.That(!request.Prompt.Contains("private-path") && !request.Prompt.Contains("PRIVATE-WINDOW-TITLE") &&
                !request.Prompt.Contains("PRIVATE-PAGE-TITLE") && !request.Prompt.Contains("PRIVATE-REJECTED-TEXT"),
                "Private or rejected content leaked into generation/repair prompt.");
        }
        var preserved = reports.Get(AiReportType.Weekly, start)!;
        var calls = 0;
        try
        {
            await Generate((_, _) => { calls++; return Task.FromResult(Result(Json(AiReportType.Weekly, "https://example.test/private"))); });
            throw new InvalidOperationException("Unsafe output saved.");
        }
        catch (AiReportQualityException) { }
        Check.That(calls == 2 && reports.Get(AiReportType.Weekly, start) == preserved,
            "Failed generation retried indefinitely or overwrote existing report.");

        calls = 0;
        try
        {
            await Generate((_, _) => { calls++; return Task.FromResult(Result(invalid)); }, unload: false);
            throw new InvalidOperationException("Retained-runtime invalid output saved.");
        }
        catch (AiReportQualityException exception) { Check.That(exception.Message.Contains("モデルを解放"), "Retry guidance missing."); }
        Check.That(calls == 1, "Retained model retry bypassed idle-runtime safeguard.");

        calls = 0;
        var truncated = await Generate((_, _) => Task.FromResult(Result(Json(AiReportType.Weekly), ++calls == 1 ? "length" : "stop")));
        Check.That(truncated.Attempts == 2, "Token-limit termination went unnoticed.");
        calls = 0;
        var malformed = await Generate((_, _) => Task.FromResult(Result(++calls == 1 ? "# Markdown without summary" : Json(AiReportType.Weekly))));
        Check.That(malformed.Attempts == 2, "Malformed output was not retried.");

        using var cancelled = new CancellationTokenSource();
        var beforeCancellation = reports.Get(AiReportType.Weekly, start);
        calls = 0;
        try
        {
            await Generate((_, _) => { calls++; cancelled.Cancel(); return Task.FromResult(Result(invalid)); }, cancellation: cancelled.Token);
            throw new InvalidOperationException("Cancellation ignored.");
        }
        catch (OperationCanceledException) { }
        Check.That(calls == 1 && reports.Get(AiReportType.Weekly, start) == beforeCancellation, "Cancellation started repair or saved output.");
        calls = 0;
        try
        {
            await Generate((_, _) => { calls++; return Task.FromResult(Result(Json(AiReportType.Weekly))); }, cancellation: cancelled.Token);
            throw new InvalidOperationException("Pre-cancellation ignored.");
        }
        catch (OperationCanceledException) { }
        Check.That(calls == 0, "Pre-cancellation still started inference.");

        using var handler = new RequestProbeHandler();
        using var http = new HttpClient(handler);
        using var client = new OllamaClient(http, coordinator, new Uri("http://127.0.0.1:11434"));
        var wireResult = await client.GenerateAsync(requests[0] with { RequireIdleRuntime = false }, CancellationToken.None);
        using var wire = JsonDocument.Parse(handler.Body!);
        Check.That(wire.RootElement.GetProperty("format").ValueKind == JsonValueKind.Object &&
            wire.RootElement.GetProperty("format").GetProperty("required").EnumerateArray().Any(item => item.GetString() == "summary"),
            "Wire format contains an encoded schema string instead of an object.");
        Check.That(wire.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32() == 1800 &&
            wire.RootElement.GetProperty("keep_alive").GetInt32() == 0 && wireResult.DoneReason == "length", "Wire options or stop reason lost.");
        await client.GenerateAsync(requests[0] with { ResponseFormat = null, RequireIdleRuntime = false }, CancellationToken.None);
        using var plain = JsonDocument.Parse(handler.Body!);
        Check.That(!plain.RootElement.TryGetProperty("format", out _), "Non-report client requests acquired an unwanted schema.");
    }

    private sealed class RequestProbeHandler : HttpMessageHandler
    {
        internal string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check.That(request.RequestUri?.IsLoopback == true && request.RequestUri.AbsolutePath == "/api/generate", "Unexpected API target.");
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                { model = "synthetic-model", response = "{}", done_reason = "length", total_duration = 100, prompt_eval_count = 1, eval_count = 1 }),
                    Encoding.UTF8, "application/json")
            };
        }
    }
}
