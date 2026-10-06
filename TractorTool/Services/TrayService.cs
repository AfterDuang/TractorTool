using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using TractorTool.Core.Logging;
using TractorTool.Contracts;
using TractorTool.Core.Plugins;
using TractorTool.UI;

namespace TractorTool.Services;

public sealed class TrayService : IDisposable
{
    private TrayIcon? _icon;
    private readonly List<PluginTrayMenu> _plugins = [];
    private readonly NativeMenuItemSeparator _pluginSeparator = new();
    private readonly AppLog _log;
    private bool _disposed;
    public NativeMenu Menu { get; } = new();
    public bool Available => _icon is { IsVisible: true, NativeMenuExporter: not null };
    public TrayService(Action restore, Func<Task> exit, AppLog log, bool disabled)
    {
        _log = log;
        var showItem = new NativeMenuItem("显示 TractorTool");
        showItem.Click += (_, _) => restore();
        var exitItem = new NativeMenuItem("退出");
        exitItem.Click += async (_, _) => await exit();
        Menu.Items.Add(showItem); Menu.Items.Add(new NativeMenuItemSeparator()); Menu.Items.Add(exitItem);
        if (disabled) { log.Write("Tray", "本次运行禁用托盘；关闭窗口时保留可见窗口。"); return; }
        try
        {
            _icon = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://TractorTool/Assets/tractortool.ico"))),
                ToolTipText = "TractorTool · 点击恢复窗口", Menu = Menu, IsVisible = true
            };
            _icon.Clicked += (_, _) => restore();
            TrayIcon.SetIcons(Application.Current!, new TrayIcons { _icon });
            log.Write(Available ? AppLogLevel.Info : AppLogLevel.Warning, "Tray", Available ? "托盘已创建。" : "托盘不可用，窗口将保留。");
        }
        catch (Exception ex) { log.Write("Tray", "托盘创建失败，窗口将保留。", ex); _icon?.Dispose(); _icon = null; }
    }

    public IAsyncDisposable? Register(PluginRecord record, IPlugin plugin)
    {
        Avalonia.Threading.Dispatcher.UIThread.VerifyAccess();
        if (plugin is not IPluginTrayModule module) return null;
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Version.TryParse(record.Manifest!.UiApiVersion, out var requiredUi) || requiredUi < new Version(1, 5))
            throw new InvalidDataException("使用托盘菜单需要声明 UI API 1.5。");
        var menu = new PluginTrayMenu(record.Manifest.Id, record.Name, _log);
        try
        {
            module.RegisterTrayMenu(menu);
            menu.Commit(() =>
            {
                Menu.Items.Remove(menu.Group);
                _plugins.Remove(menu);
                if (_plugins.Count == 0) Menu.Items.Remove(_pluginSeparator);
            });
            if (menu.Group.Menu!.Items.Count > 0)
            {
                if (_plugins.Count == 0) Menu.Items.Insert(1, _pluginSeparator);
                _plugins.Add(menu);
                Menu.Items.Insert(Menu.Items.Count - 2, menu.Group);
            }
            return menu;
        }
        catch { _ = menu.DisposeAsync(); throw; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var menu in _plugins.ToArray()) _ = menu.DisposeAsync();
        _icon?.Dispose(); _icon = null;
    }
}
