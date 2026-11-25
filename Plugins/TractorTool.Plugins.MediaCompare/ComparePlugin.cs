using Avalonia.Threading;
using TractorTool.Contracts;
using TractorTool.Plugins.MediaCompare.ViewModels;
using TractorTool.Plugins.MediaCompare.Views;
using TractorTool.UI;

namespace TractorTool.Plugins.MediaCompare;

public sealed class ComparePlugin : IPlugin, IPluginUiModule
{
    private CompareViewModel? _model;
    private readonly PreviewLifetime _previews = new();
    private Task? _stopTask;

    public ValueTask InitializeAsync(PluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("AB 审片需要 Windows x64。");
        _model = new CompareViewModel(context);
        return ValueTask.CompletedTask;
    }

    public void RegisterPages(IPluginPageRegistry registry)
    {
        var model = _model ?? throw new InvalidOperationException("插件尚未初始化。");
        registry.AddNavigationPage("compare", "AB 审片", () => new CompareView(_previews) { DataContext = model },
            new(ToolIconKind.Video, "并排比较画面，联动查看细节。") { UsesCustomHeader = true, IconPath = "Assets/icon.png" });
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        await Dispatcher.UIThread.InvokeAsync(StopCoreAsync);
    }

    public async ValueTask DisposeAsync()
    {
        try { await Dispatcher.UIThread.InvokeAsync(StopCoreAsync); }
        finally { _model = null; }
    }

    private Task StopCoreAsync() => _stopTask ??= Task.WhenAll(
        _previews.DisposeAsync().AsTask(), _model?.DisposeAsync().AsTask() ?? Task.CompletedTask);
}
