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
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("桌面状态 HUD 需要 Windows。");
        _model = new(context);
        return ValueTask.CompletedTask;
    }

    public void RegisterPages(IPluginPageRegistry registry)
    {
        var model = _model ?? throw new InvalidOperationException("插件未初始化。");
        registry.AddSettingsPage("settings", "桌面状态 HUD", () => new HudSettingsView { DataContext = model },
            new(ToolIconKind.Info, "电源变化提醒与桌面状态显示。"));
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
