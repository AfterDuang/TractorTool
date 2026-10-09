using Avalonia.Threading;
using TractorTool.Contracts;
using TractorTool.UI;

namespace TractorTool.Plugins.SystemHud;

public sealed class HudPlugin : IPlugin, IPluginUiModule, IPluginTrayModule, IPluginBackgroundService
{
    private HudModel? _model;
    public ValueTask InitializeAsync(PluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("系统数据HUD 需要 Windows。");
        _model = new(context);
        return ValueTask.CompletedTask;
    }

    public void RegisterPages(IPluginPageRegistry registry)
    {
        var model = _model ?? throw new InvalidOperationException("插件未初始化。");
        registry.AddNavigationPage("hud", "系统数据HUD", () => new HudView { DataContext = model },
            new(ToolIconKind.Tools, "选择指标，编辑桌面状态显示。") { UsesCustomHeader = true, IconPath = "Assets/icon.png" });
        registry.AddSettingsPage("settings", "系统数据HUD", () => new HudSettingsView { DataContext = model },
            new(ToolIconKind.Tools, "电源变化提醒与桌面状态显示。") { IconPath = "Assets/icon.png" });
    }

    public void RegisterTrayMenu(IPluginTrayMenu menu) => _model!.RegisterTray(menu);

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = stoppingToken.Register(() => finished.TrySetResult());
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => { if (!stoppingToken.IsCancellationRequested) _model?.Start(); });
            await finished.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => _model?.StartupFailed(ex));
            throw;
        }
        finally { await StopAsync(CancellationToken.None); }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Task pending = Task.CompletedTask;
        await Dispatcher.UIThread.InvokeAsync(() => { if (_model is { } model) pending = model.Stop(); });
        await pending.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() { await StopAsync(CancellationToken.None); _model = null; }
}
