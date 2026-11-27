using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TractorTool;
using TractorTool.Contracts;
using TractorTool.Contracts.Tasks;
using TractorTool.Core.Logging;
using TractorTool.Core.Packages;
using TractorTool.Core.Plugins;
using TractorTool.Core.Processes;
using TractorTool.Core.Storage;
using TractorTool.Services;
using TractorTool.UI;
using TractorTool.ViewModels;
using TractorTool.Views;

if (args.Length != 2 && !(args.Length == 3 && args[2] == "--navigation-only"))
    throw new ArgumentException("Usage: checks <published-host> <isolated-evidence-directory> [--navigation-only]");
var published = Path.GetFullPath(args[0]);
// Exercise the published SDK's policy; the headless check process has extra Avalonia assemblies.
using (var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(published, "TractorTool.runtimeconfig.json"))))
{
    const string policy = "TractorTool.PluginSdk.ReservedAssemblies";
    AppContext.SetData(policy, config.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties").GetProperty(policy).GetString());
}
var output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
// Loads the published application XAML and SDK, but not its production desktop lifetime.
// Real PNG decoding uses Skia; the headless windowing backend still avoids native UI.
AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
var passed = 0;
var failed = 0;
void Assert(bool value) { if (!value) throw new InvalidOperationException("Assertion failed"); }
void Pump(Task task)
{
    var deadline = DateTime.UtcNow.AddSeconds(15);
    while (!task.IsCompleted && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
    if (!task.IsCompleted) throw new TimeoutException("check timed out");
    task.GetAwaiter().GetResult(); Dispatcher.UIThread.RunJobs();
}
void Check(string name, Action action)
{
    try { action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
if (args.Length == 3)
{
    NavigationChecks.Run(Check, output);
    Console.WriteLine($"Navigation checks: {passed} passed, {failed} failed.");
    return failed == 0 ? 0 : 1;
}
var compatibility = new HostCompatibility("windows", "win-x64", UiApi.Version, UiApi.AvaloniaVersion);
var paths = new AppPaths(published, Path.Combine(output, "fixed"), Path.Combine(output, "data"));
paths.EnsureDataDirectories();
var log = new AppLog(paths.LogRoot);
var host = new PluginHost(paths, compatibility, log);
host.Discover([]);
var settingsStore = new JsonFileStore<HostSettings>(Path.Combine(paths.ConfigRoot, "settings.json"), HostSettings.Validate);
var vm = new MainViewModel(paths, host, log, new SettingsViewModel(paths, settingsStore, settingsStore.Load(), log), () => Task.CompletedTask);
var window = new TractorTool.Views.MainWindow { DataContext = vm };
window.Show();
Check("Closing the dialog owner declines both displayed and queued requests", () =>
{
    using var dialogs = new HostDialogs(window);
    var displayed = dialogs.ConfirmAsync("Fixture", "First request");
    Dispatcher.UIThread.RunJobs();
    var queued = dialogs.ConfirmAsync("Fixture", "Second request");
    Assert(!displayed.IsCompleted && !queued.IsCompleted);
    dialogs.Dispose();
    Pump(Task.WhenAll(displayed, queued));
    Assert(!displayed.Result && !queued.Result);
    using var caller = new CancellationTokenSource();
    caller.Cancel();
    var cancelled = dialogs.ConfirmAsync("Fixture", "Cancelled by caller", caller.Token);
    Assert(cancelled.IsCanceled);
});
Check("Custom title surface has the native title role and decorative content cannot intercept hits", () =>
{
    var title = window.FindControl<Border>("TitleBarDragRegion");
    Assert(title is not null && WindowDecorationProperties.GetElementRole(title) == WindowDecorationsElementRole.TitleBar);
    Assert(title!.Child is StackPanel { IsHitTestVisible: false });
    window.UpdateLayout();
    Assert(title.Bounds.Height == 32 && window.TitleBar.Height == title.Bounds.Height);
    Assert(window.FindControl<FluentAvalonia.UI.Controls.FANavigationView>("NavigationView")!.Bounds.Top == title.Bounds.Bottom);
});
Check("Home is the initial page before plugins finish loading", () =>
{
    Assert(vm.CurrentPage is TractorTool.Views.HomeView && vm.SelectedNavigation?.Key == "home");
    Assert(vm.Navigation.First().Key == "home" && vm.FooterNavigation.Count == 3);
    Assert(vm.Home.IsLoading && !vm.Home.IsEmpty);
    Assert(vm.Plugins.IsLoading && !vm.Plugins.IsEmpty && !vm.Plugins.NoMatches);
});
Check("Routine navigation stays out of logs and the error badge updates on other pages", () =>
{
    var count = log.Snapshot().Entries.Count;
    vm.ShowSettingsCommand.Execute(null); vm.ShowHomeCommand.Execute(null); Dispatcher.UIThread.RunJobs();
    Assert(log.Snapshot().Entries.Count == count);
    log.Write(AppLogLevel.Warning, "Fixture", "warning only"); Dispatcher.UIThread.RunJobs();
    Assert(!vm.FooterNavigation[0].HasErrors);
    log.Write(AppLogLevel.Error, "Fixture", "first error"); Dispatcher.UIThread.RunJobs();
    Assert(vm.FooterNavigation[0].UnreadErrors == 1 && vm.FooterNavigation[0].HasErrors);
});
Check("Logs read clears paused and filtered errors; later errors alert and tasks share the page", () =>
{
    vm.ShowLogsCommand.Execute(null); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
    Assert(vm.CurrentPage is LogsView);
    vm.Logs.SelectedLevel = vm.Logs.Levels.Single(x => x.Level == AppLogLevel.Error);
    vm.Logs.Search = "first error";
    Assert(vm.Logs.Entries.Count == 1 && vm.Logs.Entries[0].IsError);
    vm.Logs.IsPaused = true;
    log.Write(AppLogLevel.Error, "Fixture", "second error"); Dispatcher.UIThread.RunJobs();
    vm.Logs.MarkReadCommand.Execute(null); Dispatcher.UIThread.RunJobs();
    Assert(vm.FooterNavigation[0].UnreadErrors == 0 && vm.Logs.Entries.Count == 1);
    log.Write(AppLogLevel.Error, "Fixture", "third error"); Dispatcher.UIThread.RunJobs();
    Assert(vm.FooterNavigation[0].UnreadErrors == 1);
    vm.Logs.Search = ""; vm.Logs.IsPaused = false;
    Assert(vm.Logs.Entries.Count == 3 && vm.Logs.Entries.Select(x => x.Entry.Id).SequenceEqual(vm.Logs.Entries.Select(x => x.Entry.Id).Order()));
    vm.Logs.MarkReadCommand.Execute(null); Dispatcher.UIThread.RunJobs();
    Assert(!vm.FooterNavigation[0].HasErrors);
    vm.Logs.SelectedTab = 1; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
    Assert(vm.CurrentPage!.GetVisualDescendants().OfType<TasksView>().Any());
    vm.Logs.SelectedTab = 0; vm.Logs.SelectedLevel = null;
    vm.ShowHomeCommand.Execute(null);
});
Check("Real published plugin initialized without replacing the user's selected page", () =>
{
    vm.ShowSettingsCommand.Execute(null);
    var current = vm.CurrentPage;
    Pump(vm.InitializeAsync());
    Assert(host.Records.Single().State == PluginState.Loaded);
    Assert(vm.Navigation.Count == 2 && vm.Settings.PluginSettings.Count == 1);
    Assert(ReferenceEquals(current, vm.CurrentPage) && vm.SelectedNavigation?.Key == "settings");
});
Check("Repeated completed initialization does not duplicate navigation or settings", () =>
{
    var first = vm.InitializeAsync();
    var second = vm.InitializeAsync();
    Assert(ReferenceEquals(first, second));
    Pump(Task.WhenAll(first, second));
    Assert(vm.Navigation.Count == 2 && vm.Settings.PluginSettings.Count == 1 && vm.Home.InstalledPlugins.Count == 1);
});
Check("Task actions open the owning plugin page or plugin management when unavailable", () =>
{
    var snapshot = new BackgroundTaskSnapshot(Guid.NewGuid(), "Test.Independent", Guid.NewGuid(), "Fixture task",
        BackgroundTaskState.Succeeded, 100, "", null, null, DateTimeOffset.Now, DateTimeOffset.Now);
    var available = new TaskRow(snapshot, vm.Logs.Tasks);
    vm.ShowLogsCommand.Execute(null);
    available.OpenCommand.Execute(null);
    Assert(vm.SelectedNavigation?.Key == "Test.Independent/home" && vm.CurrentPage is Border);
    var unavailable = new TaskRow(snapshot with { PluginId = "Fixture.Uninstalled" }, vm.Logs.Tasks);
    Assert(unavailable.OpenLabel == "查看插件");
    unavailable.OpenCommand.Execute(null);
    Assert(vm.SelectedNavigation?.Key == "plugins" && vm.CurrentPage is PluginsView);
});
Check("Home lists the real plugin independently of management search and reuses navigation", () =>
{
    vm.ShowHomeCommand.Execute(null); window.UpdateLayout();
    var card = vm.Home.InstalledPlugins.Single();
    Assert(!vm.Home.IsLoading && !vm.Home.IsEmpty && !vm.Home.HasDirectoryError);
    Assert(card.Plugin.Name == host.Records.Single().Name && card.Plugin.CanOpen);
    Assert(card.Plugin.ImageSource is Bitmap);
    Assert(ReferenceEquals(card.Plugin.ImageSource, vm.Navigation.Single(n => n.Key == "Test.Independent/home").ImageSource));
    Assert(ReferenceEquals(card.OpenCommand, vm.Navigation.Single(n => n.Key == "Test.Independent/home").OpenCommand));
    Assert(vm.CurrentPage!.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == card.Plugin.Name));
    vm.Plugins.Search = "does-not-match-any-plugin";
    Assert(!vm.Plugins.FilteredItems.Any() && vm.Home.InstalledPlugins.Count == 1);
    vm.Plugins.Search = "";
});
Check("Real page and private dependency render", () =>
{
    vm.Home.InstalledPlugins.Single().OpenCommand.Execute(null); window.UpdateLayout();
    Assert(vm.SelectedNavigation?.Key == "Test.Independent/home");
    Assert(vm.CurrentPage!.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "私有依赖已加载"));
});
Check("Plugin settings write through registered page", () =>
{
    vm.ShowSettingsCommand.Execute(null);
    vm.Settings.SelectedPage = vm.Settings.PluginSettings.Single(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
    var settingsView = (SettingsView)vm.CurrentPage!;
    var softwareGroup = settingsView.FindControl<Expander>("SoftwareSettingsGroup")!;
    var pluginGroup = settingsView.FindControl<Expander>("PluginSettingsGroup")!;
    var editor = settingsView.GetVisualDescendants().OfType<TextBox>().Single();
    Assert(softwareGroup.IsExpanded && pluginGroup.IsExpanded && pluginGroup.IsVisible);
    var categoryIcon = pluginGroup.GetVisualDescendants().OfType<TractorTool.UI.Controls.ToolIcon>().Single();
    Assert(categoryIcon.Kind == ToolIconKind.Settings && categoryIcon.ImageSource is Bitmap);
    Assert(ReferenceEquals(categoryIcon.ImageSource, vm.Navigation.Single(n => n.Key == "Test.Independent/home").ImageSource));
    softwareGroup.IsExpanded = pluginGroup.IsExpanded = false;
    window.UpdateLayout();
    Assert(ReferenceEquals(vm.Settings.SelectedPage, vm.Settings.PluginSettings.Single()));
    Assert(ReferenceEquals(editor, settingsView.GetVisualDescendants().OfType<TextBox>().Single()));
    pluginGroup.IsExpanded = true;
    Assert(!softwareGroup.IsExpanded && ReferenceEquals(((ListBox)pluginGroup.Content!).SelectedItem, vm.Settings.SelectedCategory));
    vm.CurrentPage!.GetVisualDescendants().OfType<TextBox>().Single().Text = "integration-persisted";
    vm.CurrentPage!.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "保存插件设置")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Assert(File.ReadAllText(Path.Combine(paths.GetPluginDataDirectory("Test.Independent"), "settings.json")).Contains("integration-persisted"));
    vm.Navigation.Single(n => n.Key == "Test.Independent/home").OpenCommand.Execute(null); window.UpdateLayout();
    Assert(vm.CurrentPage!.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "备注：integration-persisted"));
});
Check("Host settings render and immediately apply dark accent without a save button", () =>
{
    vm.ShowSettingsCommand.Execute(null); window.UpdateLayout();
    vm.Settings.SelectedPage = vm.Settings.AppearancePage; window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
    var settingsPage = vm.CurrentPage ?? throw new InvalidOperationException("Settings page missing");
    Assert(settingsPage.GetVisualDescendants().OfType<SettingsAppearanceView>().Single().GetVisualDescendants().OfType<ComboBox>().Count() == 2);
    vm.Settings.SelectedTheme = vm.Settings.Themes.Single(t => t.Id == "Dark");
    vm.Settings.SelectedAccent = vm.Settings.Accents.Single(a => a.Value == "#0F766E");
    Assert(Application.Current!.RequestedThemeVariant == ThemeVariant.Dark);
    Assert(!settingsPage.GetVisualDescendants().OfType<Button>().Any(b => Equals(b.Content, "保存外观")));
    Pump(vm.Settings.SaveCompletion);
    Assert(settingsStore.Load().Theme == "Dark" && settingsStore.Load().Accent == "#0F766E");
});
Check("Reload settings applies saved theme", () => { ThemeService.Apply(new() { Theme = "Light" }); ThemeService.Apply(settingsStore.Load()); Assert(Application.Current!.RequestedThemeVariant == ThemeVariant.Dark); });
Check("Appearance animation preference reaches window, navigation and setting-row template surfaces", () =>
{
    vm.ShowSettingsCommand.Execute(null);
    vm.Settings.EnableAnimations = true;
    window.UpdateLayout();
    var titleBar = window.FindControl<Border>("TitleBarDragRegion")!;
    var navigation = window.FindControl<FluentAvalonia.UI.Controls.FANavigationView>("NavigationView")!;
    var row = vm.CurrentPage!.GetVisualDescendants().OfType<TractorTool.UI.Controls.SettingRow>().First();
    var surface = row.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("themeSurface"));
    bool HasBackgroundTransition(Avalonia.Animation.Animatable control) =>
        control.Transitions?.OfType<Avalonia.Animation.BrushTransition>().Any(t => t.Property?.Name == "Background") == true;
    Assert(Motion.GetIsEnabled(window));
    Assert(HasBackgroundTransition(window) && HasBackgroundTransition(titleBar) &&
        HasBackgroundTransition(navigation) && HasBackgroundTransition(surface));
    vm.Settings.EnableAnimations = false;
    window.UpdateLayout();
    Assert(!Motion.GetIsEnabled(window));
    Assert(!HasBackgroundTransition(window) && !HasBackgroundTransition(titleBar) &&
        !HasBackgroundTransition(navigation) && !HasBackgroundTransition(surface));
    vm.Settings.EnableAnimations = true;
    window.UpdateLayout();
    Assert(HasBackgroundTransition(surface));
    Pump(vm.Settings.SaveCompletion);
});
Check("Home and three footer navigation entries remain reachable", () => { vm.ShowLogsCommand.Execute(null); Assert(vm.PageTitle.StartsWith("日志")); vm.ShowPluginsCommand.Execute(null); Assert(vm.PageTitle == "插件"); vm.ShowSettingsCommand.Execute(null); Assert(vm.PageTitle == "设置"); vm.ShowHomeCommand.Execute(null); Assert(vm.PageTitle == "主页" && vm.SelectedNavigation?.Key == "home"); });
Check("Page navigation and hide do not cancel resident service", () =>
{
    vm.ShowLogsCommand.Execute(null); window.Hide();
    Pump(Task.Delay(1200));
    var entries = log.Snapshot().Entries.Where(e => e.Source == "Test.Independent").ToArray();
    Assert(entries.Any(e => e.Message == "常驻服务开始。"));
    Assert(!entries.Any(e => e.Message is "常驻服务已结束。" or "常驻服务已停止。" or "常驻服务失败" ||
        e.Message.StartsWith("background-tick:")));
    window.Show();
});
Check("Shutdown keeps icons alive until the window closes and records plugin cleanup", () =>
{
    var icon = (Bitmap)vm.Navigation.First().ImageSource!;
    vm.Detach();
    using var encoded = new MemoryStream();
    icon.Save(encoded, PngBitmapEncoderOptions.Default);
    Assert(encoded.Length > 0);
    Pump(host.DisposeAsync().AsTask());
    Assert(log.ReadRecent().Contains("StopAsync") && log.ReadRecent().Contains("DisposeAsync"));
    Assert(log.Snapshot().Entries.Any(e => e.Source == "Test.Independent" && e.Message == "常驻服务已结束。"));
});
NavigationChecks.Run(Check, output, window);
window.Close();

