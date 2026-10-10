using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Xml.Linq;
using System.IO;
using System.Windows.Controls;
using System.Reflection;
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
                    }
                }
                Check.That(errors.Errors.Count == 0, "WPF binding errors: " + string.Join(" | ", errors.Errors));
                model.ReleaseUiData();
                Check.That(model.Timeline.Count == 0 && model.CreatorRankings.Count == 0, "UI release retained rankings.");
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
    private sealed class BindingErrorListener : TraceListener
    {
        internal List<string> Errors { get; } = new();
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { if (!string.IsNullOrEmpty(message)) Errors.Add(message); }
    }
}
