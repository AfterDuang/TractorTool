using Avalonia.Threading;
using TractorTool.Contracts;
using TractorTool.UI;

namespace PythonEnvironmentProbePlugin;

public sealed class ProbePlugin : IPlugin, IPluginUiModule
{
    private ProbeViewModel? _model;

    public ValueTask InitializeAsync(PluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _model = new(context);
        return ValueTask.CompletedTask;
    }

    public void RegisterPages(IPluginPageRegistry registry)
    {
        var model = _model ?? throw new InvalidOperationException("插件未初始化。");
        registry.AddNavigationPage("python-probe", "Python 环境测试", () => new ProbeView(model),
            new(ToolIconKind.Tools, "真实域名批处理，以及依赖准备、取消、离线和迁移测试。"));
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        if (_model is { } model) await Dispatcher.UIThread.InvokeAsync(model.StopAsync);
    }

    public ValueTask DisposeAsync() { _model = null; return ValueTask.CompletedTask; }
}