foreach (var blockedDirectory in new[] { false, true })
{
    Check(blockedDirectory ? "Home reports an inaccessible plugin directory instead of an empty installation" : "Home renders the first-install empty state", () =>
    {
        var root = Path.Combine(output, "home-" + Guid.NewGuid().ToString("N"));
        var casePaths = new AppPaths(Path.Combine(root, "app"), Path.Combine(root, "data"));
        casePaths.EnsureDataDirectories();
        Directory.CreateDirectory(casePaths.AppRoot);
        if (blockedDirectory) File.WriteAllText(casePaths.PluginRoot, "fixture directory blocker");
        var caseLog = new AppLog(casePaths.LogRoot);
        var caseHost = new PluginHost(casePaths, compatibility, caseLog);
        var caseVm = new MainViewModel(casePaths, caseHost, caseLog,
            new SettingsViewModel(casePaths, new(Path.Combine(casePaths.ConfigRoot, "settings.json")), new(), caseLog), () => Task.CompletedTask);
        try
        {
            caseHost.Discover([]);
            Pump(caseVm.InitializeAsync());
            Assert(caseVm.CurrentPage is TractorTool.Views.HomeView && caseVm.Navigation.Single().Key == "home");
            Assert(caseVm.Home.InstalledPlugins.Count == 0 && !caseVm.Home.IsLoading);
            Assert(caseVm.Home.HasDirectoryError == blockedDirectory && caseVm.Home.IsEmpty != blockedDirectory);
            Assert(!caseVm.Plugins.IsLoading && !caseVm.Plugins.NoMatches && !caseVm.Plugins.HasItems);
            Assert(caseVm.Plugins.HasDirectoryError == blockedDirectory && caseVm.Plugins.IsEmpty != blockedDirectory);
            caseVm.Home.ShowPluginsCommand.Execute(null);
            Assert(caseVm.PageTitle == "插件");
        }
        finally { caseVm.Dispose(); Pump(caseHost.DisposeAsync().AsTask()); }
    });
}

