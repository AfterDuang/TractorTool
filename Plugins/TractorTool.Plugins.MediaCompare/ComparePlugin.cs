using Avalonia.Threading;
using TractorTool.Contracts;
using TractorTool.Plugins.MediaCompare.ViewModels;
using TractorTool.Plugins.MediaCompare.Views;
using TractorTool.UI;

namespace TractorTool.Plugins.MediaCompare;

public sealed class ComparePlugin : IPlugin, IPluginUiModule
{
    private CompareViewModel? _model;

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
        registry.AddNavigationPage("compare", "AB 审片", () => new CompareView { DataContext = model },
            new(ToolIconKind.Video, "并排比较画面，联动查看细节。") { UsesCustomHeader = true });
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        if (_model is { } model)
            await Dispatcher.UIThread.InvokeAsync(() => model.DisposeAsync().AsTask());
    }

    public async ValueTask DisposeAsync()
    {
        if (_model is { } model) await model.DisposeAsync();
        _model = null;
    }
}
