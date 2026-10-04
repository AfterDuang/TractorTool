using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using TractorTool.Core.Logging;
using TractorTool.Contracts;

namespace TractorTool.Services;

public sealed class TrayService : IDisposable
{
    private TrayIcon? _icon;
    public bool Available => _icon is { IsVisible: true, NativeMenuExporter: not null };
    public TrayService(Action restore, Func<Task> exit, AppLog log, bool disabled)
    {
        if (disabled) { log.Write("Tray", "本次运行禁用托盘；关闭窗口时保留可见窗口。"); return; }
        try
        {
            var menu = new NativeMenu();
            var showItem = new NativeMenuItem("显示 TractorTool");
            showItem.Click += (_, _) => restore();
            var exitItem = new NativeMenuItem("退出");
            exitItem.Click += async (_, _) => await exit();
            menu.Items.Add(showItem); menu.Items.Add(new NativeMenuItemSeparator()); menu.Items.Add(exitItem);
            _icon = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://TractorTool/Assets/tractortool.ico"))),
                ToolTipText = "TractorTool · 点击恢复窗口", Menu = menu, IsVisible = true
            };
            _icon.Clicked += (_, _) => restore();
            TrayIcon.SetIcons(Application.Current!, new TrayIcons { _icon });
            log.Write(Available ? AppLogLevel.Info : AppLogLevel.Warning, "Tray", Available ? "托盘已创建。" : "托盘不可用，窗口将保留。");
        }
        catch (Exception ex) { log.Write("Tray", "托盘创建失败，窗口将保留。", ex); Dispose(); }
    }
    public void Dispose() { _icon?.Dispose(); _icon = null; }
}
