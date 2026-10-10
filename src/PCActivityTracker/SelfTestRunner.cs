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

internal static partial class SelfTestRunner
{
    public static int Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task<int> RunAsync()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PCActivityTracker-SelfTest-" + Guid.NewGuid().ToString("N"));
        var defaultDataDirectory = DataPathService.GetDefaultDataDirectory();
        if (!System.IO.Path.IsPathFullyQualified(defaultDataDirectory) ||
            !defaultDataDirectory.EndsWith("PCActivityTracker", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The default data directory was not a stable per-user path.");
        if (!Version.TryParse(BrowserNativeMessagingServer.ExpectedExtensionVersion, out _))
            throw new InvalidOperationException("The expected browser-extension version is invalid.");
        if (!NativeMessagingRegistrationService.SupportedBrowserRegistryKeys.Any(key =>
                key.Contains(@"BraveSoftware\Brave-Browser\NativeMessagingHosts", StringComparison.Ordinal)))
            throw new InvalidOperationException("Brave Native Messaging registration is missing.");
        System.IO.Directory.CreateDirectory(directory);
        void Stage(string name) => File.AppendAllText(System.IO.Path.Combine(directory, "selftest-progress.log"),
            $"{DateTimeOffset.Now:O} {name}{Environment.NewLine}");
        Stage("START");
        DiagnosticLogService.Initialize(directory);
        var databasePath = System.IO.Path.Combine(directory, "test.db");
        try
        {
            PerformanceModeService.ApplyLowImpactMode();
            using (var process = System.Diagnostics.Process.GetCurrentProcess())
            {
                if (process.PriorityClass != System.Diagnostics.ProcessPriorityClass.BelowNormal)
                    throw new InvalidOperationException("Low-impact process priority was not applied.");
            }

            using var database = new SqliteDatabase(databasePath);
            database.Initialize();
            // Report native engine versions separately: NuGet advisory coverage
            // does not establish the patch status of bundled native libraries.
            var sqliteVersion = database.Query("SELECT sqlite_version() AS version;").First().String("version");
            var cipherVersion = database.Query("PRAGMA cipher_version;").First().String("cipher_version");
            Console.WriteLine($"NATIVE DATABASE: SQLite {sqliteVersion}; SQLCipher {cipherVersion}");
            if (sqliteVersion != "3.53.4" || cipherVersion?.StartsWith("4.19.0 ", StringComparison.Ordinal) != true)
                throw new InvalidOperationException("The expected native SQLCipher engine was not loaded.");
            if (database.UserVersion != SqliteDatabase.CurrentSchemaVersion)
                throw new InvalidOperationException("A new database did not receive the current schema version.");
            var databaseHeader = new byte[16];
            using (var headerStream = File.Open(databasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                headerStream.ReadExactly(databaseHeader);
            var databaseKeyPath = DatabaseKeyService.KeyPathFor(databasePath);
            if (!database.IsEncrypted || Encoding.ASCII.GetString(databaseHeader) == "SQLite format 3\0" ||
                !File.Exists(databaseKeyPath) ||
                !File.ReadAllText(databaseKeyPath).StartsWith("dpapi:database:v1:", StringComparison.Ordinal))
                throw new InvalidOperationException("The activity database was not encrypted at rest with a DPAPI-protected key.");

            var legacyPath = System.IO.Path.Combine(directory, "legacy.db");
            using (var legacy = new SqliteDatabase(legacyPath, createMigrationBackup: false, encryptionEnabled: false))
            {
                legacy.ExecuteScript("""
                    CREATE TABLE activity_sessions (
                        id INTEGER PRIMARY KEY AUTOINCREMENT, start_utc_ms INTEGER NOT NULL, end_utc_ms INTEGER NOT NULL,
                        duration_seconds INTEGER NOT NULL, activity_type TEXT NOT NULL, app_name TEXT NOT NULL,
                        process_name TEXT NOT NULL, window_title TEXT NULL, executable_name TEXT NULL,
                        browser_name TEXT NULL, url TEXT NULL, domain TEXT NULL, page_title TEXT NULL,
                        is_idle INTEGER NOT NULL DEFAULT 0, category TEXT NOT NULL DEFAULT 'Other',
                        created_utc_ms INTEGER NOT NULL, updated_utc_ms INTEGER NOT NULL
                    );
                    INSERT INTO activity_sessions(start_utc_ms,end_utc_ms,duration_seconds,activity_type,app_name,process_name,
                        is_idle,category,created_utc_ms,updated_utc_ms)
                    VALUES(1,2,1,'Application','Legacy App','legacy',0,'Other',1,1);
                    PRAGMA user_version=1;
                    """);
            }
            using (var migrated = new SqliteDatabase(legacyPath))
            {
                migrated.Initialize();
                if (migrated.UserVersion != SqliteDatabase.CurrentSchemaVersion ||
                    !migrated.Query("PRAGMA table_info(activity_sessions);").Any(row => row.String("name") == "creator_name") ||
                    !migrated.Query("SELECT name FROM sqlite_master WHERE type='table' AND name='category_rules';").Any() ||
                    !migrated.Query("PRAGMA table_info(category_rules);").Any(row => row.String("name") == "match_mode") ||
                    !migrated.Query("SELECT name FROM sqlite_master WHERE type='table' AND name='activity_annotations';").Any() ||
                    migrated.Query("SELECT COUNT(*) AS count FROM activity_sessions;").Single().Int64("count") != 1)
                    throw new InvalidOperationException("A legacy database was not migrated without data loss.");
            }
            if (!Directory.EnumerateFiles(System.IO.Path.Combine(directory, "Backups"), "activity-pre-migration-v1-*.db").Any())
                throw new InvalidOperationException("A pre-migration safety backup was not created.");
            Stage("DATABASE_INITIALIZED");
            var repository = new ActivityRepository(database);
            var identityProbe = new ActivitySession
            {
                ActivityType = ActivityType.Website, AppName = "Google Chrome", ProcessName = "chrome",
                Domain = "youtube.com", CreatorName = "Channel A", Category = "Media"
            };
            if (!identityProbe.IsSameActivity(new ActivitySession
                {
                    ActivityType = ActivityType.Website, AppName = "Google Chrome", ProcessName = "chrome",
                    Domain = "youtube.com", CreatorName = "Channel A", Category = "Media"
                }) || identityProbe.IsSameActivity(new ActivitySession
                {
                    ActivityType = ActivityType.Website, AppName = "Google Chrome", ProcessName = "chrome",
                    Domain = "youtube.com", CreatorName = "Channel B", Category = "Media"
                }))
                throw new InvalidOperationException("Activity identity comparison did not detect a creator change.");

            var bulkProbe = new ViewModels.BulkObservableCollection<int>();
            var resetNotifications = 0;
            bulkProbe.CollectionChanged += (_, eventArgs) =>
            {
                if (eventArgs.Action == NotifyCollectionChangedAction.Reset) resetNotifications++;
            };
            bulkProbe.ReplaceWith(Enumerable.Range(0, 5000));
            if (bulkProbe.Count != 5000 || resetNotifications != 1)
                throw new InvalidOperationException("Bulk UI collection did not publish exactly one reset notification.");

            var day = new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);
            repository.Save(new ActivitySession
            {
                StartTime = day.AddHours(9), EndTime = day.AddHours(10), ActivityType = ActivityType.Application,
                AppName = "Visual Studio Code", ProcessName = "code", Category = "Development"
            });
            repository.Save(new ActivitySession
            {
                StartTime = day.AddHours(10), EndTime = day.AddMinutes(10 * 60 + 15), ActivityType = ActivityType.Website,
                AppName = "Google Chrome", ProcessName = "chrome", Domain = "github.com", Url = "https://github.com/",
                Category = "Development"
            });
            repository.Save(new ActivitySession
            {
                StartTime = day.AddMinutes(10 * 60 + 15), EndTime = day.AddMinutes(10 * 60 + 20), ActivityType = ActivityType.Idle,
                AppName = "Idle", IsIdle = true, Category = "Idle"
            });
            var report = new AggregationService(repository).BuildReport(day, day.AddDays(1));
            if (report.TotalPcSeconds != 4500 || report.IdleSeconds != 300 || report.Sites.FirstOrDefault()?.Name != "github.com")
                throw new InvalidOperationException("Aggregation result did not match the expected values.");
            if (new AggregationService(repository).BuildReport(day, day.AddDays(1), includeTimeline: false).Timeline.Count != 0)
                throw new InvalidOperationException("Non-timeline reports allocated timeline rows.");

            if (OllamaClient.ParseLocalEndpoint("http://127.0.0.1:11434").Host != "127.0.0.1")
                throw new InvalidOperationException("A valid local Ollama endpoint was rejected.");
            try
            {
                _ = OllamaClient.ParseLocalEndpoint("https://remote.example:11434");
                throw new InvalidOperationException("A remote Ollama endpoint was accepted.");
            }
            catch (ArgumentException) { }

            var redirectListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            redirectListener.Start(2);
            var redirectPort = ((System.Net.IPEndPoint)redirectListener.LocalEndpoint).Port;
            var redirectFollowed = false;
            var redirectServer = Task.Run(async () =>
            {
                using (var first = await redirectListener.AcceptTcpClientAsync().ConfigureAwait(false))
                {
                    var response = Encoding.ASCII.GetBytes($"HTTP/1.1 307 Temporary Redirect\r\nLocation: http://127.0.0.1:{redirectPort}/redirected\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await first.GetStream().WriteAsync(response).ConfigureAwait(false);
                }
                using var redirectWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
                try
                {
                    using var second = await redirectListener.AcceptTcpClientAsync(redirectWait.Token).ConfigureAwait(false);
                    redirectFollowed = true;
                }
                catch (OperationCanceledException) { }
            });
            try
            {
                using var redirectClient = new OllamaClient($"http://127.0.0.1:{redirectPort}",
                    new GpuExecutionCoordinator(System.IO.Path.Combine(directory, "redirect-gpu-lock")));
                try
                {
                    await redirectClient.GetRuntimeStatusAsync(CancellationToken.None).ConfigureAwait(false);
                    throw new InvalidOperationException("A redirect response was accepted as an Ollama response.");
                }
                catch (OllamaException) { }
                await redirectServer.ConfigureAwait(false);
                if (redirectFollowed)
                    throw new InvalidOperationException("The local Ollama client followed an HTTP redirect.");
            }
            finally
            {
                redirectListener.Stop();
            }

            if (!GameSafetyService.Parse("VALORANT-Win64-Shipping.exe; cs2\nCS2.EXE").SetEquals(
                    new[] { "VALORANT-Win64-Shipping", "cs2" }))
                throw new InvalidOperationException("Game-safe process names were not normalized or deduplicated.");
            if (ThemeService.ResolveEffectiveTheme("Dark") != "Dark" ||
                ThemeService.ResolveEffectiveTheme("Light") != "Light")
                throw new InvalidOperationException("Explicit theme selection was not resolved deterministically.");

            using (var busyHttp = new HttpClient(new OllamaBusyProbeHandler()))
            using (var busyClient = new OllamaClient(busyHttp,
                       new GpuExecutionCoordinator(System.IO.Path.Combine(directory, "ollama-busy-lock")),
                       new Uri("http://127.0.0.1:11434")))
            {
                try
                {
                    await busyClient.GenerateAsync(new OllamaGenerateRequest(
                        "self-test", "prompt", "system", TimeSpan.FromSeconds(3), RequireIdleRuntime: true),
                        CancellationToken.None).ConfigureAwait(false);
                    throw new InvalidOperationException("AI generation started while Ollama reported a loaded model.");
                }
                catch (OllamaBusyException exception) when (exception.Models.Count == 1) { }
            }

            var gpuLockDirectory = System.IO.Path.Combine(directory, "gpu-lock");
            var gpuCoordinator = new GpuExecutionCoordinator(gpuLockDirectory);
            var gpuLease = await gpuCoordinator.AcquireAsync(CancellationToken.None).ConfigureAwait(false);
            using (var blockedCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
            {
                try
                {
                    await new GpuExecutionCoordinator(gpuLockDirectory).AcquireAsync(blockedCancellation.Token).ConfigureAwait(false);
                    throw new InvalidOperationException("Concurrent GPU execution was not serialized.");
                }
                catch (OperationCanceledException) { }
            }
            await gpuLease.DisposeAsync().ConfigureAwait(false);
            var nextGpuLease = await new GpuExecutionCoordinator(gpuLockDirectory)
                .AcquireAsync(CancellationToken.None).ConfigureAwait(false);
            await nextGpuLease.DisposeAsync().ConfigureAwait(false);
            Stage("GPU_COORDINATOR_PASSED");

            var hourlyDay = day.AddDays(1);
            repository.Save(new ActivitySession
            {
                StartTime = hourlyDay.AddHours(9).AddMinutes(30), EndTime = hourlyDay.AddHours(10).AddMinutes(30),
                ActivityType = ActivityType.Application, AppName = "Hourly Split", ProcessName = "hourly", Category = "Test"
            });
            var hourly = repository.GetHourlyUsage(hourlyDay, hourlyDay.AddDays(1));
            if (hourly.Single(item => item.HourStart.Hour == 9).Seconds != 1800 ||
                hourly.Single(item => item.HourStart.Hour == 10).Seconds != 1800)
                throw new InvalidOperationException("An activity crossing an hour boundary was not split accurately.");

            var focus = AggregationService.CalculateFocus(new[]
            {
                new ActivityIntervalRow(day.AddHours(7), day.AddHours(7).AddMinutes(30), false),
                new ActivityIntervalRow(day.AddHours(7).AddMinutes(30), day.AddHours(7).AddMinutes(32), true),
                new ActivityIntervalRow(day.AddHours(8), day.AddHours(8).AddMinutes(20), false),
                new ActivityIntervalRow(day.AddHours(8).AddMinutes(23), day.AddHours(8).AddMinutes(35), false)
            }, day, day.AddDays(1));
            if (focus.TotalSeconds != 62 * 60 || focus.LongestSeconds != 32 * 60 || focus.BlockCount != 2)
                throw new InvalidOperationException("Focus blocks were not separated by idle and qualifying duration.");

            var settingsRepository = new SettingsRepository(database);
            var settingsService = new SettingsService(settingsRepository);
            var annotationRepository = new ActivityAnnotationRepository(database);
            settingsService.Load();
            if (settingsService.Current.RecordingEnabled || settingsService.Current.RecordUrls ||
                settingsService.Current.RecordPageTitles || settingsService.Current.RecordWindowTitles ||
                settingsService.Current.RecordCreatorNames || settingsService.Current.RetentionDays != 90)
                throw new InvalidOperationException("New profiles did not start with privacy-safe recording defaults.");
            var storedSecrets = settingsRepository.GetSettings();
            if (storedSecrets.ContainsKey(nameof(AppSettings.BrowserBridgeToken)) ||
                !storedSecrets.ContainsKey("BrowserBridgeTokenProtected"))
                throw new InvalidOperationException("The browser bridge token was not migrated to DPAPI-protected storage.");

            settingsService.AddCategoryRule(CategoryRuleTarget.Domain, "github.com", "Research");
            settingsService.AddCategoryRule(CategoryRuleTarget.Creator, "*learning*", "Education");
            if (settingsService.Categorize("Google Chrome", "docs.github.com", false) != "Research" ||
                settingsService.Categorize("Google Chrome", "youtube.com", false, "My Learning Channel") != "Education" ||
                settingsService.CategoryRules.Count != 2)
                throw new InvalidOperationException("Local category rules were not applied by target and priority.");
            var categorizedReport = new AggregationService(repository, settingsService)
                .BuildReport(day, day.AddDays(1), includeTimeline: false);
            if (categorizedReport.Categories.FirstOrDefault(item => item.Name == "Research")?.Seconds != 900)
                throw new InvalidOperationException("A category rule was not applied retroactively to report data.");
            var researchRuleId = settingsService.CategoryRules.Single(item => item.Category == "Research").Id;
            settingsService.RemoveCategoryRule(researchRuleId);
            var revertedReport = new AggregationService(repository, settingsService)
                .BuildReport(day, day.AddDays(1), includeTimeline: false);
            if (revertedReport.Categories.FirstOrDefault(item => item.Name == "Development")?.Seconds != 4500)
                throw new InvalidOperationException("Removing a category rule did not restore historical classification.");

            settingsService.AddCategoryRule(CategoryRuleTarget.Application, "Visual*", "Work > General",
                CategoryMatchMode.Wildcard, "#334155", 100);
            settingsService.AddCategoryRule(CategoryRuleTarget.Application, "^Visual Studio Code$", "Work/Development",
                CategoryMatchMode.Regex, "#2563EB", 900);
            var categoryExplanation = ActivityCategorizer.Evaluate("Visual Studio Code", null, false, null,
                settingsService.CategoryRules);
            if (categoryExplanation.Category != "Work > Development" || categoryExplanation.ConflictCount != 1 ||
                categoryExplanation.MatchedRule?.Color != "#2563EB")
                throw new InvalidOperationException("Hierarchical category priority, regex matching, or explanation failed.");

            var context = AggregationService.CalculateContext(new[]
            {
                new AnalysisActivity(day.AddHours(9), day.AddHours(9).AddMinutes(20), "app:Code", "Code", "Code", null, "Development", false),
                new AnalysisActivity(day.AddHours(9).AddMinutes(20), day.AddHours(9).AddMinutes(22), "web:mail.example", "mail.example", "Chrome", "mail.example", "Communication", true),
                new AnalysisActivity(day.AddHours(9).AddMinutes(22), day.AddHours(9).AddMinutes(42), "app:Code", "Code", "Code", null, "Development", false)
            });
            var quality = AggregationService.CalculateFocusQuality(context,
                new FocusSummary(40 * 60, 20 * 60, 1));
            if (context.SwitchCount != 2 || context.ShortSessionCount != 1 ||
                context.TopDestinations.Count != 2 || quality.Score is < 0 or > 100)
                throw new InvalidOperationException("Context switching or focus quality analysis produced invalid metrics.");
            var behavior = new AggregationService(repository, settingsService).BuildBehaviorAnalysis(day, day.AddDays(1));
            if (behavior.Comparison.CurrentSeconds != 4500 || behavior.Context.SwitchCount != 1)
                throw new InvalidOperationException("Behavior comparison did not use the selected period correctly.");
            var segmentedContext = AggregationService.CalculateContext(new[]
            {
                new AnalysisActivity(day, day.AddMinutes(20), "app:A", "A", "A", null, "Other", false, 0),
                new AnalysisActivity(day.AddMinutes(22), day.AddMinutes(42), "app:B", "B", "B", null, "Other", false, 1)
            });
            if (segmentedContext.SwitchCount != 0)
                throw new InvalidOperationException("An idle boundary was incorrectly counted as an app switch.");
            var futureBehavior = new AggregationService(repository, settingsService)
                .BuildBehaviorAnalysis(DateTimeOffset.Now.AddDays(1), DateTimeOffset.Now.AddDays(8));
            if (futureBehavior.Comparison.CurrentSeconds != 0 || futureBehavior.Comparison.PreviousSeconds != 0)
                throw new InvalidOperationException("A future analysis period produced a misleading previous-period comparison.");

            var deepDiveDay = day.AddDays(2);
            repository.Save(new ActivitySession
            {
                StartTime = deepDiveDay.AddHours(9), EndTime = deepDiveDay.AddHours(9).AddMinutes(20),
                ActivityType = ActivityType.Application, AppName = "Visual Studio Code", ProcessName = "code",
                WindowTitle = "alpha", Category = "Development"
            });
            repository.Save(new ActivitySession
            {
                StartTime = deepDiveDay.AddHours(9).AddMinutes(20), EndTime = deepDiveDay.AddHours(9).AddMinutes(23),
                ActivityType = ActivityType.Website, AppName = "Google Chrome", ProcessName = "chrome",
                Domain = "mail.example", Category = "Communication"
            });
            repository.Save(new ActivitySession
            {
                StartTime = deepDiveDay.AddHours(9).AddMinutes(23), EndTime = deepDiveDay.AddHours(9).AddMinutes(50),
                ActivityType = ActivityType.Application, AppName = "Visual Studio Code", ProcessName = "code",
                WindowTitle = "alpha", Category = "Development"
            });
            var deepBehavior = new AggregationService(repository, settingsService)
                .BuildBehaviorAnalysis(deepDiveDay, deepDiveDay.AddDays(1));
            if (deepBehavior.DeepDive.ReturnCount != 1 || deepBehavior.DeepDive.AverageRecoverySeconds != 180 ||
                deepBehavior.DeepDive.Interrupters.FirstOrDefault()?.Name != "mail.example" ||
                deepBehavior.DeepDive.FocusHours.Count == 0 || deepBehavior.DeepDive.ScoreComponents.Count == 0)
                throw new InvalidOperationException("Focus interruption, recovery, time-of-day, or score evidence analysis failed.");

            var annotation = new ActivityAnnotation
            {
                StartTime = deepDiveDay.AddHours(9).AddMinutes(5),
                EndTime = deepDiveDay.AddHours(9).AddMinutes(15),
                ProjectName = "Alpha", TaskName = "Design", Note = "Architecture review",
                CategoryOverride = "Work > Alpha"
            };
            annotationRepository.Save(annotation);
            var explorer = new ActivityExplorerService(repository, annotationRepository, settingsService);
            var projectSearch = explorer.Search(new ActivitySearchQuery(deepDiveDay, deepDiveDay.AddDays(1),
                ZoomMinutes: 5, Project: "Alpha", IncludeIdle: false));
            if (annotation.Id == 0 || projectSearch.Entries.Count != 2 || projectSearch.MatchingSeconds != 600 ||
                projectSearch.Entries.Any(item => item.Project != "Alpha" || item.Category != "Work > Alpha" ||
                    !item.CategoryReason.Contains("手動補正", StringComparison.Ordinal)))
                throw new InvalidOperationException("Project annotations, non-destructive category overrides, or timeline zoom failed.");
            var annotatedReport = new AggregationService(repository, settingsService, annotationRepository)
                .BuildReport(deepDiveDay, deepDiveDay.AddDays(1));
            if (!annotatedReport.Timeline.Any(item => item.Subtitle.Contains("Alpha", StringComparison.Ordinal)) ||
                annotatedReport.Categories.Single(item => item.Name == "Work > Alpha").Seconds != 600)
                throw new InvalidOperationException("Project annotations were not visible or precisely aggregated on the normal timeline.");
            var rulePreview = explorer.PreviewRule(new CategoryRule
            {
                Target = CategoryRuleTarget.Domain, Pattern = "mail.example", Category = "Work > Communication",
                MatchMode = CategoryMatchMode.Wildcard, Priority = 500
            }, deepDiveDay, deepDiveDay.AddDays(1));
            if (rulePreview.SessionCount != 1 || rulePreview.Seconds != 180)
                throw new InvalidOperationException("Category rule impact preview did not match stored history.");
            using var sessionLock = new SessionLockMonitor();
            var tracker = new ActivityTrackerService(
                new ForegroundActivityProvider(new IdleDetector(), new BrowserStateStore()), repository, settingsService, sessionLock);
            var sampleSettings = settingsService.Current;
            sampleSettings.PrivacyConsentVersion = AppSettings.CurrentPrivacyConsentVersion;
            sampleSettings.RecordingEnabled = true;
            sampleSettings.RecordUrls = true;
            sampleSettings.RecordPageTitles = true;
            sampleSettings.RecordWindowTitles = true;
            sampleSettings.RecordCreatorNames = true;
            var sampleStart = day.AddHours(12);
            tracker.ProcessSnapshot(new ForegroundSnapshot(sampleStart, 1001, "Visual Studio Code", "code", "project-a", "Code.exe", false, null), sampleSettings);
            tracker.ProcessSnapshot(new ForegroundSnapshot(sampleStart.AddSeconds(10), 1001, "Visual Studio Code", "code", "project-a", "Code.exe", false, null), sampleSettings);
            tracker.ProcessSnapshot(new ForegroundSnapshot(sampleStart.AddHours(2), 1002, "Microsoft Word", "WINWORD", "document", "WINWORD.EXE", false, null), sampleSettings);
            tracker.ProcessSnapshot(new ForegroundSnapshot(sampleStart.AddHours(2).AddSeconds(10), (uint)Environment.ProcessId,
                "PC Activity Tracker", "PCActivityTracker", "Dashboard", "PCActivityTracker.exe", false, null), sampleSettings);
            var browserObservedAt = sampleStart.AddHours(2).AddSeconds(20);
            tracker.ProcessSnapshot(new ForegroundSnapshot(browserObservedAt.AddSeconds(-5), 1006, "Microsoft Edge", "msedge",
                "Private banking - InPrivate - Microsoft Edge", "msedge.exe", false, null), sampleSettings);
            var browserActivity = new BrowserActivity("Chrome", "https://user:password@example.com/private/path?token=secret#section",
                "example.com", "Example", "Example Channel", browserObservedAt, false);
            tracker.ProcessSnapshot(new ForegroundSnapshot(browserObservedAt, 1003, "Google Chrome", "chrome",
                "Example - Google Chrome", "chrome.exe", false, browserActivity), sampleSettings);
            tracker.ProcessSnapshot(new ForegroundSnapshot(browserObservedAt.AddSeconds(10), 1003, "Google Chrome", "chrome",
                "Example - Google Chrome", "chrome.exe", false, browserActivity with { ObservedAt = browserObservedAt.AddSeconds(10) }), sampleSettings);
            sessionLock.SetLockStateForTest(true);
            if (!tracker.DisplayStatus.Contains("ロック中", StringComparison.Ordinal))
                throw new InvalidOperationException("Windows session lock state was not reflected by the tracker.");
            tracker.ProcessSnapshot(new ForegroundSnapshot(browserObservedAt.AddSeconds(20), 1004, "Should Not Record", "locked-test",
                "Locked", "locked-test.exe", false, null), sampleSettings);
            if (tracker.CurrentForegroundProcessName.Length != 0)
                throw new InvalidOperationException("Foreground process state was retained while Windows was locked.");
            sessionLock.SetLockStateForTest(false);
            tracker.ProcessSnapshot(new ForegroundSnapshot(browserObservedAt.AddSeconds(30), 1005, "VALORANT",
                "VALORANT-Win64-Shipping", "VALORANT", "VALORANT-Win64-Shipping.exe", false, null), sampleSettings);
            var gameState = new GameSafetyService(tracker).Evaluate(sampleSettings);
            if (!gameState.IsGameForeground || gameState.ProcessName != "VALORANT-Win64-Shipping")
                throw new InvalidOperationException("Game-safe mode did not detect a configured foreground game.");
            tracker.SuspendAndDiscard();
            tracker.ProcessSnapshot(new ForegroundSnapshot(browserObservedAt.AddSeconds(40), 1007, "Suspended App",
                "suspended-test", "Must not persist", "suspended-test.exe", false, null), sampleSettings);
            await tracker.DisposeAsync().ConfigureAwait(false);
            Stage("TRACKER_PASSED");

            var tracked = repository.GetRange(sampleStart, sampleStart.AddHours(3));
            if (tracked.Single(item => item.ProcessName == "code").DurationSeconds != 20)
                throw new InvalidOperationException("A suspended sampling gap was charged to the previous application.");
            if (tracked.Any(item => item.ProcessName == "PCActivityTracker"))
                throw new InvalidOperationException("The tracker recorded its own foreground window.");
            if (tracked.Any(item => item.ProcessName == "locked-test"))
                throw new InvalidOperationException("The tracker recorded activity while Windows was locked.");
            if (tracked.Any(item => item.ProcessName == "suspended-test"))
                throw new InvalidOperationException("A sample captured during deletion suspension was persisted.");
            if (tracked.Single(item => item.ProcessName == "msedge").WindowTitle is not null)
                throw new InvalidOperationException("A browser window title was stored without extension-confirmed normal-tab state.");
            if (tracked.Single(item => item.Domain == "example.com").Url != "https://example.com/private/path")
                throw new InvalidOperationException("URL query or fragment data was not removed.");
            if (tracked.Single(item => item.Domain == "example.com").CreatorName != "Example Channel")
                throw new InvalidOperationException("The browser creator name was not stored.");
            var creatorReport = new AggregationService(repository).BuildReport(sampleStart, sampleStart.AddHours(3), includeTimeline: false);
            if (creatorReport.Creators.FirstOrDefault()?.Name != "Example Channel")
                throw new InvalidOperationException("Creator usage was not aggregated.");

            var reportAggregation = new AggregationService(repository);
            var aiAnalysis = reportAggregation.BuildReportAnalysis(sampleStart, sampleStart.AddHours(3));
            var aiPrompt = AiReportService.BuildPrompt(AiReportType.Daily, sampleStart, sampleStart.AddHours(3), aiAnalysis);
            if (!aiPrompt.Contains("example.com", StringComparison.Ordinal) ||
                aiPrompt.Contains("private/path", StringComparison.Ordinal) ||
                aiPrompt.Contains("project-a", StringComparison.Ordinal) ||
                aiPrompt.Contains("Example - Google Chrome", StringComparison.Ordinal))
                throw new InvalidOperationException("The AI prompt omitted aggregates or leaked raw activity details.");
            var aiReportRepository = new AiReportRepository(database);
            var storedAiReport = aiReportRepository.Save(AiReportType.Daily, sampleStart, sampleStart.AddHours(3),
                "self-test-model", "# Local AI report", 1);
            if (storedAiReport.Content != "# Local AI report" ||
                aiReportRepository.Get(AiReportType.Daily, sampleStart)?.ModelName != "self-test-model")
                throw new InvalidOperationException("A generated AI report was not persisted locally.");
            var validAiContent = """
                # 概要
                - PC利用時間を集計しました。
                # 主な時間配分
                - アプリ別の観測時間を表示しました。
                # 集中と中断
                - 集中ブロックを集計しました。
                # 次回への提案
                - 必要であれば時間配分を確認できます。
                # 要約
                観測された集計値のみを短くまとめました。
                """;
            if (!AiReportQualityValidator.Validate(validAiContent, AiReportType.Daily).IsValid ||
                AiReportQualityValidator.Validate(validAiContent + " https://example.com/private", AiReportType.Daily).IsValid)
                throw new InvalidOperationException("AI report quality validation accepted unsafe content or rejected a valid report.");
            var snapshotService = new AiReportService(reportAggregation, repository, aiReportRepository,
                new GpuExecutionCoordinator(System.IO.Path.Combine(directory, "snapshot-gpu-lock")));
            if (snapshotService.GetSnapshot(AiReportType.Daily, sampleStart, sampleStart.AddHours(3)).IsStale)
                throw new InvalidOperationException("A newly saved AI report was incorrectly marked stale.");
            await Task.Delay(10).ConfigureAwait(false);
            repository.Save(new ActivitySession
            {
                StartTime = sampleStart.AddHours(2).AddMinutes(30),
                EndTime = sampleStart.AddHours(2).AddMinutes(31),
                ActivityType = ActivityType.Application, AppName = "Fresh Activity", ProcessName = "fresh",
                Category = "Test"
            });
            if (!snapshotService.GetSnapshot(AiReportType.Daily, sampleStart, sampleStart.AddHours(3)).IsStale)
                throw new InvalidOperationException("Activity added after AI generation did not mark the report stale.");

            repository.Save(new ActivitySession
            {
                StartTime = day.AddHours(15), EndTime = day.AddHours(15).AddMinutes(1), ActivityType = ActivityType.Website,
                AppName = "Google Chrome", ProcessName = "chrome", Domain = "legacy.example",
                Url = "https://legacy-user:legacy-password@legacy.example/path?old=secret#fragment", Category = "Other"
            });
            repository.SanitizeStoredUrls();
            if (repository.GetRange(day.AddHours(15), day.AddHours(16)).Single().Url != "https://legacy.example/path")
                throw new InvalidOperationException("Existing URL data was not sanitized.");
            if (UrlPrivacyService.Sanitize("https://example.com/" + new string('a', 5000)) != "https://example.com")
                throw new InvalidOperationException("An excessive URL path was not reduced to its origin.");
            database.ExecuteScript("PRAGMA wal_checkpoint(PASSIVE);");
            if (FileContainsUtf8(databasePath, "legacy.example") || FileContainsUtf8(databasePath + "-wal", "legacy.example"))
                throw new InvalidOperationException("SQLCipher DB or WAL exposed a known activity value as plaintext.");

            var browserState = new BrowserStateStore();
            var orderingTime = DateTimeOffset.Now;
            browserState.Update(new BrowserActivity("Chrome", "https://new.example/", "new.example", "New Page",
                null, orderingTime.AddSeconds(2), false));
            browserState.Update(new BrowserActivity("Chrome", "https://old.example/", "old.example", "Old Page",
                null, orderingTime.AddSeconds(1), false));
            browserState.Clear("Chrome", orderingTime.AddMilliseconds(1500));
            if (browserState.GetFresh("chrome", "New Page - Google Chrome", orderingTime.AddSeconds(3))?.Domain != "new.example")
                throw new InvalidOperationException("A stale browser event replaced newer active-tab state.");
            browserState.Clear("Chrome", orderingTime.AddSeconds(4));
            if (browserState.GetFresh("chrome", "New Page - Google Chrome", orderingTime.AddSeconds(4)) is not null)
                throw new InvalidOperationException("A newer browser clear event was not applied.");

            var bridgeToken = settingsService.Current.BrowserBridgeToken;
            const string selfTestOrigin = "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            settingsService.PairBrowserOrigin(selfTestOrigin);
            var selfTestPipeName = "PCActivityTracker.Browser.Native.SelfTest." + Guid.NewGuid().ToString("N");
            await using var bridge = new BrowserNativeMessagingServer(browserState, settingsService, selfTestPipeName);
            byte[] Envelope(object message) => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                new { origin = selfTestOrigin, message });
            const string challengeNonce = "abcdefghijklmnopqrstuv";
            var challenge = bridge.ProcessEnvelope(Envelope(new { type = "challenge", nonce = challengeNonce }));
            using (var challengeJson = System.Text.Json.JsonDocument.Parse(challenge))
            {
                var actualProof = challengeJson.RootElement.GetProperty("proof").GetString();
                var expectedProof = Convert.ToBase64String(System.Security.Cryptography.HMACSHA256.HashData(
                    Encoding.UTF8.GetBytes(bridgeToken), Encoding.UTF8.GetBytes(challengeNonce)))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                if (actualProof != expectedProof) throw new InvalidOperationException("Native Messaging authentication proof was invalid.");

                bridge.Start();
                var nativeResponse = await RoundTripThroughNativeHostAsync(selfTestOrigin, selfTestPipeName, new
                {
                    type = "challenge",
                    nonce = challengeNonce
                }).ConfigureAwait(false);
                using var nativeJson = System.Text.Json.JsonDocument.Parse(nativeResponse);
                if (nativeJson.RootElement.GetProperty("proof").GetString() != expectedProof)
                    throw new InvalidOperationException("Native host stdio and current-user pipe round-trip returned an invalid proof.");
            }
            var accepted = bridge.ProcessEnvelope(Envelope(new { type = "activeTab", token = bridgeToken,
                payload = new { browser = "Brave", url = "https://user:password@github.com/openai?token=secret#fragment",
                    title = "GitHub", creatorName = "OpenAI", incognito = false,
                    extensionVersion = BrowserNativeMessagingServer.ExpectedExtensionVersion } }));
            var bridgedActivity = browserState.GetFresh("brave", "GitHub - Brave", DateTimeOffset.Now);
            if (!System.Text.Json.JsonDocument.Parse(accepted).RootElement.GetProperty("ok").GetBoolean() ||
                bridgedActivity?.Domain != "github.com" || bridgedActivity.CreatorName != "OpenAI" ||
                bridgedActivity.Url != "https://github.com/openai")
                throw new InvalidOperationException("Authenticated Native Messaging activity was not accepted.");
            var diagnostics = bridge.GetDiagnostics();
            if (diagnostics.AcceptedCount != 1 ||
                diagnostics.ExtensionVersion != BrowserNativeMessagingServer.ExpectedExtensionVersion ||
                diagnostics.LastBrowser != "Brave" || diagnostics.LastDomain != "github.com")
                throw new InvalidOperationException("Native Messaging diagnostics did not describe the accepted active tab.");

            var rejected = bridge.ProcessEnvelope(Envelope(new { type = "activeTab", token = "wrong-token", payload = new { } }));
            if (System.Text.Json.JsonDocument.Parse(rejected).RootElement.GetProperty("error").GetString() != "unauthorized" ||
                bridge.GetDiagnostics().RejectedCount != 1)
                throw new InvalidOperationException("Native Messaging rejection diagnostics were not updated.");

            var cleared = bridge.ProcessEnvelope(Envelope(new { type = "activeTab", token = bridgeToken,
                payload = new { browser = "Brave", url = (string?)null, title = "", incognito = false, active = false } }));
            if (!System.Text.Json.JsonDocument.Parse(cleared).RootElement.GetProperty("ok").GetBoolean() ||
                browserState.GetFresh("brave", "GitHub - Brave", DateTimeOffset.Now) is not null)
                throw new InvalidOperationException("A non-web Native Messaging event did not clear stale site activity.");

            DiagnosticLogService.Error("SelfTest.Redaction", new InvalidOperationException(
                "https://secret.example/private C:\\Users\\Private\\secret.txt token-super-secret"));
            var diagnosticLog = File.ReadAllText(DiagnosticLogService.Path);
            if (diagnosticLog.Contains("secret.example", StringComparison.OrdinalIgnoreCase) ||
                diagnosticLog.Contains("token-super-secret", StringComparison.Ordinal) ||
                diagnosticLog.Contains("secret.txt", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The diagnostic log exposed exception message data.");
            var diagnosticZip = Path.Combine(directory, "diagnostics.zip");
            new DiagnosticExportService(database, settingsService, bridge.GetDiagnostics).Export(diagnosticZip);
            using (var archive = System.IO.Compression.ZipFile.OpenRead(diagnosticZip))
            {
                var combined = string.Join('\n', archive.Entries.Select(entry =>
                {
                    using var reader = new StreamReader(entry.Open());
                    return reader.ReadToEnd();
                }));
                if (combined.Contains("secret.example", StringComparison.OrdinalIgnoreCase) ||
                    combined.Contains("token-super-secret", StringComparison.Ordinal) ||
                    combined.Contains("github.com", StringComparison.OrdinalIgnoreCase) ||
                    combined.Contains(bridgeToken, StringComparison.Ordinal) ||
                    combined.Contains(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The redacted diagnostic export contained private activity data.");
            }
            var previousBridgeToken = settingsService.Current.BrowserBridgeToken;
            var rotatedBridgeToken = settingsService.ResetBrowserPairing();
            if (rotatedBridgeToken == previousBridgeToken || settingsService.IsBrowserOriginPaired(selfTestOrigin))
                throw new InvalidOperationException("Resetting browser pairing did not rotate the token and revoke origins.");
            Stage("NATIVE_MESSAGING_AND_DIAGNOSTICS_PASSED");

            var retentionNow = day.AddDays(100);
            repository.Save(new ActivitySession
            {
                StartTime = retentionNow.AddDays(-31), EndTime = retentionNow.AddDays(-31).AddMinutes(1),
                ActivityType = ActivityType.Application, AppName = "Expired", ProcessName = "expired", Category = "Other"
            });
            repository.Save(new ActivitySession
            {
                StartTime = retentionNow.AddDays(-1), EndTime = retentionNow.AddDays(-1).AddMinutes(1),
                ActivityType = ActivityType.Application, AppName = "Retained", ProcessName = "retained", Category = "Other"
            });
            var retentionSettings = settingsService.Current;
            retentionSettings.RetentionDays = 30;
            retentionSettings.StartMinimized = true;
            retentionSettings.AutomaticBackupsEnabled = false;
            retentionSettings.OllamaEndpoint = "http://localhost:11434";
            retentionSettings.OllamaModel = "self-test-model";
            retentionSettings.UnloadOllamaAfterGeneration = false;
            retentionSettings.OllamaTimeoutMinutes = 7;
            retentionSettings.GameSafeModeEnabled = false;
            retentionSettings.RequireIdleOllama = false;
            retentionSettings.GameProcessNames = "VALORANT-Win64-Shipping.exe,cs2,CS2.EXE";
            retentionSettings.ThemeMode = "Dark";
            settingsService.Save(retentionSettings);
            var reloadedSettings = new SettingsService(new SettingsRepository(database));
            reloadedSettings.Load();
            if (reloadedSettings.Current.RetentionDays != 30 ||
                reloadedSettings.Current.RecordCreatorNames != retentionSettings.RecordCreatorNames ||
                !reloadedSettings.Current.StartMinimized || reloadedSettings.Current.AutomaticBackupsEnabled ||
                reloadedSettings.Current.OllamaEndpoint != "http://localhost:11434" ||
                reloadedSettings.Current.OllamaModel != "self-test-model" ||
                reloadedSettings.Current.UnloadOllamaAfterGeneration ||
                reloadedSettings.Current.OllamaTimeoutMinutes != 7 ||
                reloadedSettings.Current.GameSafeModeEnabled ||
                reloadedSettings.Current.RequireIdleOllama ||
                reloadedSettings.Current.GameProcessNames != "VALORANT-Win64-Shipping,cs2" ||
                reloadedSettings.Current.ThemeMode != "Dark")
                throw new InvalidOperationException("Atomic settings save did not persist a complete settings snapshot.");
            await using (var retentionBackups = new BackupService(database, settingsService))
            await using (var maintenance = new DataMaintenanceService(repository, settingsService, retentionBackups))
                maintenance.RunOnce(retentionNow);
            var retainedRows = repository.GetRange(retentionNow.AddDays(-32), retentionNow);
            if (retainedRows.Any(item => item.ProcessName == "expired") ||
                retainedRows.All(item => item.ProcessName != "retained"))
                throw new InvalidOperationException("Resident retention maintenance kept expired rows or removed current rows.");

            var backupTestDirectory = System.IO.Path.Combine(directory, "backup-test");
            System.IO.Directory.CreateDirectory(backupTestDirectory);
            var backupTestPath = System.IO.Path.Combine(backupTestDirectory, "activity.db");
            string createdBackupPath;
            using (var backupDatabase = new SqliteDatabase(backupTestPath))
            {
                backupDatabase.Initialize();
                var backupRepository = new ActivityRepository(backupDatabase);
                backupRepository.Save(new ActivitySession
                {
                    StartTime = day, EndTime = day.AddMinutes(1), ActivityType = ActivityType.Application,
                    AppName = "Before Backup", ProcessName = "before", Category = "Test"
                });
                var backupSettings = new SettingsService(new SettingsRepository(backupDatabase));
                backupSettings.Load();
                var backupService = new BackupService(backupDatabase, backupSettings);
                var createdBackup = backupService.CreateBackup();
                createdBackupPath = createdBackup.FullPath;
                var backupHeader = new byte[16];
                using (var backupHeaderStream = File.Open(createdBackupPath, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                    backupHeaderStream.ReadExactly(backupHeader);
                if (Encoding.ASCII.GetString(backupHeader) == "SQLite format 3\0")
                    throw new InvalidOperationException("A database backup was written as plaintext SQLite.");
                var missingKeyDirectory = Path.Combine(directory, "missing-key-test");
                var missingKeyBackupDirectory = Path.Combine(missingKeyDirectory, "Backups");
                Directory.CreateDirectory(missingKeyBackupDirectory);
                File.Copy(createdBackupPath, Path.Combine(missingKeyBackupDirectory, "activity-manual-test.db"));
                var rejectedMissingKey = false;
                try { using var _ = new SqliteDatabase(Path.Combine(missingKeyDirectory, "activity.db")); }
                catch (InvalidDataException) { rejectedMissingKey = true; }
                if (!rejectedMissingKey)
                    throw new InvalidOperationException("An encrypted backup without database.key was not rejected safely.");
                backupRepository.Save(new ActivitySession
                {
                    StartTime = day.AddMinutes(2), EndTime = day.AddMinutes(3), ActivityType = ActivityType.Application,
                    AppName = "After Backup", ProcessName = "after", Category = "Test"
                });
                backupService.StageRestore(createdBackupPath);
            }
            if (!BackupService.ApplyPendingRestore(backupTestDirectory))
                throw new InvalidOperationException("A staged database restore was not applied.");
            using (var restoredDatabase = new SqliteDatabase(backupTestPath))
            {
                restoredDatabase.Initialize();
                var restored = new ActivityRepository(restoredDatabase).GetRange(day, day.AddHours(1));
                if (restored.Count != 1 || restored[0].ProcessName != "before")
                    throw new InvalidOperationException("The restored database did not match the selected backup.");
            }
            using (var retentionDatabase = new SqliteDatabase(backupTestPath))
            {
                retentionDatabase.Initialize();
                var retentionRepository = new ActivityRepository(retentionDatabase);
                retentionRepository.Save(new ActivitySession
                {
                    StartTime = day.AddDays(-10), EndTime = day.AddDays(-10).AddMinutes(1),
                    ActivityType = ActivityType.Application, AppName = "Expired Backup Row", ProcessName = "expired-backup", Category = "Test"
                });
                var retentionBackupSettings = new SettingsService(new SettingsRepository(retentionDatabase));
                retentionBackupSettings.Load();
                await using var retentionBackupService = new BackupService(retentionDatabase, retentionBackupSettings);
                var retainedBackup = retentionBackupService.CreateBackup();
                retentionBackupService.ApplyRetention(day.AddDays(-1));
                using (var retainedProbe = new SqliteDatabase(retainedBackup.FullPath, createMigrationBackup: false))
                {
                    retainedProbe.Initialize();
                    var rows = new ActivityRepository(retainedProbe).GetRange(day.AddDays(-20), day.AddDays(1));
                    if (rows.Any(item => item.ProcessName == "expired-backup") || rows.All(item => item.ProcessName != "before"))
                        throw new InvalidOperationException("Backup retention did not remove expired history while keeping current history.");
                }
                retentionBackupService.DeleteAllBackups();
                if (retentionBackupService.ListBackups().Count != 0)
                    throw new InvalidOperationException("Complete backup deletion left recoverable history files.");
            }
            Stage("BACKUP_RESTORE_PASSED");

            var installedCommand = AutoStartService.BuildInstalledCommand("C:\\Program Files\\Tracker\\PCActivityTracker.exe");
            var developerCommand = AutoStartService.BuildDeveloperCommand("C:\\Tracker Folder\\bin\\PCActivityTracker.exe",
                "C:\\Tracker Folder\\scripts\\run-silent.vbs");
            if (!installedCommand.Contains("--background", StringComparison.OrdinalIgnoreCase) ||
                !developerCommand.Contains("wscript.exe", StringComparison.OrdinalIgnoreCase) ||
                !developerCommand.Contains("run-silent.vbs", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The sign-in startup commands were not suitable for installed and developer builds.");

            repository.DeleteAll();
            if (repository.GetRange(day, day.AddDays(1)).Count != 0)
                throw new InvalidOperationException("Secure history deletion did not remove all activity rows.");
            if (aiReportRepository.Get(AiReportType.Daily, sampleStart) is not null)
                throw new InvalidOperationException("Secure history deletion did not remove locally saved AI reports.");
            if (annotationRepository.GetRange(deepDiveDay, deepDiveDay.AddDays(1)).Count != 0)
                throw new InvalidOperationException("Secure history deletion did not remove manual annotations.");

            DiagnosticLogService.Error("SelfTest.Expected", new InvalidOperationException("diagnostic probe"));
            if (!File.Exists(DiagnosticLogService.Path))
                throw new InvalidOperationException("Local diagnostic logging was not operational.");
            DiagnosticLogService.DeleteAll();
            if (File.Exists(DiagnosticLogService.Path))
                throw new InvalidOperationException("Complete diagnostic deletion left a log file.");
            Stage("PASSED");

            Console.WriteLine("SELF-TEST PASSED: encrypted schema migration, explorer search/zoom, non-destructive annotations, explainable category rules, focus recovery analysis, Native Messaging, local-only AI, backup/restore, privacy deletion, and low-impact operation are operational.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("SELF-TEST FAILED: " + exception);
            return 1;
        }
        finally
        {
            try { System.IO.Directory.Delete(directory, true); } catch { }
        }
    }

}