var fixtureDirectory = Directory.GetDirectories(Path.Combine(published, "Plugins", "Test.Independent")).Single();
var originalManifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(Path.Combine(fixtureDirectory, "plugin.json")), JsonOptions.Value)!;
const string privateAssemblyName = "System.TractorTool.FixtureDependency";
const string missingPrivateDependency = "插件缺少私有依赖：" + privateAssemblyName;
void RemovePrivateDependency(string directory) => File.Move(Path.Combine(directory, privateAssemblyName + ".dll"), Path.Combine(directory, privateAssemblyName + ".missing"));
void CopyTree(string source, string destination)
{
    Directory.CreateDirectory(destination);
    foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
    foreach (var child in Directory.GetDirectories(source)) CopyTree(child, Path.Combine(destination, Path.GetFileName(child)));
}
void Fault(string name, PluginManifest manifest, PluginState expected, Action<string>? mutate = null, bool disabled = false, bool expectIconFallback = false, string? expectedFailure = null)
{
    Check(name, () =>
    {
        var caseRoot = Path.Combine(output, "fault-" + Guid.NewGuid().ToString("N"));
        var casePaths = new AppPaths(Path.Combine(caseRoot, "app"), Path.Combine(caseRoot, "data"));
        casePaths.EnsureDataDirectories();
        var pluginDir = Path.Combine(casePaths.PluginRoot, manifest.Id, manifest.Version);
        CopyTree(fixtureDirectory, pluginDir);
        File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), JsonSerializer.Serialize(manifest, JsonOptions.Value));
        mutate?.Invoke(pluginDir);
        var marker = Path.Combine(caseRoot, "entry-marker.txt");
        Environment.SetEnvironmentVariable("TRACTORTOOL_TEST_ENTRY_MARKER", marker);
        var caseHost = new PluginHost(casePaths, compatibility, new AppLog(casePaths.LogRoot));
        var caseVm = new MainViewModel(casePaths, caseHost, new AppLog(casePaths.LogRoot),
            new SettingsViewModel(casePaths, new(Path.Combine(casePaths.ConfigRoot, "settings.json")), new(), log), () => Task.CompletedTask);
        try
        {
            caseHost.Discover(disabled ? [manifest.Id] : []);
            Pump(caseVm.InitializeAsync());
            var record = caseHost.Records.Single();
            Assert(record.State == expected);
            if (expectedFailure is not null) Assert(record.Detail.Contains(expectedFailure, StringComparison.Ordinal));
            if (expectIconFallback)
            {
                var card = caseVm.Home.InstalledPlugins.Single();
                Assert(card.Plugin.ImageSource is null && card.Plugin.CanOpen);
                card.OpenCommand.Execute(null);
                Assert(caseVm.PageTitle == "独立插件" && caseVm.CurrentPage?.DataContext is not MessageViewModel);
            }
            if (expected is PluginState.Disabled or PluginState.Incompatible) Assert(!File.Exists(marker));
            if (expected != PluginState.Loaded)
            {
                Assert(caseVm.Navigation.Single().Key == "home" && caseVm.Settings.PluginSettings.Count == 0);
                var card = caseVm.Home.InstalledPlugins.Single();
                Assert(!card.Plugin.CanOpen && ReferenceEquals(card.OpenCommand, caseVm.ShowPluginsCommand));
                card.OpenCommand.Execute(null);
                Assert(caseVm.PageTitle == "插件");
            }
            if (manifest.EntryType.EndsWith("BrokenPagePlugin"))
            {
                caseVm.Home.InstalledPlugins.Single().OpenCommand.Execute(null);
                Assert(caseVm.CurrentPage?.DataContext is MessageViewModel message && message.Message.Contains("fixture page failed"));
                caseVm.ShowSettingsCommand.Execute(null); Assert(caseVm.PageTitle == "设置");
            }
        }
        finally { Environment.SetEnvironmentVariable("TRACTORTOOL_TEST_ENTRY_MARKER", null); caseVm.Dispose(); Pump(caseHost.DisposeAsync().AsTask()); }
    });
}
Fault("Disabled plugin does not execute constructor", originalManifest, PluginState.Disabled, disabled: true);
Fault("API mismatch does not execute constructor", originalManifest with { ApiVersion = "99.0" }, PluginState.Incompatible);
Fault("Avalonia mismatch does not execute constructor", originalManifest with { AvaloniaVersion = "0.0.1" }, PluginState.Incompatible);
Fault("Wrong platform does not execute constructor", originalManifest with { SupportedPlatforms = ["not-this-os"] }, PluginState.Incompatible);
Fault("Missing entry assembly contained", originalManifest with { EntryAssembly = "missing.dll" }, PluginState.Failed);
Fault("Unknown entry type contained", originalManifest with { EntryType = "Missing.Type" }, PluginState.Failed);
Fault("Wrong entry interface contained", originalManifest with { EntryType = "IndependentPlugin.ProbeSettings" }, PluginState.Failed);
Fault("Initialization exception contained", originalManifest with { EntryType = "IndependentPlugin.ThrowingPlugin" }, PluginState.Failed);
Fault("Partial registration rolled back", originalManifest with { EntryType = "IndependentPlugin.BadRegistrationPlugin" }, PluginState.Failed);
Fault("Page factory exception keeps host navigation working", originalManifest with { EntryType = "IndependentPlugin.BrokenPagePlugin" }, PluginState.Loaded);
Fault("Shared SDK DLL conflict rejected", originalManifest, PluginState.Failed, d => File.WriteAllText(Path.Combine(d, "Avalonia.Controls.dll"), "conflicting-sdk"));
Fault("Nested mixed-case SDK copy rejected", originalManifest, PluginState.Failed, d =>
    File.WriteAllText(Path.Combine(d, "Assets", "AVALONIA.CONTROLS.DLL"), "conflicting-sdk"));
