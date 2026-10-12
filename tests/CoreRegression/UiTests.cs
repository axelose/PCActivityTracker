using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Xml.Linq;
using System.IO;
using System.Windows.Controls;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using LocalAi.Runtime;
using PCActivityTracker;
using PCActivityTracker.Browser;
using PCActivityTracker.Data;
using PCActivityTracker.Domain;
using PCActivityTracker.Services;
using PCActivityTracker.Tracking;
using PCActivityTracker.ViewModels;
using PCActivityTracker.Views;

namespace CoreRegression;
internal static class UiTests
{
    internal static void Run()
    {
        // Resources/layout only. Never invoke App.OnStartup, show a window, register
        // extensions, modify auto-start, connect Ollama, or start foreground tracking.
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // A real App constructor schedules its startup on the Dispatcher. Load
        // only its resource dictionary into a plain Application instead.
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "App.xaml"));
        var resources = source.Root!.Element(presentation + "Application.Resources")!;
        var dictionary = new XElement(presentation + "ResourceDictionary",
            new XAttribute(XNamespace.Xmlns + "x", xaml),
            new XAttribute(XNamespace.Xmlns + "converters", "clr-namespace:PCActivityTracker.Converters;assembly=PCActivityTracker"),
            resources.Elements());
        foreach (var converter in dictionary.Descendants().Where(element =>
                     element.Name.NamespaceName == "clr-namespace:PCActivityTracker.Converters"))
            converter.Name = XName.Get(converter.Name.LocalName,
                "clr-namespace:PCActivityTracker.Converters;assembly=PCActivityTracker");
        app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        using var fixture = new Fixture();
        var previous = fixture.Settings.Current;
        previous.PrivacyConsentVersion = 1;
        previous.StartWithWindows = true;
        previous.StartMinimized = true;
        previous.RecordUrls = true;
        previous.RecordCreatorNames = true;
        var onboarding = new PrivacyOnboardingWindow(previous);
        try
        {
            Check.That(((CheckBox)onboarding.FindName("CreatorsCheck")).IsChecked == false,
                "New creator identifiers were enabled without a new choice.");
            Check.That(((CheckBox)onboarding.FindName("UrlsCheck")).IsChecked == true, "Existing URL setting changed.");
            var result = (AppSettings)typeof(PrivacyOnboardingWindow).GetMethod("BuildResult", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(onboarding, new object[] { true, 180 })!;
            Check.That(result.PrivacyConsentVersion == 2 && !result.RecordCreatorNames &&
                result.StartWithWindows && result.StartMinimized && result.RetentionDays == 180,
                "Consent upgrade changed unrelated startup settings or enabled creator fields.");
        }
        finally { onboarding.Close(); }
        using var sessionLock = new SessionLockMonitor();
        var tracker = new ActivityTrackerService(new ForegroundActivityProvider(new IdleDetector(), new BrowserStateStore()),
            fixture.Activities, fixture.Settings, sessionLock);
        var backup = new BackupService(fixture.Database, fixture.Settings);
        try
        {
            BrowserBridgeDiagnostics Diagnostics() => new("Test", "未接続", null, null, null, null, null,
                BrowserNativeMessagingServer.ExpectedExtensionVersion, 0, 0, 0, "Test");
            var aggregate = new AggregationService(fixture.Activities, fixture.Settings, fixture.Annotations);
            using var model = new MainViewModel(aggregate, fixture.Activities, fixture.Annotations,
                new ActivityExplorerService(fixture.Activities, fixture.Annotations, fixture.Settings),
                fixture.Settings, tracker, fixture.Database.Path, Diagnostics, backup, new AutoStartService(),
                new AiReportService(aggregate, fixture.Activities, new AiReportRepository(fixture.Database), new GpuExecutionCoordinator()),
                new GameSafetyService(tracker), new NativeMessagingRegistrationService(fixture.Settings, fixture.DirectoryPath),
                new DiagnosticExportService(fixture.Database, fixture.Settings, Diagnostics));
            Check.That(!model.UiRefreshRunning, "Background startup enabled dashboard/diagnostic timers.");
            model.SetUiActive(false);
            var errors = new BindingErrorListener();
            PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
            var window = new MainWindow(model);
            try
            {
                foreach (var theme in new[] { "Light", "Dark", "System" })
                {
                    ThemeService.Apply(theme);
                    foreach (var page in new[] { "Dashboard", "Today", "Day", "Week", "Month", "Analysis", "Explorer", "AiReports", "Settings" })
                    {
                        // Select the binding state without running navigation commands
                        // (settings navigation would explicitly probe local Ollama).
                        typeof(MainViewModel).GetProperty(nameof(MainViewModel.SelectedPage))!.SetValue(model, page);
                        var content = (FrameworkElement)window.Content;
                        content.Measure(new Size(1280, 900));
                        content.Arrange(new Rect(0, 0, 1280, 900));
                        content.UpdateLayout();
                        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                        if (page == "Explorer")
                        {
                            var sampleStart = new DateTimeOffset(DateTime.Today.AddHours(9));
                            var sample = new ExplorerTimelineItem(0, sampleStart, sampleStart.AddMinutes(5),
                                null, null, "09:00 – 09:05", "www.youtube.com", "Synthetic Channel", "5分", "Entertainment",
                                "標準分類: youtube.com", "", "", "", false, true, true, false, 1,
                                "Google Chrome", "chrome", "www.youtube.com", "Synthetic Channel", "youtube:channel:UC" + new string('a', 22));
                            model.ExplorerTimeline.Clear(); model.ExplorerTimeline.Add(sample); model.SelectedExplorerItem = sample;
                            typeof(MainViewModel).GetProperty(nameof(MainViewModel.HasNoExplorerResults))!.SetValue(model, false);
                            model.QuickCategory = "Education";
                            Check.That(model.CanQuickCategorize && model.QuickCategoryScope == "チャンネルID", "Channel history did not prefer stable ID rule.");
                            Check.That(!model.CanSaveQuickCategory, "A quick rule could save without review.");
                            var mixed = sample with { ActivityCount = 2, CategoryBreakdown = "Education 3分 / Entertainment 2分",
                                Parts = new[] { sample with { Category = "Education", End = sampleStart.AddMinutes(3), Duration = "3分" },
                                    sample with { Start = sampleStart.AddMinutes(3), Duration = "2分" } } };
                            model.ExplorerTimeline.Clear(); model.ExplorerTimeline.Add(mixed); model.SelectedExplorerBucket = mixed;
                            Check.That(!model.CanQuickCategorize, "Mixed bucket could create a representative rule.");
                            model.SelectExplorerPartCommand.Execute(mixed.Parts![0]);
                            Check.That(model.CanQuickCategorize && model.SelectedExplorerItem == mixed.Parts[0] && model.SelectedExplorerBucket == mixed,
                                "Selecting an original piece lost the parent or editing target.");
                            model.ExplorerCategoryComparison.Clear();
                            model.ExplorerCategoryComparison.Add(new ComparisonItem("カテゴリ", "Education", "1分", "3分", "+2分", true));
                            model.SavedSearchName = "Synthetic learning";
                            model.SaveExplorerSearchCommand.Execute(null);
                            ((Expander)window.FindName("ExplorerToolsExpander")).IsExpanded = true;
                            content.UpdateLayout();
                            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                            Render(content, "explorer-" + theme + "-timeline");
                            typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsUnclassifiedView))!.SetValue(model, true);
                            model.UnclassifiedItems.Add(new UnclassifiedItem(CategoryRuleTarget.ProcessName, "synthetic-editor", "Synthetic Editor", "43分", 8, 2580));
                            model.UnclassifiedItems.Add(new UnclassifiedItem(CategoryRuleTarget.Domain, "unknown.example.test", "unknown.example.test", "18分", 3, 1080));
                            model.AiCategorySuggestions.Add(new(new CategoryRuleDraft(CategoryRuleTarget.ProcessName, "synthetic-editor", "Development"), "Synthetic Editor"));
                            content.UpdateLayout();
                            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                            var list = (ListBox)window.FindName("UnclassifiedList");
                            list.SelectedItems.Add(model.UnclassifiedItems[0]);
                            list.SelectedItems.Add(model.UnclassifiedItems[1]);
                            Check.That(list.SelectedItems.Count == 2 && !model.CanSaveBatchCategory, "Unknown multi-select or review gating failed.");
                            Render(content, "explorer-" + theme + "-unclassified");
                            model.UnclassifiedItems.Clear();
                            typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsUnclassifiedView))!.SetValue(model, false);
                        }
                    }
                }
                ExerciseCategoryCommands(model, fixture);
                Check.That(errors.Errors.Count == 0, "WPF binding errors: " + string.Join(" | ", errors.Errors));
                model.ReleaseUiData();
                Check.That(model.Timeline.Count == 0 && model.CreatorRankings.Count == 0 && model.UnclassifiedItems.Count == 0 &&
                    !model.CanSaveBatchCategory && !model.CanSaveQuickCategory, "UI release retained rankings or reviews.");
            }
            finally
            {
                window.DataContext = null;
                window.Content = null;
                window.Close();
                PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
            }
        }
        finally
        {
            backup.DisposeAsync().AsTask().GetAwaiter().GetResult();
            tracker.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
    private static void Render(FrameworkElement content, string name)
    {
        content.Measure(new Size(1280, 900)); content.Arrange(new Rect(0, 0, 1280, 900)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1280, 900, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        // Synthetic UI previews stay in ignored build output, never public source.
        using var stream = File.Create(Path.Combine(AppContext.BaseDirectory, name + ".png")); encoder.Save(stream);
    }

    private static void ExerciseCategoryCommands(MainViewModel model, Fixture fixture)
    {
        var oldContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            typeof(MainViewModel).GetProperty(nameof(MainViewModel.SelectedPage))!.SetValue(model, "Explorer");
            var start = new DateTimeOffset(DateTime.Today.AddHours(9));
            var id = "youtube:channel:UC" + new string('b', 22);
            fixture.Activities.Save(new ActivitySession { StartTime = start, EndTime = start.AddMinutes(5),
                ActivityType = ActivityType.Website, AppName = "Chrome", ProcessName = "chrome", Domain = "youtube.com",
                CreatorName = "Synthetic learning channel", CreatorId = id });
            model.SelectedExplorerItem = new ExplorerTimelineItem(0, start, start.AddMinutes(5), null, null,
                "09:00 – 09:05", "youtube.com", "Synthetic", "5分", "Entertainment", "Preset", "", "", "",
                false, true, false, false, 1, "Chrome", "chrome", "youtube.com", "Synthetic", id);
            model.QuickCategory = " 学習 / 技術 ";
            Execute(model.PreviewQuickCategoryCommand);
            Check.That(model.CanSaveQuickCategory, "UI quick preview did not enable confirmed save: " + model.StatusMessage);
            model.QuickCategory = "Education";
            Check.That(!model.CanSaveQuickCategory, "Changing category did not invalidate review.");
            Execute(model.PreviewQuickCategoryCommand); Execute(model.SaveQuickCategoryCommand);
            Check.That(fixture.Settings.CategoryRules.Any(rule => rule.Target == CategoryRuleTarget.CreatorId && rule.Pattern == id && rule.Category == "Education"),
                "UI quick command did not persist a stable-ID rule: " + model.StatusMessage);
            model.QuickCategory = "Manual > Temporary";
            Execute(model.SetIntervalCategoryCommand);
            Check.That(fixture.Annotations.GetRange(start, start.AddMinutes(5)).Single().CategoryOverride == "Manual > Temporary",
                "UI time-only correction failed: " + model.StatusMessage);
            var selected = new[] { new UnclassifiedItem(CategoryRuleTarget.ProcessName, "synthetic-editor", "Synthetic Editor", "1分", 1, 60),
                new UnclassifiedItem(CategoryRuleTarget.Domain, "unknown.example.test", "unknown.example.test", "1分", 1, 60) };
            typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsUnclassifiedView))!.SetValue(model, true);
            model.BatchCategory = " 仕事 / 開発 ";
            Execute(model.PreviewBatchCategoryCommand, selected);
            Check.That(model.CanSaveBatchCategory, "UI batch preview failed: " + model.StatusMessage);
            var count = fixture.Settings.CategoryRules.Count;
            Execute(model.SaveBatchCategoryCommand, selected.Take(1).ToArray());
            Check.That(fixture.Settings.CategoryRules.Count == count, "Changing selected targets bypassed confirmation.");
            Execute(model.SaveBatchCategoryCommand, selected);
            Check.That(fixture.Settings.CategoryRules.Count == count + 2 && fixture.Settings.CategoryRules.Count(rule => rule.Category == "仕事 > 開発") == 2,
                "UI batch save or category normalization failed: " + model.StatusMessage);
            typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsUnclassifiedView))!.SetValue(model, false);
            model.SearchCategory = "Education"; model.SearchExcludedCategory = "Gaming";
            model.SearchCreator = id; model.SearchIncludeIdle = false; model.TimelineZoomMinutes = 15;
            model.SavedSearchName = "Learning only";
            model.SaveExplorerSearchCommand.Execute(null);
            model.SelectedSavedSearch = model.SavedExplorerSearches.Single(item => item.Name == "Learning only");
            model.SearchCategory = "Other"; model.SearchExcludedCategory = ""; model.SearchCreator = "";
            Execute(model.ApplyExplorerSearchCommand);
            Check.That(model.SearchCategory == "Education" && model.SearchExcludedCategory == "Gaming" && model.SearchCreator == id &&
                !model.SearchIncludeIdle && model.TimelineZoomMinutes == 15, "Saved-search UI did not restore all conditions.");
            model.SelectedSavedSearch = model.SavedExplorerSearches.Single(item => item.Name == "Learning only");
            model.DeleteExplorerSearchCommand.Execute(null);
            Check.That(!model.SavedExplorerSearches.Any(item => item.Name == "Learning only"), "Saved-search UI deletion failed.");

            typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsUnclassifiedView))!.SetValue(model, true);
            var suggestion = new AiCategorySuggestion(new(CategoryRuleTarget.ProcessName, "synthetic-ai-editor", "Education"), "Synthetic AI Editor");
            model.AiCategorySuggestions.Add(suggestion);
            var beforeAi = fixture.Settings.CategoryRules.Count;
            Check.That(!model.CanSaveAiCategories, "Receiving an AI suggestion allowed immediate save.");
            Execute(model.SaveAiCategoriesCommand, new[] { suggestion });
            Check.That(fixture.Settings.CategoryRules.Count == beforeAi, "AI bypassed impact preview.");
            Execute(model.PreviewAiCategoriesCommand, new[] { suggestion });
            Check.That(model.CanSaveAiCategories, "AI selection impact preview failed: " + model.StatusMessage);
            Execute(model.SaveAiCategoriesCommand, Array.Empty<AiCategorySuggestion>());
            Check.That(fixture.Settings.CategoryRules.Count == beforeAi, "Changed AI selection bypassed confirmation.");
            Execute(model.SaveAiCategoriesCommand, new[] { suggestion });
            Check.That(fixture.Settings.CategoryRules.Count == beforeAi + 1 && model.AiCategorySuggestions.Count == 0 && !model.CanSaveAiCategories,
                "AI explicit approval did not save and discard ephemeral candidates: " + model.StatusMessage);
            model.AiCategorySuggestions.Add(suggestion);
            model.SelectedDate = model.SelectedDate.AddDays(-1);
            Check.That(model.AiCategorySuggestions.Count == 0 && !model.CanSaveAiCategories, "Date change retained stale AI candidates.");
            typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsUnclassifiedView))!.SetValue(model, false);
        }
        finally { SynchronizationContext.SetSynchronizationContext(oldContext); }
    }

    private static void Execute(ICommand command, object? parameter = null)
    {
        var frame = new DispatcherFrame();
        var timedOut = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) => { timedOut = true; frame.Continue = false; };
        EventHandler handler = (_, _) => { if (command.CanExecute(parameter)) frame.Continue = false; };
        command.CanExecuteChanged += handler;
        try
        {
            timer.Start(); command.Execute(parameter);
            if (!command.CanExecute(parameter)) Dispatcher.PushFrame(frame);
            Check.That(!timedOut, "UI classification command timed out.");
        }
        finally { timer.Stop(); command.CanExecuteChanged -= handler; }
    }
    private sealed class BindingErrorListener : TraceListener
    {
        internal List<string> Errors { get; } = new();
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { if (!string.IsNullOrEmpty(message)) Errors.Add(message); }
    }
}
