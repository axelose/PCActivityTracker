using System.Diagnostics;
using CoreRegression;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var suites = new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase)
        {
            ["memory"] = MemoryTests.Run, ["browser"] = BrowserStateTests.Run,
            ["aggregation"] = CreatorAggregationTests.Run, ["settings"] = SettingsTests.Run,
            ["ui"] = UiTests.Run, ["migration"] = MigrationTests.Run
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
