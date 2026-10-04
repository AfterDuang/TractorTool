using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using TractorTool.Core.Logging;
using TractorTool.Core.Plugins;
using TractorTool.Core.Storage;
using TractorTool.Core.Packages;
using TractorTool.Core.Processes;
using TractorTool.Core.Tasks;
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

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var fixedRoot = Program.ProfileRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TractorTool");
                _lease = new InstanceLease(AppPaths.Absolute(fixedRoot));
                var paths = AppPaths.Open(Program.ProfileRoot);
                if (!string.Equals(paths.DataRoot, paths.BootstrapRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    _dataLease = new InstanceLease(paths.DataRoot);
                _log = new AppLog(paths.LogRoot);
                _log.Write("Host", $"启动：Version={AppInfo.Version}; AppRoot={paths.AppRoot}; DataRoot={paths.DataRoot}; Bootstrap={paths.BootstrapFile}");
                var store = new JsonFileStore<HostSettings>(Path.Combine(paths.ConfigRoot, "settings.json"), HostSettings.Validate);
                var settings = store.Load();
                _tasks = new BackgroundTaskService(Path.Combine(paths.DataRoot, "Data", "tasks.json"), _log);
                ThemeService.Apply(settings);
                var compatibility = new HostCompatibility(OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux",
                    System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, UiApi.Version, UiApi.AvaloniaVersion);
                _packageHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                _packageProcesses = new ProcessRunner("Packages", _log, OperatingSystem.IsWindows() ? WindowsProcessSession.Start :
                    _ => throw new PlatformNotSupportedException("当前平台尚未提供受管理的工具进程适配器。"));
                _packages = new ExternalPackageService(paths.PackageRoot, compatibility.Rid, _packageHttp, _packageProcesses, _log);
                string? managementError = null;
                try { _pluginManager = new PluginManager(paths, compatibility); }
                catch (Exception ex) { managementError = "插件管理不可用：" + ex.Message; _log.Write("Plugins", managementError, ex); }
                _plugins = new PluginHost(paths, compatibility, _log,
                    OperatingSystem.IsWindows() ? WindowsProcessSession.Start : null, _packages, _tasks);
                if (managementError is not null) _plugins.Discover([], startupError: managementError);
                _vm = new MainViewModel(paths, _plugins, _log, new SettingsViewModel(paths, store, settings, _log, _packages), ExitAsync,
                    managementError is null ? _pluginManager : null, settings.DisabledPlugins, _packages, _tasks);
                var window = new MainWindow { DataContext = _vm };
                desktop.MainWindow = window;
                window.Closing += (_, e) =>
                {
                    if (_exiting) return;
                    e.Cancel = true;
                    if (e.CloseReason != WindowCloseReason.WindowClosing) { _ = ExitCoreAsync(systemExit: true); return; }
                    if (_tray?.Available == true) { window.Hide(); _log.Write("Tray", "主窗口已隐藏；插件生命周期保持运行。"); }
                    else { _vm.NotifyTrayUnavailable(); Restore(); }
                };
                window.Opened += async (_, _) =>
                {
                    if (_ready) return;
                    _ready = true;
                    _tray = new TrayService(Restore, ExitAsync, _log, Program.LaunchArguments.Contains("--no-tray"));
                    if (OperatingSystem.IsWindows()) Program.RestoreSignal?.Listen(() => Dispatcher.UIThread.Post(Restore));
                    try { await _vm.InitializeAsync(); }
                    catch (Exception ex)
                    {
                        _log.Write("Host", "初始化失败", ex);
                        if (!_exiting) _vm.Notice = "初始化失败：" + ex.Message;
                    }
                    if (!_exiting && _tray.Available != true) _vm.NotifyTrayUnavailable();
                };
                desktop.ShutdownRequested += (_, e) => { if (!_exiting) { e.Cancel = true; _ = ExitCoreAsync(systemExit: true); } };
                desktop.Exit += (_, _) => { _vm?.Dispose(); _tray?.Dispose(); };
            }
            catch (Exception ex)
            {
                var quit = new Button { Content = "退出" };
                quit.Click += (_, _) => desktop.Shutdown(1);
                var message = new TextBox { Text = "启动失败，原配置未覆盖。\n" + ex.Message + "\n若配置损坏，可在退出后用同目录的 .bak 恢复。", IsReadOnly = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
                var window = new Window { Title = "TractorTool · 启动失败", Width = 720, Height = 360,
                    Content = new StackPanel { Margin = new Thickness(24), Spacing = 20, Children = { message, quit } } };
                window.Closed += (_, _) => desktop.Shutdown(1);
                desktop.MainWindow = window;
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void Restore()
    {
        if (_exiting || _desktop?.MainWindow is not { } window) return;
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        _log?.Write("Tray", "主窗口已恢复。");
    }

    private Task ExitAsync() => ExitCoreAsync(systemExit: false);
    private async Task ExitCoreAsync(bool systemExit)
    {
        if (_exiting) return;
        if (systemExit) _exitConfirmation?.Cancel();
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
            catch (Exception ex) { _log?.Write("Host", "退出确认失败，保持运行。", ex); return; }
            finally { _confirmingExit = false; _exitConfirmation = null; }
        }
        if (_exiting) return;
        _exiting = true;
        if (_desktop?.MainWindow is { } window) window.IsEnabled = false;
        var pendingSave = _vm?.Settings.BeginShutdown() ?? Task.CompletedTask;
        var pendingManagement = _vm?.Plugins.BeginShutdown() ?? Task.CompletedTask;
        _vm?.Detach();
        _log?.Write("Host", "退出：等待设置保存并停止任务、插件和常驻服务。");
        try
        {
            var stopTasks = _tasks?.BeginShutdownAsync() ?? Task.CompletedTask;
            var stopPlugins = _plugins?.DisposeAsync().AsTask() ?? Task.CompletedTask;
            var stopPackages = StopPackagesAsync();
            await Task.WhenAll(pendingSave, pendingManagement, stopTasks, stopPlugins, stopPackages).WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) { _log?.Write("Host", "退出清理失败或超时", ex); }
        finally { _tray?.Dispose(); _desktop?.Shutdown(); }
    }

    private async Task StopPackagesAsync()
    {
        var packages = _packages?.DisposeAsync().AsTask() ?? Task.CompletedTask;
        var processes = _packageProcesses?.DisposeAsync().AsTask() ?? Task.CompletedTask;
        await Task.WhenAll(packages, processes);
        _packageHttp?.Dispose();
    }
}
