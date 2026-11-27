using Avalonia.Threading;
using TractorTool.Contracts;
using TractorTool.UI;

namespace NodeEnvironmentProbePlugin;

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
        registry.AddNavigationPage("node-probe", "Node 环境测试", () => new ProbeView(model),
            new(ToolIconKind.Tools, "使用真实 npm 包处理输入，验证环境、任务与取消。"));
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        if (_model is { } model) await Dispatcher.UIThread.InvokeAsync(model.StopAsync);
    }

    public ValueTask DisposeAsync() { _model = null; return ValueTask.CompletedTask; }
}
