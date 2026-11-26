using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TractorTool.Contracts;
using TractorTool.Contracts.Processes;
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
        registry.AddNavigationPage("python-probe", "Python 环境测试", () => new StackPanel
        {
            Spacing = 16,
            DataContext = model,
            Children =
            {
                new TextBlock { Text = "Python 3.13.18 / idna 3.11", Classes = { "sectionTitle" } },
                new TextBlock { Text = "点击后按锁文件准备独立环境，执行真实 Python 包的域名编码。切页不会取消运行。", TextWrapping = TextWrapping.Wrap },
                new Button { Content = "准备环境并执行", Command = model.RunCommand, Classes = { "accent" } },
                new Button { Content = "取消当前操作", Command = model.CancelCommand },
                new TextBlock { [!TextBlock.TextProperty] = new Binding(nameof(ProbeViewModel.Status)), TextWrapping = TextWrapping.Wrap }
            }
        }, new(ToolIconKind.Tools, "验证 PyPI 锁定安装、Python 调用与独立环境管理。"));
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        if (_model is { } model) await Dispatcher.UIThread.InvokeAsync(model.StopAsync);
    }

    public ValueTask DisposeAsync() { _model = null; return ValueTask.CompletedTask; }
}

public sealed partial class ProbeViewModel : ObservableObject
{
    private readonly PluginContext _context;
    private bool _stopped;
    [ObservableProperty] private string _status = "尚未申请环境。";
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
        Status = "正在申请 Python 环境…";
        try
        {
            var packages = _context.Packages ?? throw new NotSupportedException("宿主未提供依赖服务。");
            using var lease = await packages.TryRequestPythonAsync("worker", operation.Token);
            if (lease is null) { Status = "本次未安装；可稍后重试。"; return; }
            Status = "环境已准备，正在执行 idna…";
            var result = await lease.RunAsync(new ProcessRequest(lease.PythonPath,
                ["-c", "import idna, sys; text = chr(0x6D4B) + chr(0x8BD5); print(idna.__version__); print(idna.encode(text).decode('ascii')); print(text); print(text, file=sys.stderr)"])
            {
                Timeout = TimeSpan.FromSeconds(15),
                OutputLimit = 4096
            }, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            var lines = result.StandardOutput.Replace("\r", "", StringComparison.Ordinal).Trim().Split('\n');
            if (result.ExitCode != 0 || result.StandardOutputTruncated || result.StandardErrorTruncated ||
                lines.Length != 3 || lines[0] != "3.11" || lines[1] != "xn--0zwm56d" || lines[2] != "测试" || result.StandardError.Trim() != "测试")
                throw new InvalidDataException($"Python 执行结果不符（退出码 {result.ExitCode}）：{result.StandardError}");
            Status = $"idna {lines[0]}：测试 → {lines[1]}。标准输出和诊断输出均正确返回中文。\n解释器：{lease.PythonPath}\n可在设置 → 外部依赖 → Packages 查看环境与来源。";
            _context.Log.Write(_context.PluginId, "Python 环境调用通过：idna 3.11，域名编码结果匹配。");
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            Status = "操作已取消，可稍后重试。";
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
