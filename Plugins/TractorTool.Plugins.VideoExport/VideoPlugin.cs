using TractorTool.Contracts;
using TractorTool.UI;
using Avalonia.Threading;
namespace TractorTool.Plugins.VideoExport;

public sealed class VideoPlugin : IPlugin, IPluginUiModule
{
    private VideoViewModel? _model;
    public ValueTask InitializeAsync(PluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _model = new VideoViewModel(context);
        return ValueTask.CompletedTask;
    }
    public void RegisterPages(IPluginPageRegistry registry)
    {
        var model = _model ?? throw new InvalidOperationException("插件未初始化。");
        registry.AddNavigationPage("video", "视频导出", () => new VideoView { DataContext = model },
            new(ToolIconKind.Video, "读取源视频的媒体信息。"));
        registry.AddSettingsPage("settings", "视频导出", () => new VideoSettingsView { DataContext = model },
            new(ToolIconKind.Video, "视频信息读取偏好。"));
    }
    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        if (_model is { } model)
        {
            await Dispatcher.UIThread.InvokeAsync(() => model.StopAsync());
        }
    }
    public ValueTask DisposeAsync() { _model = null; return ValueTask.CompletedTask; }
}
