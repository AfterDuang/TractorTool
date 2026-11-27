using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TractorTool.Core.Logging;
using TractorTool.Core.Plugins;
using TractorTool.Core.Packages;
using TractorTool.Core.Storage;
using TractorTool.Core.Tasks;
using TractorTool.Services;
using TractorTool.UI;
using TractorTool.Views;

namespace TractorTool.ViewModels;

public partial class NavigationItem(string title, IRelayCommand openCommand, string key = "",
    ToolIconKind icon = ToolIconKind.Tools, string description = "", IImage? imageSource = null) : ObservableObject
{
    public string Title { get; } = title;
    public IRelayCommand OpenCommand { get; } = openCommand;
    public string Key { get; } = key;
    public ToolIconKind Icon { get; } = icon;
    public string Description { get; } = description;
    public IImage? ImageSource { get; } = imageSource;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasErrors)), NotifyPropertyChangedFor(nameof(ErrorDescription))]
    private long _unreadErrors;
    public bool HasErrors => UnreadErrors > 0;
    public string ErrorDescription => $"{UnreadErrors} 条未读错误";
}

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly PluginHost _plugins;
    private readonly TrayService? _tray;
    private readonly AppLog _log;
    private readonly Func<Task> _exit;
    private readonly NavigationIconStore _icons;
    private readonly PluginManager? _manager;
    private readonly PluginPackageReferences? _packageReferences;
    private readonly IReadOnlyCollection<string> _legacyDisabled;
    private Task? _initialization;
    private readonly List<RegisteredPage> _registered = [];
    private string _pageKey = "";
    private bool _detached;
    private bool _navigating;
    private Func<Control, bool>? _presentPage;
    private Action? _clearPage;
    private bool _logUpdateQueued;
    private readonly object _logGate = new();
    public ObservableCollection<NavigationItem> Navigation { get; } = [];
    public IReadOnlyList<NavigationItem> FooterNavigation { get; }
    public HomeViewModel Home { get; }
    public SettingsViewModel Settings { get; }
    public PluginsViewModel Plugins { get; }
    public LogsViewModel Logs { get; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPageScroll))] private Control? _currentPage;
    // Only built-in pages with a page-level ScrollViewer opt into the shell's edge layout.
    public bool HasPageScroll => CurrentPage is HomeView or SettingsView or PluginsView or MessageView;
    [ObservableProperty] private string _pageTitle = "主页";
    [ObservableProperty] private bool _showPageHeader = true;
    [ObservableProperty] private string _pageDescription = "你的桌面工具箱，从这里开始。";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasNotice))] private string _notice = "";
    public bool HasNotice => Notice.Length != 0;
    [ObservableProperty] private NavigationItem? _selectedNavigation;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLogsSelected))]
    private string _activeNavigation = "";
    public bool IsLogsSelected => ActiveNavigation == "logs";

    public MainViewModel(AppPaths paths, PluginHost plugins, AppLog log, SettingsViewModel settings, Func<Task> exit, PluginManager? manager = null,
        IReadOnlyCollection<string>? legacyDisabled = null, ExternalPackageService? packages = null, BackgroundTaskService? tasks = null,
        TrayService? tray = null)
    {
        _plugins = plugins; _log = log; Settings = settings; _exit = exit; _manager = manager;
        _tray = tray;
        _legacyDisabled = legacyDisabled ?? [];
        if (packages is not null) _packageReferences = new(paths, manager, packages);
        _icons = new NavigationIconStore(log);
        Settings.ExitCommand = ExitCommand;
        Logs = new LogsViewModel(paths, log, new(tasks, OpenTaskPlugin,
            id => _plugins.Records.FirstOrDefault(r => r.Manifest?.Id == id)?.Name ?? id,
            id => FindTaskPluginPage(id) is not null));
        Plugins = new PluginsViewModel(paths, plugins, ShowLogsCommand, manager, log);
        Home = new HomeViewModel(ShowPluginsCommand, new RelayCommand(() =>
        {
            ShowSettings();
            if (CurrentPage is SettingsView) Settings.SelectedPage = Settings.AppearancePage;
        }));
        Navigation.Add(new("主页", ShowHomeCommand, "home", ToolIconKind.Home, imageSource: _icons.GetHostIcon(ToolIconKind.Home)));
        FooterNavigation = [
            new("日志", ShowLogsCommand, "logs", ToolIconKind.Logs, imageSource: _icons.GetHostIcon(ToolIconKind.Logs)),
            new("插件", ShowPluginsCommand, "plugins", ToolIconKind.Tools, imageSource: _icons.GetHostIcon(ToolIconKind.Tools)),
            new("设置", ShowSettingsCommand, "settings", ToolIconKind.Settings, imageSource: _icons.GetHostIcon(ToolIconKind.Settings))];
        _log.Changed += OnLogChanged;
        FooterNavigation[0].UnreadErrors = _log.Snapshot().UnreadErrors;
        ShowHome();
    }

    public Task InitializeAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        return _initialization ??= InitializeCoreAsync();
    }

    private async Task InitializeCoreAsync()
    {
        try { await LoadPluginsAsync(); }
        catch (Exception ex)
        {
            if (!_detached)
            {
                Notice = "初始化失败：" + ex.Message;
                Home.DirectoryError = Notice;
                Home.Summary = "插件加载未完成";
                Plugins.OperationStatus = Notice;
            }
            throw;
        }
        finally
        {
            Home.IsLoading = false;
            Plugins.IsLoading = false;
        }
    }

    private async Task LoadPluginsAsync()
    {
        if (_detached) return;
        if (_manager is not null)
        {
            try
            {
                await Task.Run(() => { _manager.ApplyPending(); _plugins.Discover(_legacyDisabled, _manager.Snapshot); });
            }
            catch (Exception ex)
            {
                if (_detached) return;
                Plugins.ManagementAvailable = false;
                _plugins.Discover([], startupError: "插件管理启动失败：" + ex.Message);
            }
            if (_detached) return;
        }
        _packageReferences?.SetDiscovered(_plugins.Records, _plugins.DirectoryError);
        await _plugins.LoadWithResourcesAsync((record, plugin) =>
        {
            if (_detached) return null;
            var registry = new PageRegistry(record.Manifest!.Id);
            if (plugin is IPluginUiModule ui) ui.RegisterPages(registry);
            if (_detached) return null;
            var menu = _tray?.Register(record, plugin, () => !_detached);
            if (!_detached) _registered.AddRange(registry.Pages);
            return menu;
        });
        if (_detached) return;
        if (_manager is not null)
        {
            try { await Task.Run(() => _manager.RecordLoadResults(_plugins.Records)); }
            catch (Exception ex) { _log.Write("Plugins", "保存插件加载结果失败", ex); Plugins.OperationStatus = "插件已加载，但管理状态保存失败：" + ex.Message; }
        }
        if (_detached) return;
        foreach (var page in _registered)
        {
            var record = _plugins.Records.Single(r => r.Manifest?.Id == page.PluginId && r.State == PluginState.Loaded);
            var image = _icons.GetPluginIcon(record.Directory, page.Options.IconPath, page.PluginId);
            if (page.IsSettings)
            {
                Settings.AddPluginPage(new(page.Title, page.Factory, Description: page.Options.Description, Icon: page.Options.Icon, ImageSource: image));
                continue;
            }
            var item = new NavigationItem(page.Title, new RelayCommand(() => OpenPluginPage(page)),
                page.PluginId + "/" + page.Id, page.Options.Icon, page.Options.Description, image);
            Navigation.Add(item);
        }
        Plugins.Update(_registered, Navigation);
        Home.Update(Plugins.Items, Plugins.Summary, Plugins.DirectoryError);
        Logs.Tasks.Refresh();
        // Update the catalog without replacing a page opened while plugins were starting.
    }

    private void OpenPluginPage(RegisteredPage page)
    {
        var key = page.PluginId + "/" + page.Id;
        if (_detached || _navigating || _pageKey == key) return;
        _navigating = true;
        try
        {
            Navigate(key, key, page.Title, page.Options.Description,
                page.Factory() ?? throw new InvalidDataException("页面工厂返回空值。"), !page.Options.UsesCustomHeader);
        }
        catch (Exception ex)
        {
            ShowNavigationError(key, key, page.Title, page.PluginId, ex,
                new RelayCommand(() => OpenPluginPage(page)));
        }
        finally { _navigating = false; }
    }

    private RegisteredPage? FindTaskPluginPage(string id) => _registered.FirstOrDefault(p => p.PluginId == id && !p.IsSettings);
    private void OpenTaskPlugin(string id)
    {
        var page = FindTaskPluginPage(id);
        if (page is not null) OpenPluginPage(page);
        else ShowPlugins();
    }

    private void Navigate(string key, string navigationKey, string title, string description, Control view, bool showHeader = true)
    {
        // A parented control belongs to someone else; never clean it up as our candidate.
        if (view.Parent is not null || view.GetVisualParent() is not null)
            throw new InvalidDataException("页面工厂必须返回尚未挂载的独立页面。");
        if (_detached) { ReleasePage(view); return; }
        try
        {
            if (_presentPage?.Invoke(view) == false)
            {
                ReleasePage(view);
                return;
            }
        }
        catch { ReleasePage(view); throw; }
        if (_detached)
        {
            ReleasePage(view);
            ClearPresentedPage();
            return;
        }
        var previous = CurrentPage;
        _pageKey = key;
        ActiveNavigation = navigationKey;
        SelectedNavigation = Navigation.Concat(FooterNavigation).FirstOrDefault(p => p.Key == navigationKey);
        PageTitle = title; PageDescription = description;
        ShowPageHeader = showHeader;
        CurrentPage = view;
        // Commit before cleanup; callbacks cannot reenter navigation while it is in progress.
        ReleasePage(previous);
    }

    private void ReleasePage(Control? page)
    {
        try
        {
            if (page is IPluginPageLifetime lifetime) lifetime.OnNavigatedFrom();
            else if (page?.DataContext is IPluginPageLifetime vm) vm.OnNavigatedFrom();
        }
        catch (Exception ex) { _log.Write("Navigation", "页面离开清理失败", ex); }
    }

    [RelayCommand] private void ShowHome()
    {
        if (_detached || _navigating || _pageKey == "home") return;
        NavigateHostPage("home", "主页", "你的桌面工具箱，从这里开始。", () => new HomeView { DataContext = Home }, ShowHomeCommand);
    }
    [RelayCommand] private void ShowPlugins()
    {
        if (_detached || _navigating || _pageKey == "plugins") return;
        NavigateHostPage("plugins", "插件", "你的工具，由插件扩展。", () => new PluginsView { DataContext = Plugins }, ShowPluginsCommand);
    }
    [RelayCommand] private void ShowSettings()
    {
        if (_detached || _navigating || _pageKey == "settings") return;
        NavigateHostPage("settings", "设置", "让 TractorTool 更适合你的使用习惯。", () => new SettingsView { DataContext = Settings }, ShowSettingsCommand);
    }
    [RelayCommand] private void ShowLogs()
    {
        if (_detached || _navigating || _pageKey == "logs") return;
        Logs.Refresh();
        Logs.Tasks.Refresh();
        NavigateHostPage("logs", "日志", "运行记录与任务进度", () => new LogsView { DataContext = Logs }, ShowLogsCommand);
    }
    private void NavigateHostPage(string key, string title, string description, Func<Control> factory, IRelayCommand retry)
    {
        _navigating = true;
        try { Navigate(key, key, title, description, factory()); }
        catch (Exception ex) { ShowNavigationError(key, key, title, "Navigation", ex, retry); }
        finally { _navigating = false; }
    }
    private void ShowNavigationError(string key, string navigationKey, string title, string source,
        Exception error, IRelayCommand retry)
    {
        if (_detached) return;
        _log.Write(source, "页面创建或呈现失败", error);
        Notice = "页面加载失败；可重试或查看日志。";
        try
        {
            Navigate(key + "/error", navigationKey, title, "页面暂时无法打开",
                new MessageView { DataContext = new MessageViewModel("页面加载失败",
                    error.GetBaseException().Message, retry, ShowLogsCommand) });
        }
        catch (Exception ex) { _log.Write("Navigation", "错误页呈现失败", ex); Notice = "页面呈现失败，请查看日志后重试。"; }
    }
    public void AttachPagePresenter(Func<Control, bool> present, Action clear)
    {
        if (_detached) return;
        _presentPage = present;
        _clearPage = clear;
        if (CurrentPage is not { } initial) return;
        _navigating = true;
        try
        {
            present(initial);
        }
        catch (Exception ex) { ShowNavigationError(_pageKey, ActiveNavigation, PageTitle, "Navigation", ex, SelectedNavigation?.OpenCommand ?? ShowHomeCommand); }
        finally { _navigating = false; }
    }
    public void DetachPagePresenter()
    {
        try { ClearPresentedPage(); }
        finally { _presentPage = null; _clearPage = null; }
    }
    private void ClearPresentedPage()
    {
        try { _clearPage?.Invoke(); }
        catch (Exception ex) { _log.Write("Navigation", "退出页面呈现清理失败", ex); }
    }
    [RelayCommand] private Task ExitAsync() => _exit();
    public void NotifyTrayUnavailable()
    {
        if (!HasNotice) Notice = "系统托盘不可用，窗口将保留；可在设置的关于区域退出。";
    }
    private void OnLogChanged()
    {
        // Coalesce worker notifications; the bounded log buffer remains the source of truth.
        lock (_logGate)
        {
            if (_detached || _logUpdateQueued) return;
            _logUpdateQueued = true;
        }
        Dispatcher.UIThread.Post(() =>
        {
            lock (_logGate) _logUpdateQueued = false;
            if (_detached) return;
            FooterNavigation[0].UnreadErrors = _log.Snapshot().UnreadErrors;
            if (IsLogsSelected) Logs.Refresh();
        });
    }
    public Task BeginShutdown()
    {
        var pendingSave = Start(Settings.BeginShutdown);
        var pendingManagement = Start(Plugins.BeginShutdown);
        var detached = Start(() => { Detach(); return Task.CompletedTask; });
        return Task.WhenAll(_initialization ?? Task.CompletedTask, pendingSave, pendingManagement, detached);

        static Task Start(Func<Task> stop)
        {
            try { return stop(); }
            catch (Exception ex) { return Task.FromException(ex); }
        }
    }

    public void Detach()
    {
        if (_detached) return;
        _detached = true;
        List<Exception>? errors = null;
        Run(Settings.ExternalTools.Dispose);
        Run(Logs.Tasks.Dispose);
        Run(() => _packageReferences?.Dispose());
        _log.Changed -= OnLogChanged;
        var previous = CurrentPage;
        Run(() => CurrentPage = null);
        Run(DetachPagePresenter);
        Run(() => ReleasePage(previous));
        if (errors is not null) throw new AggregateException("工作区清理失败", errors);

        void Run(Action cleanup)
        {
            try { cleanup(); }
            catch (Exception ex) { (errors ??= []).Add(ex); }
        }
    }

    // Detach runs when shutdown begins; the disabled window can still render during plugin cleanup.
    // Dispose only after the owning window closes (or for a VM that was never attached to a window).
    public void Dispose()
    {
        try { Detach(); }
        finally { _icons.Dispose(); }
    }
}