Fault("Private Avalonia extension requires UI 1.6", originalManifest with { UiApiVersion = "1.5" }, PluginState.Failed,
    expectedFailure: "UI API 1.6");
Fault("Existing UI 1.5 plugin loads without a private Avalonia extension", originalManifest with { UiApiVersion = "1.5" }, PluginState.Loaded,
    d => File.Delete(Path.Combine(d, "Avalonia.TractorTool.FixtureExtension.dll")));
Fault("Missing private dependency reported", originalManifest, PluginState.Failed, RemovePrivateDependency, expectedFailure: missingPrivateDependency);
Fault("Win32, VisualBasic and XML implementation assemblies resolve from runtime", originalManifest with { EntryType = "IndependentPlugin.FrameworkReferencePlugin" }, PluginState.Loaded);
// Preload a real DLL and prove default name binding works; an emitted assembly is not a valid substitute.
var hostOnly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(fixtureDirectory, privateAssemblyName + ".dll"));
Assert(!hostOnly.IsDynamic);
Assert(System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(hostOnly) == System.Runtime.Loader.AssemblyLoadContext.Default);
Assert(ReferenceEquals(System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyName(hostOnly.GetName()), hostOnly));
Fault("Packaged System-prefixed dependency stays private even when the host has a copy", originalManifest with { EntryType = "IndependentPlugin.FrameworkReferencePlugin" }, PluginState.Loaded);
Fault("Missing System-prefixed private dependency cannot borrow the host copy", originalManifest, PluginState.Failed,
    RemovePrivateDependency, expectedFailure: missingPrivateDependency);
