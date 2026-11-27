using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using TractorTool.Core.Logging;
using TractorTool.Core.Plugins;
using TractorTool.Core.Storage;
using TractorTool.Core.Packages;
using TractorTool.Core.Packages.Python;
using TractorTool.Core.Packages.Node;
using TractorTool.Core.Processes;
using TractorTool.Core.Tasks;
using TractorTool.Contracts;
using TractorTool.Contracts.Tasks;
using System.Net.Http;
using TractorTool.Platform;
using TractorTool.Services;
using TractorTool.UI;
using TractorTool.ViewModels;
using TractorTool.Views;

namespace TractorTool;

public partial class App : Application
{
    private PluginHost? _plugins;
    private InstanceLease? _lease;
    // Keep installation/data ownership until the process closes, even if plugin cleanup times out.
    private static PluginManager? _pluginManager;
    private static InstanceLease? _dataLease;
    private static InstanceLease? _migrationLease;
    private CancellationTokenSource? _startup;
    private TrayService? _tray;
    private MainViewModel? _vm;
    private AppLog? _log;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private bool _exiting;
    private bool _ready;
    private ExternalPackageService? _packages;
    private ProcessRunner? _packageProcesses;
    private HttpClient? _packageHttp;
    private BackgroundTaskService? _tasks;
    private bool _confirmingExit;
    private CancellationTokenSource? _exitConfirmation;
    private Task? _shutdown;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var window = new MainWindow();
            window.ShowStartup("正在启动 TractorTool…");
            _startup = new CancellationTokenSource();
            window.Closing += (_, e) =>
            {
                if (_exiting) return;
                e.Cancel = true;
                if (CancelStartup(window)) return;
                if (!_ready) { _ = ExitCoreAsync(systemExit: true); return; }
                if (e.CloseReason != WindowCloseReason.WindowClosing) { _ = ExitCoreAsync(systemExit: true); return; }
                if (_tray?.Available == true) window.Hide();
                else { _vm?.NotifyTrayUnavailable(); Restore(); }
            };
            window.Opened += OnOpened;
            async void OnOpened(object? sender, EventArgs args)
            {
                window.Opened -= OnOpened;
                await StartAsync(desktop, window);
            }
            desktop.MainWindow = window;
            if (OperatingSystem.IsWindows()) Program.RestoreSignal?.Listen(() => Dispatcher.UIThread.Post(Restore));
            desktop.ShutdownRequested += (_, e) =>
            {
                if (_exiting) return;
                e.Cancel = true;
                if (CancelStartup(window)) return;
                _ = ExitCoreAsync(systemExit: true);
            };
            desktop.Exit += (_, _) =>
            {
                DisposeShellResource(() => _vm?.Dispose());
                DisposeShellResource(() => _tray?.Dispose());
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private bool CancelStartup(MainWindow window)
    {
        if (_startup is null) return false;
        _startup.Cancel();
        window.ShowStartup("正在停止启动，原数据保留…");
        return true;
    }

    private async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window)
    {
        var startup = _startup!;
        var compatibility = new HostCompatibility(OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux",
            System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, UiApi.Version, UiApi.AvaloniaVersion);
        var progress = new Progress<string>(message => { if (_startup == startup && !startup.IsCancellationRequested) window.ShowStartup(message); });
        try
        {
            // No log/settings/package/plugin writers exist while the two roots are compared.
            var opened = await Task.Run(() =>
            {
                var fixedRoot = Program.ProfileRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TractorTool");
                _lease = new InstanceLease(AppPaths.Absolute(fixedRoot));
                var paths = AppPaths.Open(Program.ProfileRoot, ensureDirectories: false);
                if (!string.Equals(paths.DataRoot, paths.BootstrapRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    _dataLease = new InstanceLease(paths.DataRoot, createDirectory: false);
                var settings = paths.MissingDataRoot is not null ? new HostSettings() :
                    new JsonFileStore<HostSettings>(Path.Combine(paths.ConfigRoot, "settings.json"), HostSettings.Validate).Load();
                return (Paths: paths, Settings: settings);
            });
            startup.Token.ThrowIfCancellationRequested();
            // Read existing appearance before a potentially long migration; this does not save settings.
            ThemeService.Apply(opened.Settings);
            var prepared = await Task.Run(() =>
            {
                startup.Token.ThrowIfCancellationRequested();
                var paths = opened.Paths;
                var migration = new DataRootMigration(paths, (root, environment, token) =>
                    RebuildPythonEnvironmentAsync(root, environment, progress, token), (root, environment, token) =>
                    RebuildNodeEnvironmentAsync(root, environment, progress, token));
                var requested = migration.Snapshot.Migration is not null;
                string? managementError = null;
                try { _pluginManager = new PluginManager(paths, compatibility); }
                catch (Exception ex)
                {
                    if (paths.MissingDataRoot is not null) throw new IOException("重置数据需要独占插件管理目录，请先解决访问问题后重试。\n" + ex.Message, ex);
                    if (requested) throw new IOException("迁移需要独占插件管理目录，请先解决访问问题后重试。\n" + ex.Message, ex);
                    managementError = "插件管理不可用：" + ex.Message;
                }
                if (paths.MissingDataRoot is not null)
                    paths.CompleteDataReset(_pluginManager!.DiscardDataRootState, startup.Token);
                var result = _pluginManager is null ? new DataMigrationResult(paths, null) :
                    migration.ApplyAtStartup(_pluginManager, progress, startup.Token);
                _migrationLease = result.TargetLease; // Keep both roots exclusively owned until process exit.
                startup.Token.ThrowIfCancellationRequested();
                result.Paths.EnsureDataDirectories();
                return (result.Paths, result.Error, result.Warning, managementError, Migrated: requested && result.Error is null);
            });
            startup.Token.ThrowIfCancellationRequested();
            ConfigureMainWindow(window, prepared.Paths, compatibility, prepared.managementError, prepared.Error, prepared.Warning, prepared.Migrated);
            startup.Token.ThrowIfCancellationRequested();
            _startup = null;
            _ready = true;
            window.ShowWorkspace();
            try { await _vm!.InitializeAsync(); }
            catch (Exception ex)
            {
                WriteHostLog("初始化失败", ex);
            }
            if (!_exiting && _tray?.Available != true) _vm!.NotifyTrayUnavailable();
        }
        catch (OperationCanceledException) when (startup.IsCancellationRequested)
        {
            _startup = null;
            _exiting = true;
            try { await StopServicesBoundedAsync(); }
            finally { FinishShutdown(desktop, 0); }
        }
        catch (Exception ex)
        {
            _startup = null;
            _ready = false;
            // No log is created before migration; use one only if configuration reached that point.
            WriteHostLog("启动失败", ex);
            await StopServicesBoundedAsync();
            DisposeShellResource(() => _tray?.Dispose());
            if (!_exiting) window.ShowStartupError(ex.Message + "\n请保留原数据与迁移记录，解决问题后重新启动。");
        }
        finally { startup.Dispose(); }
    }

    private void ConfigureMainWindow(MainWindow window, AppPaths paths, HostCompatibility compatibility, string? managementError, string? migrationError, string? migrationWarning, bool migrated)
    {
        _log = new AppLog(paths.LogRoot, pruneFiles: migrationError is null);
        if (migrationError is not null) _log.Write(AppLogLevel.Error, "Storage", migrationError);
        else if (migrationWarning is not null) _log.Write(AppLogLevel.Warning, "Storage", migrationWarning);
        else if (migrated) _log.Write("Storage", "应用数据迁移完成，旧业务数据已清理，插件程序位置不变。");
        if (paths.MissingDataRoot is { } missing)
            _log.Write(AppLogLevel.Warning, "Storage", $"原数据目录已删除：{missing}。已在默认目录重新初始化：{paths.DataRoot}；如需更换位置，请重新迁移。");
        if (managementError is not null) _log.Write(AppLogLevel.Error, "Plugins", managementError);
        _log.Write("Host", $"启动：Version={AppInfo.Version}; AppRoot={paths.AppRoot}; DataRoot={paths.DataRoot}; Bootstrap={paths.BootstrapFile}");
        var store = new JsonFileStore<HostSettings>(Path.Combine(paths.ConfigRoot, "settings.json"), HostSettings.Validate);
        var settings = store.Load();
        _tasks = new BackgroundTaskService(Path.Combine(paths.DataRoot, "Data", "tasks.json"), _log);
        ThemeService.Apply(settings);
        _packageHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _packageProcesses = new ProcessRunner("Packages", _log, OperatingSystem.IsWindows() ? WindowsProcessSession.Start :
            _ => throw new PlatformNotSupportedException("当前平台尚未提供受管理的工具进程适配器。"));
        _packages = new ExternalPackageService(paths.PackageRoot, compatibility.Rid, _packageHttp, _packageProcesses, _log,
            sourcesFile: Path.Combine(paths.ConfigRoot, "dependency-sources.json"));
        _plugins = new PluginHost(paths, compatibility, _log,
            OperatingSystem.IsWindows() ? WindowsProcessSession.Start : null, _packages, _tasks);
        if (managementError is not null) _plugins.Discover([], startupError: managementError);
        _tray = new TrayService(Restore, ExitAsync, _log, Program.LaunchArguments.Contains("--no-tray"));
        _vm = new MainViewModel(paths, _plugins, _log, new SettingsViewModel(paths, store, settings, _log, _packages, _pluginManager), ExitAsync,
            managementError is null ? _pluginManager : null, settings.DisabledPlugins, _packages, _tasks, _tray);
        window.DataContext = _vm;
    }

    private static async Task RebuildPythonEnvironmentAsync(string packageRoot, PythonMigrationEnvironment environment,
        IProgress<string>? progress, CancellationToken token)
    {
        // Migration runs before normal services. Writing to either Logs tree here would
        // invalidate the migration snapshot, so diagnostics use the startup progress surface.
        var log = new MigrationProgressLog(progress);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        await using var processes = new ProcessRunner("PythonMigration", log,
            OperatingSystem.IsWindows() ? WindowsProcessSession.Start :
            _ => throw new PlatformNotSupportedException("当前平台尚未提供 Python 环境迁移进程适配器。"));
        var sources = ExternalPackageService.ReadConfiguredSources(Path.Combine(Path.GetDirectoryName(packageRoot)!, "Config", "dependency-sources.json"));
        await PythonEnvironmentService.RebuildCopiedEnvironmentAsync(packageRoot, environment, http, processes, log,
            source => sources.Any(s => s.Kind == DependencySourceKind.PythonIndex && s.Url == source), token).ConfigureAwait(false);
    }

    private static async Task RebuildNodeEnvironmentAsync(string packageRoot, NodeMigrationEnvironment environment,
        IProgress<string>? progress, CancellationToken token)
    {
        var log = new MigrationProgressLog(progress);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        await using var processes = new ProcessRunner("NodeMigration", log,
            OperatingSystem.IsWindows() ? WindowsProcessSession.Start :
            _ => throw new PlatformNotSupportedException("当前平台尚未提供 Node 环境迁移进程适配器。"));
        var sources = ExternalPackageService.ReadConfiguredSources(Path.Combine(Path.GetDirectoryName(packageRoot)!, "Config", "dependency-sources.json"));
        await NodeEnvironmentService.RebuildCopiedEnvironmentAsync(packageRoot, environment, http, processes, log,
            source => sources.Any(s => s.Kind == DependencySourceKind.NpmRegistry && s.Url == source), token).ConfigureAwait(false);
    }

    private sealed class MigrationProgressLog(IProgress<string>? progress) : IAppLog
    {
        public void Write(string source, string message, Exception? error = null) =>
            progress?.Report(error is null ? message : message + "：" + error.Message);
    }

    private void Restore()
    {
        if (_exiting || _desktop?.MainWindow is not { } window) return;
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private Task ExitAsync() => ExitCoreAsync(systemExit: false);
    private async Task ExitCoreAsync(bool systemExit)
    {
        if (_exiting) return;
        if (systemExit)
        {
            try { _exitConfirmation?.Cancel(); }
            catch (Exception ex) { WriteHostLog("取消退出确认失败，继续停止服务。", ex); }
        }
        else
        {
            if (_confirmingExit) return;
            _confirmingExit = true;
            using var confirmation = new CancellationTokenSource();
            _exitConfirmation = confirmation;
            try
            {
                if (_tasks?.HasActive == true && _desktop?.MainWindow is MainWindow owner &&
                    !await owner.ConfirmExitAsync(_tasks.Snapshot.Count(t => t.State is BackgroundTaskState.Queued or BackgroundTaskState.Running or BackgroundTaskState.Cancelling), confirmation.Token)) return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { WriteHostLog("退出确认失败，保持运行。", ex); return; }
            finally { _confirmingExit = false; _exitConfirmation = null; }
        }
        if (_exiting) return;
        _exiting = true;
        Task? stopping = null;
        try
        {
            var surface = StartShutdownStep(() =>
            {
                if (_desktop?.MainWindow is MainWindow window) window.BeginShutdown();
                return Task.CompletedTask;
            });
            WriteHostLog("退出：等待设置保存并停止任务、插件和常驻服务。");
            stopping = Task.WhenAll(surface, StopServicesAsync());
            await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) { WriteHostLog("退出清理失败或超时", stopping?.Exception ?? ex); }
        finally { FinishShutdown(_desktop, _ready ? 0 : 1); }
    }

    private Task StopServicesAsync()
    {
        if (_shutdown is not null) return _shutdown;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _shutdown = completion.Task;
        _ = CompleteAsync();
        return _shutdown;

        async Task CompleteAsync()
        {
            Task? stopping = null;
            try
            {
                // Start every component even if an earlier synchronous cleanup prefix throws.
                stopping = Task.WhenAll(
                    StartShutdownStep(() => _vm?.BeginShutdown() ?? Task.CompletedTask),
                    StartShutdownStep(() => _tasks?.BeginShutdownAsync() ?? Task.CompletedTask),
                    StartShutdownStep(() => _plugins?.DisposeAsync().AsTask() ?? Task.CompletedTask),
                    StopPackagesAsync());
                await stopping;
                completion.TrySetResult();
            }
            catch (Exception ex) { completion.TrySetException(stopping?.Exception ?? ex); }
        }
    }

    private async Task StopServicesBoundedAsync()
    {
        try { await StopServicesAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception ex) { WriteHostLog("启动结束后的服务清理失败或超时", ex); }
    }

    private static Task StartShutdownStep(Func<Task> stop)
    {
        try { return stop(); }
        catch (Exception ex) { return Task.FromException(ex); }
    }

    private void FinishShutdown(IClassicDesktopStyleApplicationLifetime? desktop, int exitCode)
    {
        try { DisposeShellResource(() => _tray?.Dispose()); }
        finally { desktop?.Shutdown(exitCode); }
    }

    private void DisposeShellResource(Action dispose)
    {
        try { dispose(); }
        catch (Exception ex) { WriteHostLog("窗口资源释放失败", ex); }
    }

    private void WriteHostLog(string message, Exception? error = null)
    {
        try { _log?.Write("Host", message, error); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError("{0}: {1}; 日志写入失败: {2}", message, error, ex); }
    }

    private async Task StopPackagesAsync()
    {
        var packages = StartShutdownStep(() => _packages?.DisposeAsync().AsTask() ?? Task.CompletedTask);
        var processes = StartShutdownStep(() => _packageProcesses?.DisposeAsync().AsTask() ?? Task.CompletedTask);
        var stopping = Task.WhenAll(packages, processes);
        try { await stopping; }
        catch when (stopping.Exception is { } failure) { throw failure; }
        finally { _packageHttp?.Dispose(); }
    }
}
