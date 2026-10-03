using System.IO.Compression;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using TractorTool.Contracts;
using TractorTool.Core.Logging;
using TractorTool.Core.Plugins;
using TractorTool.Core.Storage;
using TractorTool.UI;
using TractorTool.ViewModels;
using TractorTool.Views;

internal static class PluginManagementUiChecks
{
    public static void Run(Action<string, Action> check, Action<Task> pump, string output)
    {
        var compatibility = new HostCompatibility("windows", "win-x64", UiApi.Version, UiApi.AvaloniaVersion);
        AppPaths Paths()
        {
            var root = Path.Combine(output, "management-ui-" + Guid.NewGuid().ToString("N"));
            var paths = new AppPaths(Path.Combine(root, "app"), Path.Combine(root, "profile"));
            paths.EnsureDataDirectories(); return paths;
        }
        PluginManifest Manifest() => new() {
            SchemaVersion = 1, Id = "Checks.Ui", Name = "UI fixture", Version = "1.0.0", EntryAssembly = "Fixture.dll",
            EntryType = "NotExecuted", ApiVersion = "1.0", UiApiVersion = UiApi.Version,
            AvaloniaVersion = UiApi.AvaloniaVersion, SupportedPlatforms = ["windows"] };
        PluginsViewModel Model(AppPaths paths, PluginManager manager, bool? enabled = null, PluginState? state = null)
        {
            var log = new AppLog(paths.LogRoot);
            var host = new PluginHost(paths, compatibility, log);
            // Explicit management state fixture; does not load any DLL or claim plugin integration.
            if (enabled is { } active) host.Records.Add(new(Path.Combine(paths.PluginRoot, "Checks.Ui", "1.0.0"),
                Manifest(), state ?? (active ? PluginState.Loaded : PluginState.Disabled), "UI fixture"));
            var vm = new PluginsViewModel(paths, host, new RelayCommand(() => { }), manager, log);
            vm.Update([], []); return vm;
        }
        void Assert(bool value) { if (!value) throw new Exception("Plugin management UI assertion failed"); }
        string Archive(AppPaths paths)
        {
            var file = Path.Combine(paths.DataRoot, "fixture.zip");
            using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
            using (var json = zip.CreateEntry("plugin.json").Open()) JsonSerializer.Serialize(json, Manifest(), JsonOptions.Value);
            zip.CreateEntryFromFile(typeof(IPlugin).Assembly.Location, "Fixture.dll");
            return file;
        }
        check("Picker and dropped archive both require confirmation and share ZIP validation", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility);
            var vm = Model(paths, manager); var file = Archive(paths); int confirmations = 0;
            vm.PickArchiveAsync = () => Task.FromResult<string?>(file);
            vm.ConfirmAsync = _ => { confirmations++; return Task.FromResult(false); };
            pump(vm.InstallCommand.ExecuteAsync(null)); pump(vm.InstallArchiveAsync(file));
            Assert(confirmations == 2 && manager.Snapshot.Count == 0);
            vm.ConfirmAsync = _ => Task.FromResult(true);
            pump(vm.InstallArchiveAsync(file));
            Assert(manager.Snapshot.Single().Pending?.Kind == PluginChangeKind.Activate);
            Assert(!Directory.Exists(Path.Combine(paths.PluginRoot, "Checks.Ui")));
            pump(vm.BeginShutdown());
        });
        check("Dropped archives reject non-ZIP paths before confirmation", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility);
            var vm = Model(paths, manager); int confirmations = 0;
            vm.ConfirmAsync = _ => { confirmations++; return Task.FromResult(true); };
            pump(vm.InstallArchiveAsync("relative.zip")); pump(vm.InstallArchiveAsync(Path.Combine(paths.DataRoot, "file.dll")));
            Assert(confirmations == 0 && manager.Snapshot.Count == 0 && vm.HasOperationStatus);
        });
        check("Repeated drop and shutdown cannot enqueue a second install while confirmation is open", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility);
            var vm = Model(paths, manager); var file = Archive(paths);
            var decision = new TaskCompletionSource<bool>(); int confirmations = 0;
            vm.ConfirmAsync = _ => { confirmations++; return decision.Task; };
            var first = vm.InstallArchiveAsync(file); pump(vm.InstallArchiveAsync(file));
            var stopping = vm.BeginShutdown(); Assert(!stopping.IsCompleted && confirmations == 1);
            decision.SetResult(true); pump(first); pump(stopping); pump(vm.InstallArchiveAsync(file));
            Assert(manager.Snapshot.Count == 0 && !vm.CanInstall);
        });
        foreach (var enabled in new[] { true, false })
            check("Toggle initial binding is inert; direct thumb changes queue and reverse pending " + enabled, () =>
            {
                var paths = Paths(); using var manager = new PluginManager(paths, compatibility);
                var vm = Model(paths, manager, enabled);
                var view = new PluginsView { DataContext = vm }; var window = new Window { Content = view, Width = 900, Height = 700 };
                try
                {
                    window.Show(); window.UpdateLayout();
                    Assert(manager.Snapshot.Count == 0);
                    var command = (IAsyncRelayCommand)vm.Items.Single().ToggleCommand!;
                    var toggle = view.GetVisualDescendants().OfType<ToggleSwitch>().Single();
                    // Like a thumb drag: bypass Click/Command and change IsChecked directly.
                    toggle.SetCurrentValue(ToggleSwitch.IsCheckedProperty, (bool?)!enabled);
                    pump(command.ExecutionTask ?? throw new Exception("Thumb did not enqueue operation")); window.UpdateLayout();
                    Assert(manager.Snapshot.Single().Pending?.Kind == (enabled ? PluginChangeKind.Disable : PluginChangeKind.Enable));
                    Assert(vm.Items.Single().RequestedEnabled == !enabled && !vm.Items.Single().CanManage);
                    command = (IAsyncRelayCommand)vm.Items.Single().ToggleCommand!;
                    view.GetVisualDescendants().OfType<ToggleSwitch>().Single().SetCurrentValue(ToggleSwitch.IsCheckedProperty, (bool?)enabled);
                    pump(command.ExecutionTask ?? throw new Exception("Reverse thumb did not cancel operation"));
                    Assert(manager.Snapshot.Single().Pending is null && vm.Items.Single().RequestedEnabled == enabled);
                }
                finally { window.Close(); }
            });
        check("Toggle write failure restores checked state without falsely scheduling a change", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility);
            var vm = Model(paths, manager, true);
            Directory.CreateDirectory(Path.Combine(paths.PluginRoot, ".management", "state.json")); // Isolated write obstacle.
            var view = new PluginsView { DataContext = vm }; var window = new Window { Content = view, Width = 900, Height = 700 };
            try
            {
                window.Show(); window.UpdateLayout(); var command = (IAsyncRelayCommand)vm.Items.Single().ToggleCommand!;
                view.GetVisualDescendants().OfType<ToggleSwitch>().Single().SetCurrentValue(ToggleSwitch.IsCheckedProperty, false);
                pump(command.ExecutionTask ?? throw new Exception("No write attempted")); window.UpdateLayout();
                Assert(manager.Snapshot.Count == 0 && vm.OperationStatus.Contains("失败") && vm.Items.Single().RequestedEnabled);
                Assert(view.GetVisualDescendants().OfType<ToggleSwitch>().Single().IsChecked == true);
            }
            finally { window.Close(); }
        });
        check("A failed display state does not replace the installation's stored enabled preference", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility);
            var vm = Model(paths, manager, true);
            pump(((IAsyncRelayCommand)vm.Items.Single().ToggleCommand!).ExecuteAsync(null));
            manager.ApplyPending(); Assert(!manager.Snapshot.Single().Enabled);
            vm = Model(paths, manager, true, PluginState.Failed);
            Assert(!vm.Items.Single().RequestedEnabled);
            pump(((IAsyncRelayCommand)vm.Items.Single().ToggleCommand!).ExecuteAsync(null));
            Assert(manager.Snapshot.Single().Pending?.Kind == PluginChangeKind.Enable);
        });
    }
}