var globalResolverCalled = false;
ResolveEventHandler resolveMissing = (_, eventArgs) =>
{
    if (new System.Reflection.AssemblyName(eventArgs.Name).Name != privateAssemblyName) return null;
    globalResolverCalled = true;
    return hostOnly;
};
AppDomain.CurrentDomain.AssemblyResolve += resolveMissing;
try
{
    Fault("Missing private dependency cannot be supplied by a global resolver", originalManifest, PluginState.Failed,
        RemovePrivateDependency, expectedFailure: missingPrivateDependency);
    Check("Private dependency rejection does not invoke the global resolver", () => Assert(!globalResolverCalled));
}
finally { AppDomain.CurrentDomain.AssemblyResolve -= resolveMissing; }
Fault("Missing plugin icon does not prevent registration", originalManifest, PluginState.Loaded,
    d => File.Move(Path.Combine(d, "Assets", "icon.png"), Path.Combine(d, "Assets", "icon.missing")), expectIconFallback: true);
Check("One broken plugin does not prevent another plugin from loading", () =>
{
    var casePaths = new AppPaths(Path.Combine(output, "mixed-app"), Path.Combine(output, "mixed-data"));
    casePaths.EnsureDataDirectories();
    foreach (var id in new[] { "Test.Good", "Test.Bad" })
    {
        var destination = Path.Combine(casePaths.PluginRoot, id, originalManifest.Version);
        CopyTree(fixtureDirectory, destination);
        var manifest = originalManifest with { Id = id, EntryType = id == "Test.Bad" ? "IndependentPlugin.ThrowingPlugin" : originalManifest.EntryType };
        File.WriteAllText(Path.Combine(destination, "plugin.json"), JsonSerializer.Serialize(manifest, JsonOptions.Value));
    }
    var caseHost = new PluginHost(casePaths, compatibility, new AppLog(casePaths.LogRoot));
    caseHost.Discover([]); int registrations = 0;
    Pump(caseHost.LoadAsync((_, plugin) => { ((IPluginUiModule)plugin).RegisterPages(new PageRegistry("mixed")); registrations++; }));
    Assert(caseHost.Records.Count(r => r.State == PluginState.Loaded) == 1 && caseHost.Records.Count(r => r.State == PluginState.Failed) == 1 && registrations == 1);
    Pump(caseHost.DisposeAsync().AsTask());
});
bool HasMarker(string directory, string marker) => File.Exists(Path.Combine(directory, marker + ".marker"));
void Release(string directory, string stage) => File.WriteAllText(Path.Combine(directory, "release-" + stage + ".marker"), "");
async Task WaitForMarkerAsync(string directory, string marker)
{
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (!HasMarker(directory, marker))
    {
        if (DateTime.UtcNow >= deadline) throw new TimeoutException("Missing lifecycle marker: " + marker);
        await Task.Delay(10);
    }
}
Task LoadLifecyclePlugins(PluginHost caseHost, Action? onRegistered = null) => caseHost.LoadAsync((record, plugin) =>
{
    ((IPluginUiModule)plugin).RegisterPages(new PageRegistry(record.Manifest!.Id));
    onRegistered?.Invoke();
});
foreach (var exitDuringInitialization in new[] { false, true })
Check(exitDuringInitialization ? "Shutdown waits for initialization and skips its UI commit" :
    "Concurrent initialization shares one task and commits the delayed plugin only once", () =>
{
    var root = Path.Combine(output, "repeat-init-" + Guid.NewGuid().ToString("N"));
    var casePaths = new AppPaths(Path.Combine(root, "app"), Path.Combine(root, "data"));
    casePaths.EnsureDataDirectories();
    var pluginDirectory = Path.Combine(casePaths.PluginRoot, originalManifest.Id, originalManifest.Version);
    CopyTree(fixtureDirectory, pluginDirectory);
    File.WriteAllText(Path.Combine(pluginDirectory, "plugin.json"), JsonSerializer.Serialize(
        originalManifest with { EntryType = "IndependentPlugin.DelayedInitializationPlugin" }, JsonOptions.Value));
    var caseLog = new AppLog(casePaths.LogRoot);
    var caseHost = new PluginHost(casePaths, compatibility, caseLog);
    caseHost.Discover([]);
    var caseVm = new MainViewModel(casePaths, caseHost, caseLog,
        new SettingsViewModel(casePaths, new(Path.Combine(casePaths.ConfigRoot, "settings.json")), new(), caseLog), () => Task.CompletedTask);
    var directory = casePaths.GetPluginDataDirectory(originalManifest.Id);
    try
    {
        var first = caseVm.InitializeAsync();
        Pump(WaitForMarkerAsync(directory, "initialize-started"));
        var second = caseVm.InitializeAsync();
        Assert(ReferenceEquals(first, second) && !first.IsCompleted && !caseVm.Plugins.NoMatches);
        var shutdown = exitDuringInitialization ? caseVm.BeginShutdown() : Task.CompletedTask;
        var pluginShutdown = exitDuringInitialization ? caseHost.DisposeAsync().AsTask() : Task.CompletedTask;
        Assert(shutdown.IsCompleted != exitDuringInitialization);
        Release(directory, "initialize");
        Pump(Task.WhenAll(first, second, shutdown, pluginShutdown));
        Assert(ReferenceEquals(first, caseVm.InitializeAsync()));
        Assert(caseVm.Navigation.Count == (exitDuringInitialization ? 1 : 2) && caseVm.Settings.PluginSettings.Count == 0);
        Assert(caseVm.Home.InstalledPlugins.Count == (exitDuringInitialization ? 0 : 1));
    }
    finally { Directory.CreateDirectory(directory); Release(directory, "initialize"); caseVm.Dispose(); Pump(caseHost.DisposeAsync().AsTask()); }
});
void AssertCleaned(string directory, bool hadBackground = true)
{
    Assert(HasMarker(directory, "initialize-completed") && HasMarker(directory, "stop-completed") && HasMarker(directory, "disposed"));
    Assert(!HasMarker(directory, "disposed-too-early") && !HasMarker(directory, "stop-before-initialize-completed"));
    if (hadBackground) Assert(HasMarker(directory, "background-completed"));
}
void ShutdownCase(string name, (string Id, string EntryType)[] variants,
    Action<PluginHost, AppLog, Dictionary<string, string>> action)
{
    Check(name, () =>
    {
        var root = Path.Combine(output, "shutdown-" + Guid.NewGuid().ToString("N"));
        var casePaths = new AppPaths(Path.Combine(root, "app"), Path.Combine(root, "data"));
        casePaths.EnsureDataDirectories();
        var data = new Dictionary<string, string>();
        foreach (var variant in variants)
        {
            var directory = Path.Combine(casePaths.PluginRoot, variant.Id, originalManifest.Version);
            CopyTree(fixtureDirectory, directory);
            File.WriteAllText(Path.Combine(directory, "plugin.json"), JsonSerializer.Serialize(
                originalManifest with { Id = variant.Id, EntryType = "IndependentPlugin." + variant.EntryType }, JsonOptions.Value));
            data[variant.Id] = casePaths.GetPluginDataDirectory(variant.Id);
            Directory.CreateDirectory(data[variant.Id]);
        }
        var caseLog = new AppLog(casePaths.LogRoot);
        var caseHost = new PluginHost(casePaths, compatibility, caseLog);
        try { caseHost.Discover([]); action(caseHost, caseLog, data); }
        finally
        {
            // Failed assertions must still release only this case's fixture gates.
            foreach (var directory in data.Values)
                foreach (var stage in new[] { "initialize", "stop", "background" }) Release(directory, stage);
            Pump(caseHost.DisposeAsync().AsTask());
        }
    });
}
ShutdownCase("Independent plugin tray resources register, roll back and stop with their owner",
    [("Test.Tray", "ProbePlugin"), ("Test.BadTray", "BadTrayRegistrationPlugin")], (caseHost, caseLog, _) =>
    {
        using var tray = new TrayService(() => { }, () => Task.CompletedTask, caseLog, disabled: true);
        Pump(caseHost.LoadWithResourcesAsync(tray.Register));
        Assert(caseHost.Records.Single(r => r.Manifest!.Id == "Test.Tray").State == PluginState.Loaded);
        Assert(caseHost.Records.Single(r => r.Manifest!.Id == "Test.BadTray").State == PluginState.Failed);
        Assert(tray.Menu.Items.Count == 5);
        Pump(caseHost.DisposeAsync().AsTask()); Assert(tray.Menu.Items.Count == 3);
    });
