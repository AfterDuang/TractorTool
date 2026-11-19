using TractorTool.Contracts;
using TractorTool.Plugins.FolderHistory.Services;
using TractorTool.Plugins.FolderHistory.ViewModels;
using TractorTool.Plugins.FolderHistory.Views;
using TractorTool.UI;

namespace TractorTool.Plugins.FolderHistory;

public sealed class FolderHistoryPlugin : IPlugin, IPluginUiModule, IPluginBackgroundService
{
    private FolderHistoryService? _service;
    private FolderHistoryViewModel? _model;

    public async ValueTask InitializeAsync(PluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _service = await Task.Run(() => new FolderHistoryService(context), cancellationToken);
        _model = new FolderHistoryViewModel(_service, context.ApplicationStopping);
    }

    public void RegisterPages(IPluginPageRegistry registry)
    {
        var model = _model ?? throw new InvalidOperationException("插件未初始化。");
        registry.AddNavigationPage("history", "文件夹版本历史", () => new FolderHistoryView { DataContext = model },
            new(ToolIconKind.Folder, "自动保存文件夹历史，比较变化并安全找回旧文件。") { IconPath = "Assets/icon.png" });
    }

    public Task RunAsync(CancellationToken stoppingToken) =>
        (_service ?? throw new InvalidOperationException("插件未初始化。")).RunAsync(stoppingToken);

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        if (_service is { } service) await service.StopAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _model = null;
        _service?.Dispose();
        _service = null;
        return ValueTask.CompletedTask;
    }
}
