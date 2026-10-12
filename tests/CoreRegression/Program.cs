using System.Diagnostics;
using CoreRegression;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Explicit opt-in only; default/CI suites never connect to Ollama.
        if (args.Length == 2 && args[0] == "--ollama-smoke")
        {
            try { AiReportTests.RunOllamaSmoke(args[1]); return 0; }
            catch (Exception exception) { Console.Error.WriteLine("FAIL ollama-smoke: " + exception.Message); return 1; }
        }
        var suites = new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase)
        {
            ["memory"] = MemoryTests.Run, ["browser"] = BrowserStateTests.Run,
            ["aggregation"] = CreatorAggregationTests.Run, ["settings"] = SettingsTests.Run,
            ["ui"] = UiTests.Run, ["migration"] = MigrationTests.Run,
            ["ai"] = AiReportTests.Run, ["categorization"] = CategorizationTests.Run,
            ["explorer"] = ExplorerToolsTests.Run
        };
        var requested = args.Length == 0 ? suites.Keys.ToArray() : args;
        var failures = 0;
        foreach (var name in requested)
        {
            var timer = Stopwatch.StartNew();
            try
            {
                if (!suites.TryGetValue(name, out var run)) throw new ArgumentException("Unknown suite: " + name);
                run();
                Console.WriteLine($"PASS {name} ({timer.ElapsedMilliseconds} ms)");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}: {exception}");
            }
        }
        return failures == 0 ? 0 : 1;
    }
}