ShutdownCase("Cancellation callback failure isolates cleanup and concurrent disposal shares its task",
    [("Test.ThrowCancel", "ThrowingCancellationPlugin"), ("Test.HealthyShutdown", "ShutdownProbePlugin")],
    (caseHost, caseLog, data) =>
    {
        Pump(LoadLifecyclePlugins(caseHost));
        Pump(Task.WhenAll(data.Values.Select(directory => WaitForMarkerAsync(directory, "background-started"))));
        var first = caseHost.DisposeAsync().AsTask();
        var second = caseHost.DisposeAsync().AsTask();
        Assert(ReferenceEquals(first, second));
        Pump(first);
        foreach (var directory in data.Values) AssertCleaned(directory);
        Assert(HasMarker(data["Test.ThrowCancel"], "cancellation-callback"));
        Assert(caseLog.ReadRecent().Contains("fixture cancellation callback failed"));
    });
ShutdownCase("Slow stop and background cleanup never dispose a live plugin and complete after release",
    [("Test.SlowStop", "DelayedStopPlugin"), ("Test.SlowBackground", "DelayedBackgroundPlugin")],
    (caseHost, caseLog, data) =>
    {
        Pump(LoadLifecyclePlugins(caseHost));
        Pump(Task.WhenAll(data.Values.Select(directory => WaitForMarkerAsync(directory, "background-started"))));
        var shutdown = caseHost.DisposeAsync().AsTask();
        Pump(WaitForMarkerAsync(data["Test.SlowStop"], "stop-started"));
        Pump(WaitForMarkerAsync(data["Test.SlowBackground"], "background-stopping"));
        Pump(Task.Delay(3400)); // Cross the real host's 3-second stage deadline while both gates remain held.
        Assert(!shutdown.IsCompleted);
        foreach (var directory in data.Values) Assert(!HasMarker(directory, "disposed"));
        Assert(!HasMarker(data["Test.SlowStop"], "stop-completed"));
        Assert(!HasMarker(data["Test.SlowBackground"], "background-completed"));
        Assert(caseLog.ReadRecent().Contains("停止插件超时") && caseLog.ReadRecent().Contains("等待常驻活动结束超时"));
        Release(data["Test.SlowStop"], "stop");
        Release(data["Test.SlowBackground"], "background");
        Pump(shutdown);
        foreach (var directory in data.Values) AssertCleaned(directory);
    });
