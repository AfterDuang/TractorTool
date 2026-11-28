using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.Input;
using TractorTool;
using TractorTool.Core.Logging;
using TractorTool.Core.Plugins;
using TractorTool.Core.Storage;
using TractorTool.Core.Tasks;
using TractorTool.UI;
using TractorTool.ViewModels;

internal static class HostFailureChecks
{
    public static void Run(Action<string, Action> check, Action<Task> pump, string output)
    {
        foreach (var accepted in new[] { false, true })
        check($"History cleanup {(accepted ? "queues" : "declines")} the exact confirmed versions including manually deleted history", () =>
        {
            var (paths, manager, host, vm) = CreateHistoryCleanup(output);
            using (manager)
            try
            {
                string? preview = null;
                vm.ConfirmAsync = message => { preview = message; return Task.FromResult(accepted); };
                Require(vm.CleanHistoryCommand.CanExecute(null));
                pump(vm.CleanHistoryCommand.ExecuteAsync(null));
                Require(preview?.Contains("历史版本测试：1.0.0、1.1.0") == true &&
                    preview.Contains("重启后") && preview.Contains("无法回退") &&
                    preview.Contains("当前版本、插件数据、恢复快照和外部依赖均保留"),
                    "Confirmation must name the exact old versions and explain the cleanup boundary.");
                var item = manager.Snapshot.Single();
                Require(item.ActiveVersion == "2.0.0" && item.Recovery?.Version == "1.0.0" &&
                    Directory.Exists(Path.Combine(paths.PluginRoot, item.Id, "1.1.0")),
                    "Preview and queuing must not delete code or clear the recovery record before restart.");
                if (accepted)
                {
                    Require(item.Pending is { Kind: PluginChangeKind.CleanHistory, DestructiveStarted: false } pending &&
                        pending.HistoryVersions!.SequenceEqual(new[] { "1.0.0", "1.1.0" }));
                    Require(vm.Items.Single().PendingText.Contains("1.0.0、1.1.0") &&
                        vm.Items.Single().CancelChangeCommand is not null,
                        "Queued history cleanup must show its versions and remain cancellable before restart.");
                }
                else Require(item.Pending is null, "Declining the preview must not queue a cleanup.");
            }
            finally { pump(host.DisposeAsync().AsTask()); }
        });

        check("History cleanup confirmation arriving after shutdown never queues a change", () =>
        {
            var (_, manager, host, vm) = CreateHistoryCleanup(output);
            using (manager)
            {
                var prompted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Task operation = Task.CompletedTask, shutdown = Task.CompletedTask;
                try
                {
                    vm.ConfirmAsync = _ => { prompted.SetResult(); return answer.Task; };
                    operation = vm.CleanHistoryCommand.ExecuteAsync(null);
                    pump(prompted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
                    shutdown = vm.BeginShutdown();
                    Require(!shutdown.IsCompleted && !vm.CleanHistoryCommand.CanExecute(null));
                    answer.SetResult(true);
                    pump(Task.WhenAll(operation, shutdown));
                    Require(manager.Snapshot.Single().Pending is null && !vm.CleanHistoryCommand.CanExecute(null),
                        "Shutdown must wait for the prompt without accepting its late confirmation.");
                }
                finally
                {
                    answer.TrySetResult(false);
                    pump(Task.WhenAll(operation, shutdown));
                    pump(host.DisposeAsync().AsTask());
                }
            }
        });

        check("Discovery completion resets after startup or directory failure and recovers on the next scan", () =>
        {
            var root = Path.Combine(output, "discovery-completion-" + Guid.NewGuid().ToString("N"));
            var paths = new AppPaths(Path.Combine(root, "app"), Path.Combine(root, "data"));
            paths.EnsureDataDirectories();
            var host = new PluginHost(paths, new("windows", "win-x64", UiApi.Version, UiApi.AvaloniaVersion), new AppLog(paths.LogRoot));
            try
            {
                Require(!host.DiscoveryCompleted);
                host.Discover([]);
                Require(host.DiscoveryCompleted && host.DirectoryError is null);
                host.Discover([], startupError: "fixture management unavailable");
                Require(!host.DiscoveryCompleted && host.DirectoryError is not null);
                host.Discover([]);
                Require(host.DiscoveryCompleted);
                Directory.Delete(paths.PluginRoot);
                File.WriteAllText(paths.PluginRoot, "fixture directory blocker");
                host.Discover([]);
                Require(!host.DiscoveryCompleted && host.DirectoryError is not null && host.Records.Count == 0);
                File.Delete(paths.PluginRoot);
                host.Discover([]);
                Require(host.DiscoveryCompleted && host.DirectoryError is null);
            }
            finally { pump(host.DisposeAsync().AsTask()); }
        });

        check("A completed discovery with an installation warning still persists actual load failures", () =>
        {
            var root = Path.Combine(output, "discovery-warning-" + Guid.NewGuid().ToString("N"));
            var paths = new AppPaths(Path.Combine(root, "app"), Path.Combine(root, "data"));
            paths.EnsureDataDirectories();
            var item = new ManagedPlugin { Id = "Checks.LoadFailure", ActiveVersion = "1.0.0", AwaitingLoad = true };
            var directory = Path.Combine(paths.PluginRoot, item.Id, item.ActiveVersion!);
            Directory.CreateDirectory(directory);
            var manifest = new PluginManifest { SchemaVersion = 1, Id = item.Id, Name = item.Id, Version = item.ActiveVersion!,
                EntryAssembly = "Missing.dll", EntryType = "Fixture.Plugin", ApiVersion = "1.0", UiApiVersion = UiApi.Version,
                AvaloniaVersion = UiApi.AvaloniaVersion, SupportedPlatforms = ["win-x64"] };
            File.WriteAllText(Path.Combine(directory, "plugin.json"), JsonSerializer.Serialize(manifest, JsonOptions.Value));
            var store = new JsonFileStore<PluginManagementState>(Path.Combine(paths.PluginRoot, ".management", "state.json"), PluginManagementState.Validate);
            store.Save(new() { Plugins = [item] });
            var compatibility = new HostCompatibility("windows", "win-x64", UiApi.Version, UiApi.AvaloniaVersion);
            using var manager = new PluginManager(paths, compatibility);
            var log = new AppLog(paths.LogRoot);
            var host = new PluginHost(paths, compatibility, log);
            const string warning = "fixture plugin directory is not writable";
            // Inject only the write-probe diagnostic after a real scan; do not alter machine ACLs or load results.
            void ReportWriteWarning()
            {
                if (host.DiscoveryCompleted)
                    typeof(PluginHost).GetProperty(nameof(PluginHost.DirectoryError))!.SetValue(host, warning);
            }
            log.Changed += ReportWriteWarning;
            var settings = new SettingsViewModel(paths, new(Path.Combine(paths.ConfigRoot, "settings.json")), new(), log, manager: manager);
            using var vm = new MainViewModel(paths, host, log, settings, () => Task.CompletedTask, manager);
            try
            {
                pump(vm.InitializeAsync());
                Require(vm.Plugins.ManagementAvailable && host.DiscoveryCompleted && host.DirectoryError == warning);
                var record = host.Records.Single();
                Require(record.State == PluginState.Failed && record.Detail.Contains("找不到入口程序集"));
                Require(manager.Snapshot.Single().Error.Contains(record.Detail) && store.Load().Plugins.Single() == manager.Snapshot.Single(),
                    "An installation-only directory warning must not suppress the actual load outcome.");
            }
            finally { log.Changed -= ReportWriteWarning; vm.Dispose(); pump(host.DisposeAsync().AsTask()); }
        });

        check("Management startup failure does not persist false load failures after writes recover", () =>
        {
            var root = Path.Combine(output, "load-management-failure-" + Guid.NewGuid().ToString("N"));
            var paths = new AppPaths(Path.Combine(root, "app"), Path.Combine(root, "data"));
            paths.EnsureDataDirectories();
            var healthy = new ManagedPlugin { Id = "Checks.Healthy", ActiveVersion = "1.0.0", AwaitingLoad = true };
            var pending = new ManagedPlugin { Id = "Checks.Pending", ActiveVersion = "1.0.0",
                Pending = new(PluginChangeKind.Disable, Guid.NewGuid().ToString("N"), paths.DataRoot) };
            var store = new JsonFileStore<PluginManagementState>(Path.Combine(paths.PluginRoot, ".management", "state.json"), PluginManagementState.Validate);
            store.Save(new() { Plugins = [healthy, pending] });
            var before = File.ReadAllBytes(store.FilePath);
            var compatibility = new HostCompatibility("windows", "win-x64", UiApi.Version, UiApi.AvaloniaVersion);
            using var manager = new PluginManager(paths, compatibility);
            var writes = 0;
            void FailPendingWrites()
            {
                // Both ApplyPending writes fail; a later erroneous RecordLoadResults write would succeed.
                if (++writes <= 2) throw new IOException("fixture pending state write failure");
            }
            typeof(PluginManager).GetEvent("StateChanging", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetAddMethod(true)!.Invoke(manager, [(Action)FailPendingWrites]);
            var log = new AppLog(paths.LogRoot);
            var host = new PluginHost(paths, compatibility, log);
            var settings = new SettingsViewModel(paths, new(Path.Combine(paths.ConfigRoot, "settings.json")), new(), log, manager: manager);
            using var vm = new MainViewModel(paths, host, log, settings, () => Task.CompletedTask, manager);
            try
            {
                pump(vm.InitializeAsync());
                Require(!vm.Plugins.ManagementAvailable && !vm.Plugins.IsLoading &&
                    !host.DiscoveryCompleted && host.DirectoryError?.Contains("fixture pending state write failure") == true && host.Records.Count == 0);
                Require(writes == 2 && manager.Snapshot.SequenceEqual(new[] { healthy, pending }) &&
                    File.ReadAllBytes(store.FilePath).SequenceEqual(before),
                    "A failed management startup must leave unattempted load records and the journal unchanged.");
            }
            finally { vm.Dispose(); pump(host.DisposeAsync().AsTask()); }
        });

        check("Load results record success and real failures once while preserving disabled and previous failures", () =>
        {
            var root = Path.Combine(output, "load-results-" + Guid.NewGuid().ToString("N"));
            var paths = new AppPaths(Path.Combine(root, "app"), Path.Combine(root, "data"));
            paths.EnsureDataDirectories();
            var loaded = new ManagedPlugin { Id = "Checks.Loaded", ActiveVersion = "1.0.0", AwaitingLoad = true };
            var failed = loaded with { Id = "Checks.Failed" };
            var missing = loaded with { Id = "Checks.Missing" };
            var disabled = loaded with { Id = "Checks.Disabled", Enabled = false };
            var previous = loaded with { Id = "Checks.Previous", Error = "previous initialization failure" };
            var store = new JsonFileStore<PluginManagementState>(Path.Combine(paths.PluginRoot, ".management", "state.json"), PluginManagementState.Validate);
            store.Save(new() { Plugins = [loaded, failed, missing, disabled, previous] });
            using var manager = new PluginManager(paths, new("windows", "win-x64", UiApi.Version, UiApi.AvaloniaVersion));
            PluginRecord Record(ManagedPlugin item, PluginState state, string detail = "") =>
                new("fixture", new PluginManifest { Id = item.Id, Version = item.ActiveVersion! }, state, detail);
            PluginRecord[] records = [Record(loaded, PluginState.Loaded), Record(failed, PluginState.Failed, "fixture initialization failure"),
                Record(disabled, PluginState.Disabled), Record(previous, PluginState.Failed, previous.Error)];
            manager.RecordLoadResults(records);
            var snapshot = manager.Snapshot;
            Require(snapshot.Single(p => p.Id == loaded.Id) == (loaded with { AwaitingLoad = false }));
            Require(snapshot.Single(p => p.Id == failed.Id).Error.Contains("fixture initialization failure") &&
                snapshot.Single(p => p.Id == missing.Id).Error.Contains("找不到活动版本"));
            Require(snapshot.Single(p => p.Id == previous.Id) == previous && snapshot.Single(p => p.Id == disabled.Id) == disabled);
            var saved = File.ReadAllBytes(store.FilePath);
            manager.RecordLoadResults(records);
            Require(manager.Snapshot.SequenceEqual(snapshot) && File.ReadAllBytes(store.FilePath).SequenceEqual(saved),
                "Records skipped on the next startup must not acquire another error prefix or rewrite the journal.");
        });

        check("Initialization failure ends loading and tray fallback preserves the failure notice", () =>
        {
            var (paths, log, host, vm) = Create(output);
            var failOnce = true;
            vm.Home.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(vm.Home.Summary) || !failOnce) return;
                failOnce = false;
                throw new InvalidOperationException("fixture catalog failure");
            };
            try
            {
                try { pump(vm.InitializeAsync()); throw new Exception("Expected initialization failure."); }
                catch (InvalidOperationException ex) when (ex.Message == "fixture catalog failure") { }
                Require(!vm.Home.IsLoading && !vm.Plugins.IsLoading && vm.Notice.Contains("fixture catalog failure"));
                var notice = vm.Notice;
                vm.NotifyTrayUnavailable();
                Require(vm.Notice == notice && vm.Home.DirectoryError == notice);
            }
            finally { vm.Dispose(); pump(host.DisposeAsync().AsTask()); }
        });

        check("Exit drains other services and shuts down after a synchronous workspace cleanup failure", () =>
        {
            var (paths, log, host, vm) = Create(output);
            var tasks = new BackgroundTaskService(Path.Combine(paths.DataRoot, "tasks.json"), log);
            using var handler = new DisposalProbe();
            using var http = new HttpClient(handler);
            var desktop = DispatchProxy.Create<IClassicDesktopStyleApplicationLifetime, LifetimeProbe>();
            var lifetime = (LifetimeProbe)(object)desktop;
            // Exercise only App's shutdown coordinator, without initializing another Application or native window.
            var app = (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
            Set(app, "_vm", vm); Set(app, "_plugins", host); Set(app, "_log", log);
            Set(app, "_tasks", tasks); Set(app, "_packageHttp", http);
            Set(app, "_desktop", desktop); Set(app, "_ready", true);
            var cleared = 0;
            vm.AttachPagePresenter(_ => true, () => cleared++);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(vm.CurrentPage) && vm.CurrentPage is null)
                    throw new InvalidOperationException("fixture detach failure");
            };
            try
            {
                pump(Invoke(app, "ExitCoreAsync", true));
                Require(lifetime.Shutdowns == 1 && lifetime.ExitCode == 0 && handler.Disposed && cleared == 1);
                Require(log.Snapshot().Entries.Any(e => e.Message == "退出清理失败或超时"));
                try { tasks.CreateScope("Test.AfterShutdown"); throw new Exception("Task service remained open."); }
                catch (ObjectDisposedException) { }
                pump(Invoke(app, "ExitCoreAsync", true));
                Require(lifetime.Shutdowns == 1);
            }
            finally { vm.Dispose(); pump(host.DisposeAsync().AsTask()); pump(tasks.BeginShutdownAsync()); }
        });

