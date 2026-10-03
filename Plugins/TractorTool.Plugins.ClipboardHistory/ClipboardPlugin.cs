using TractorTool.Contracts;
using TractorTool.UI;

namespace TractorTool.Plugins.ClipboardHistory;

public sealed class ClipboardPlugin : IPlugin, IPluginUiModule
{
    private PluginContext? _context;
    public ValueTask InitializeAsync(PluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            throw new PlatformNotSupportedException("剪贴板插件需要 Windows 10 2004 或更新版本。");
        _context = context;
        return ValueTask.CompletedTask;
    }
    public void RegisterPages(IPluginPageRegistry registry)
    {
        var context = _context ?? throw new InvalidOperationException("插件未初始化。");
        registry.AddNavigationPage("history", "剪贴板历史",
            () => new ClipboardHistoryView { DataContext = new ClipboardHistoryViewModel(new WindowsClipboardHistory(), context.ApplicationStopping) },
            new PluginPageOptions(ToolIconKind.Clipboard, "查看 Windows 保存的历史，随时手动刷新。"));
    }
    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() { _context = null; return ValueTask.CompletedTask; }
}
