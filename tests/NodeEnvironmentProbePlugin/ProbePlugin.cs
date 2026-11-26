using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TractorTool.Contracts;
using TractorTool.Contracts.Processes;
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
        registry.AddNavigationPage("node-probe", "Node 环境测试", () => new StackPanel
        {
            Spacing = 16,
            DataContext = model,
            Children =
            {
                new TextBlock { Text = "Node 24.22.3 / is-number 7.0.0", Classes = { "sectionTitle" } },
                new TextBlock { Text = "点击后按锁文件准备独立环境，调用真实 npm 包。切页不会取消运行。", TextWrapping = TextWrapping.Wrap },
                new Button { Content = "准备环境并执行", Command = model.RunCommand, Classes = { "accent" } },
                new Button { Content = "取消当前操作", Command = model.CancelCommand },
                new TextBlock { [!TextBlock.TextProperty] = new Binding(nameof(ProbeViewModel.Status)), TextWrapping = TextWrapping.Wrap }
            }
        }, new(ToolIconKind.Tools, "验证 npm 锁定安装、Node 调用与独立环境管理。"));
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
        Status = "正在申请 Node 环境…";
        try
        {
            var packages = _context.Packages ?? throw new NotSupportedException("宿主未提供依赖服务。");
            using var lease = await packages.TryRequestNodeAsync("worker", operation.Token);
            if (lease is null) { Status = "本次未安装；可稍后重试。"; return; }
            Status = "环境已准备，正在执行 is-number…";
            const string script = "const r = require('node:module').createRequire(process.argv[1]); const n = r('is-number'); " +
                "console.log(JSON.stringify([process.version,r('is-number/package.json').version,n('42'),n('test'),'测试'])); console.error('测试');";
            var result = await lease.RunAsync(new ProcessRequest(lease.NodePath,
                ["-e", script, Path.Combine(lease.Directory, "package.json")])
            { Timeout = TimeSpan.FromSeconds(15), OutputLimit = 4096 }, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (result.ExitCode != 0 || result.StandardOutputTruncated || result.StandardErrorTruncated)
                throw new InvalidDataException($"Node 执行失败（退出码 {result.ExitCode}）：{result.StandardError}");
            using var output = JsonDocument.Parse(result.StandardOutput);
            var values = output.RootElement;
            if (values.GetArrayLength() != 5 || values[0].GetString() != "v24.22.3" || values[1].GetString() != "7.0.0" ||
                !values[2].GetBoolean() || values[3].GetBoolean() || values[4].GetString() != "测试" || result.StandardError.Trim() != "测试")
                throw new InvalidDataException("Node / is-number 的版本、执行结果或中文输出不符。");
            Status = $"Node 24.22.3 / is-number 7.0.0：数字判断与中文输出正确。\n运行时：{lease.NodePath}\n可在设置 → 外部依赖 → Packages → Node 环境查看来源和存储。";
            _context.Log.Write(_context.PluginId, "Node 环境调用通过：is-number 7.0.0，数字判断结果匹配。");
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { Status = "操作已取消，可稍后重试。"; }
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