        check("Settings notification failure still waits for active settings and migration writes", () =>
        {
            using var started = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var migration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blockWrite = false;
            var (paths, log, host, vm) = Create(output, _ =>
            {
                if (!blockWrite) return;
                started.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("fixture settings write blocked");
            });
            Task? shutdown = null;
            try
            {
                blockWrite = true;
                vm.Settings.SelectedTheme = vm.Settings.Themes.Single(t => t.Id == "Dark");
                Require(started.Wait(TimeSpan.FromSeconds(5)));
                typeof(DataMigrationViewModel).GetField("_operation", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(vm.Settings.Migration, migration.Task);
                var failOnce = true;
                vm.Settings.RetrySaveCommand.CanExecuteChanged += (_, _) =>
                {
                    if (!failOnce) return;
                    failOnce = false;
                    throw new InvalidOperationException("fixture settings notification");
                };
                shutdown = vm.BeginShutdown();
                Require(!shutdown.IsCompleted && !vm.Settings.Migration.CanChoose);
                release.Set();
                pump(vm.Settings.SaveCompletion);
                Require(!shutdown.IsCompleted);
                migration.SetResult();
                try { pump(shutdown); throw new Exception("Expected shutdown notification failure."); }
                catch (InvalidOperationException ex) when (ex.Message == "fixture settings notification") { }
                Require(new JsonFileStore<HostSettings>(Path.Combine(paths.ConfigRoot, "settings.json")).Load().Theme == "Dark");
            }
            finally
            {
                release.Set(); migration.TrySetResult();
                if (shutdown is not null) { try { pump(shutdown); } catch (InvalidOperationException) { } }
                vm.Dispose(); pump(host.DisposeAsync().AsTask());
            }
        });

        check("Partial startup cleanup disposes constructed services once without requiring a workspace", () =>
        {
            using var handler = new DisposalProbe();
            using var http = new HttpClient(handler);
            var app = (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
            Set(app, "_packageHttp", http);
            var first = Invoke(app, "StopServicesAsync");
            pump(first);
            Require(handler.Disposed && ReferenceEquals(first, Invoke(app, "StopServicesAsync")));
        });
    }

    private static (AppPaths, PluginManager, PluginHost, PluginsViewModel) CreateHistoryCleanup(string output)
    {
        var root = Path.Combine(output, "history-cleanup-ui-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(Path.Combine(root, "app"), Path.Combine(root, "data"));
        paths.EnsureDataDirectories();
        var item = new ManagedPlugin { Id = "Checks.History", Name = "历史版本测试", ActiveVersion = "2.0.0",
            Recovery = new("1.0.0", Guid.NewGuid().ToString("N"), paths.DataRoot, false) };
        // The recovery version was manually deleted; another obsolete version still has its code.
        foreach (var version in new[] { "1.1.0", "2.0.0" })
        {
            var directory = Path.Combine(paths.PluginRoot, item.Id, version);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "plugin.json"), JsonSerializer.Serialize(new { id = item.Id, version }));
        }
        new JsonFileStore<PluginManagementState>(Path.Combine(paths.PluginRoot, ".management", "state.json"), PluginManagementState.Validate)
            .Save(new() { Plugins = [item] });
        var compatibility = new HostCompatibility("windows", "win-x64", UiApi.Version, UiApi.AvaloniaVersion);
        var manager = new PluginManager(paths, compatibility);
        var log = new AppLog(paths.LogRoot);
        var host = new PluginHost(paths, compatibility, log);
        host.Records.Add(new(Path.Combine(paths.PluginRoot, item.Id, item.ActiveVersion),
            new PluginManifest { Id = item.Id, Name = item.Name, Version = item.ActiveVersion }, PluginState.Loaded, "State fixture"));
        var vm = new PluginsViewModel(paths, host, new RelayCommand(() => { }), manager, log);
        vm.Update([], []);
        return (paths, manager, host, vm);
    }