ShutdownCase("Initialization completing during shutdown cannot register pages and is cleaned up afterwards",
    [("Test.LateInitialize", "DelayedInitializationPlugin")], (caseHost, caseLog, data) =>
    {
        var directory = data["Test.LateInitialize"];
        var registrations = 0;
        var loading = LoadLifecyclePlugins(caseHost, () => registrations++);
        Pump(WaitForMarkerAsync(directory, "initialize-started"));
        var shutdown = caseHost.DisposeAsync().AsTask();
        Pump(Task.Delay(3400));
        Assert(!HasMarker(directory, "stop-started") && !HasMarker(directory, "disposed") && registrations == 0);
        Assert(caseLog.ReadRecent().Contains("等待初始化结束超时"));
        Release(directory, "initialize");
        Pump(Task.WhenAll(loading, shutdown));
        AssertCleaned(directory, hadBackground: false);
        Assert(registrations == 0 && !HasMarker(directory, "registered") && !HasMarker(directory, "background-started"));
        Assert(caseHost.Records.Single().State != PluginState.Loaded);
    });
Check("Settings shutdown waits for the save and prevents another write", () =>
{
    var casePaths = new AppPaths(Path.Combine(output, "settings-shutdown-app"), Path.Combine(output, "settings-shutdown-data"));
    casePaths.EnsureDataDirectories();
    var store = new JsonFileStore<HostSettings>(Path.Combine(casePaths.ConfigRoot, "settings.json"), HostSettings.Validate);
    var settings = new SettingsViewModel(casePaths, store, new(), new AppLog(casePaths.LogRoot));
    settings.SelectedTheme = settings.Themes.Single(t => t.Id == "Dark");
    var execution = settings.SaveCompletion;
    var shutdown = settings.BeginShutdown();
    Assert(!settings.RetrySaveCommand.CanExecute(null));
    Pump(shutdown);
    Assert(execution.IsCompletedSuccessfully && store.Load().Theme == "Dark");
    var before = File.ReadAllBytes(store.FilePath);
    settings.SelectedTheme = settings.Themes.Single(t => t.Id == "Light");
    // Direct command invocation and property callbacks must respect the shutdown latch.
    settings.RetrySaveCommand.Execute(null);
    Pump(settings.SaveCompletion);
    Assert(File.ReadAllBytes(store.FilePath).SequenceEqual(before) && store.Load().Theme == "Dark");
    Assert(Application.Current!.RequestedThemeVariant == ThemeVariant.Dark);
});
Check("Dependency settings separate installed packages and persistent GitHub sources", () =>
{
    using var http = new HttpClient();
    var runner = new ProcessRunner("PackageSettings.Check", log, _ => throw new InvalidOperationException("Browsing must not start a tool"));
    var service = new ExternalPackageService(Path.Combine(output, "github-ui-packages"), "win-x64", http, runner, log);
    service.UpdateReferences(new([], true));
    using var model = new ExternalToolsViewModel(service, _ => { });
    try
    {
        Assert(model.IsEmpty && model.Sources.Count == 3 && model.Sources.All(s => s.IsDefault));
        Assert(!model.RemoveSourceCommand.CanExecute(model.Sources[0]));
        model.SourceName = "Team"; model.SourceAddress = "https://github.example.com/api/v3/";
        Pump(model.AddSourceCommand.ExecuteAsync(null)); Dispatcher.UIThread.RunJobs();
        Assert(model.Sources.Count == 4 && model.Error.Length == 0 && model.IsEmpty);
        var source = model.Sources.Single(s => !s.IsDefault);
        Pump(model.RemoveSourceCommand.ExecuteAsync(source)); Dispatcher.UIThread.RunJobs();
        Assert(model.Sources.Count == 3 && model.IsEmpty);
        model.Dispose(); Assert(!model.AddSourceCommand.CanExecute(null));
    }
    finally { model.Dispose(); Pump(service.DisposeAsync().AsTask()); Pump(runner.DisposeAsync().AsTask()); }
});
AppearanceChecks.Run(Check, Pump, output);
HostFailureChecks.Run(Check, Pump, output);
PluginLifecycleChecks.Run(Check, Pump, output, fixtureDirectory, compatibility);
PackageCategoryChecks.Run(Check, Pump, output, log);
Check("First dependency transfer appears in settings and can be cancelled", () =>
{
    using var pending = new PendingPackageDownload();
    using var http = new HttpClient(pending);
    var runner = new ProcessRunner("PackageSettings.Check", log, _ => throw new InvalidOperationException());
    var key = new TractorTool.Contracts.Packages.PackageReference("FirstDownload", "1.0.0");
    var endpoint = new Uri("https://example.invalid/package");
    var definition = new PackageDefinition(key, "win-x64", "Fixture", "", endpoint, new string('a', 64), "Fixture",
        endpoint, new Dictionary<string, PackageTool> { ["tool"] = new("tool.exe", "1.0") });
    var service = new ExternalPackageService(Path.Combine(output, "first-ui-download"), "win-x64", http, runner, log, [definition]);
    service.UpdateReferences(new([], true));
    using var model = new ExternalToolsViewModel(service, _ => { });
    try
    {
        var install = service.InstallAsync(key);
        Pump(pending.Entered.Task);
        var card = model.VisibleCards.Single();
        Assert(!card.IsInstalled && card.IsBusy && card.CancelCommand.CanExecute(null));
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.VisibleCards.CollectionChanged += (_, _) => { if (model.IsEmpty) removed.TrySetResult(); };
        card.CancelCommand.Execute(null);
        try { Pump(install); } catch (OperationCanceledException) { }
        Pump(removed.Task);
        Assert(install.IsCanceled && model.IsEmpty && !card.CancelCommand.CanExecute(null));
    }
    finally { model.Dispose(); Pump(service.DisposeAsync().AsTask()); Pump(runner.DisposeAsync().AsTask()); }
});
MotionChecks.Run(Check);
TaskUiChecks.Run(Check, Pump, output);
LogViewChecks.Run(Check, output);
IconChecks.Run(Check, published, output);
PluginManagementUiChecks.Run(Check, Pump, published, output);
TrayMenuChecks.Run(Check, Pump, output);
Console.WriteLine($"RESULT passed={passed} failed={failed}; publishedHost={published}; evidence={output}");
return failed == 0 ? 0 : 1;

file sealed class PendingPackageDownload : HttpMessageHandler
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Entered.TrySetResult();
        await Task.Delay(Timeout.Infinite, token);
        throw new InvalidOperationException("Expected download cancellation");
    }
}
