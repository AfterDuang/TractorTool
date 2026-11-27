using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Controls.ApplicationLifetimes;
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
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("Assertion failed"); }

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