    private static (AppPaths, AppLog, PluginHost, MainViewModel) Create(string output, Action<HostSettings>? validate = null)
    {
        var root = Path.Combine(output, "host-failure-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(Path.Combine(root, "app"), Path.Combine(root, "data"));
        paths.EnsureDataDirectories();
        var log = new AppLog(paths.LogRoot);
        var host = new PluginHost(paths, new("windows", "win-x64", UiApi.Version, UiApi.AvaloniaVersion), log);
        host.Discover([]);
        var store = new JsonFileStore<HostSettings>(Path.Combine(paths.ConfigRoot, "settings.json"), value =>
        {
            HostSettings.Validate(value);
            validate?.Invoke(value);
        });
        var vm = new MainViewModel(paths, host, log, new(paths, store, store.Load(), log), () => Task.CompletedTask);
        return (paths, log, host, vm);
    }

    private static void Set(App app, string field, object value) =>
        typeof(App).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, value);
    private static Task Invoke(App app, string method, params object[] arguments) =>
        (Task)typeof(App).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, arguments)!;
    private static void Require(bool value, string message = "Assertion failed") { if (!value) throw new InvalidOperationException(message); }

    private sealed class DisposalProbe : HttpMessageHandler
    {
        public bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new InvalidOperationException("Fixture must not perform network requests.");
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}

public class LifetimeProbe : DispatchProxy
{
    public int Shutdowns { get; private set; }
    public int ExitCode { get; private set; }
    protected override object? Invoke(MethodInfo? method, object?[]? arguments)
    {
        if (method!.Name == nameof(IClassicDesktopStyleApplicationLifetime.Shutdown))
        {
            Shutdowns++;
            ExitCode = (int)arguments![0]!;
            return null;
        }
        if (method.Name == "get_MainWindow") return null;
        throw new NotSupportedException(method.Name);
    }
}
