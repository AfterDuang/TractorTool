using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TractorTool.Contracts;
using TractorTool.Contracts.Packages;
using TractorTool.Contracts.Processes;
using TractorTool.UI;

namespace PackageProbePlugin;

public sealed class ProbePlugin : IPlugin, IPluginUiModule
{
    private ProbeViewModel? _model;

    public ValueTask InitializeAsync(PluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _model = new ProbeViewModel(context);
        return ValueTask.CompletedTask;
    }

    public void RegisterPages(IPluginPageRegistry registry)
    {
        var model = _model ?? throw new InvalidOperationException("插件未初始化。");
        registry.AddNavigationPage("probe", "外部依赖测试", () => new StackPanel
        {
            Spacing = 16,
            DataContext = model,
            Children =
            {
                new TextBlock { Text = "测试 MediaInfo CLI 26.05", Classes = { "sectionTitle" } },
                new TextBlock { Text = "点击后由宿主确认并准备依赖，再读取版本。切页或隐藏窗口会继续；不读取媒体文件。", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new Button { Content = "申请依赖并读取版本", Command = model.RunCommand, Classes = { "accent" } },
                new Button { Content = "取消当前操作", Command = model.CancelCommand },
                new TextBlock { [!TextBlock.TextProperty] = new Binding(nameof(ProbeViewModel.Status)), TextWrapping = Avalonia.Media.TextWrapping.Wrap }
            }
        }, new(ToolIconKind.Tools, "独立插件申请非 FFmpeg 包并执行只读版本命令。"));
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        if (_model is { } model)
            await Dispatcher.UIThread.InvokeAsync(model.StopAsync);
    }

    public ValueTask DisposeAsync() { _model = null; return ValueTask.CompletedTask; }
}

// The plugin owns the command and lease; navigation only creates a view of this model.
public sealed partial class ProbeViewModel : ObservableObject
{
    private readonly PluginContext _context;
    private bool _stopped;
    [ObservableProperty] private string _status = "尚未申请依赖。";
    public AsyncRelayCommand RunCommand { get; }
    public System.Windows.Input.ICommand CancelCommand { get; }

    public ProbeViewModel(PluginContext context)
    {
        _context = context;
        RunCommand = new AsyncRelayCommand(RunAsync, () => !_stopped);
        CancelCommand = RunCommand.CreateCancelCommand();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _context.ApplicationStopping);
        Status = "正在申请 MediaInfo 26.05…";
        try
        {
            var packages = _context.Packages ?? throw new NotSupportedException("宿主未提供包服务。");
            var processes = _context.Processes ?? throw new NotSupportedException("宿主未提供进程服务。");
            ProcessResult result;
            using (var lease = await packages.TryRequestAsync(new PackageReference("MediaInfo", "26.5.0"), operation.Token))
            {
                if (lease is null) { Status = "本次未安装；可稍后重试。"; return; }
                Status = "依赖已取得，正在读取版本…";
                result = await processes.RunAsync(new ProcessRequest(lease.Tools["mediainfo"], ["--Version"])
                {
                    Timeout = TimeSpan.FromSeconds(15),
                    OutputLimit = 16 * 1024
                }, operation.Token);
                if (result.ExitCode != 0 || result.StandardOutputTruncated || result.StandardErrorTruncated ||
                    !result.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal)
                        .StartsWith("MediaInfo Command line, \nMediaInfoLib - v26.05", StringComparison.Ordinal))
                    throw new InvalidDataException($"MediaInfo 版本检查失败（退出码 {result.ExitCode}）。");
            }
            Status = result.StandardOutput.Trim() + "\n版本检查通过，包租约已释放。";
            _context.Log.Write(_context.PluginId, "MediaInfo CLI 26.05 版本检查通过。");
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            Status = "操作已取消。";
            _context.Log.Write(_context.PluginId, Status);
        }
        catch (Exception ex)
        {
            Status = "操作失败：" + ex.Message;
            _context.Log.Write(_context.PluginId, Status, ex);
        }
    }

    public async Task StopAsync()
    {
        _stopped = true;
        RunCommand.NotifyCanExecuteChanged();
        RunCommand.Cancel();
        if (RunCommand.ExecutionTask is { } running) await running;
    }
}
