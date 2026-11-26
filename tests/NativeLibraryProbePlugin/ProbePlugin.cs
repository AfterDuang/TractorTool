using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TractorTool.Contracts;
using TractorTool.Contracts.Packages;
using TractorTool.UI;

namespace NativeLibraryProbePlugin;

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
        registry.AddNavigationPage("native-probe", "DLL 运行库测试", () => new StackPanel
        {
            Spacing = 16,
            DataContext = model,
            Children =
            {
                new TextBlock { Text = "SDL 3.2.8", Classes = { "sectionTitle" } },
                new TextBlock { Text = "点击后申请官方运行库并读取版本。不会打开媒体、设备或额外窗口。", TextWrapping = TextWrapping.Wrap },
                new Button { Content = "申请运行库并读取版本", Command = model.RunCommand, Classes = { "accent" } },
                new Button { Content = "取消当前操作", Command = model.CancelCommand },
                new TextBlock { [!TextBlock.TextProperty] = new Binding(nameof(ProbeViewModel.Status)), TextWrapping = TextWrapping.Wrap }
            }
        }, new(ToolIconKind.Tools, "验证真实 GitHub DLL 下载、版本调用和退出前占用保护。"));
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        if (_model is { } model) await Dispatcher.UIThread.InvokeAsync(model.StopAsync);
    }

    public ValueTask DisposeAsync() { _model = null; return ValueTask.CompletedTask; }
}

public sealed partial class ProbeViewModel : ObservableObject
{
    private static readonly PackageReference Package = new("SDL3", "3.2.8");
    private readonly PluginContext _context;
    private bool _stopped;
    [ObservableProperty] private string _status = "尚未申请运行库。";
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
        Status = "正在申请 SDL 3.2.8…";
        try
        {
            var packages = _context.Packages ?? throw new NotSupportedException("宿主未提供包服务。");
            using (var lease = await packages.TryRequestAsync(Package, operation.Token))
            {
                if (lease is null) { Status = "本次未安装；可稍后重试。"; return; }
                operation.Token.ThrowIfCancellationRequested();
                var libraryPath = lease.Libraries["SDL3"];
                // Register before any load attempt. A failed or partial native load must remain protected too.
                lease.RetainUntilProcessExit();
                Status = "运行库已准备，正在读取版本…";
                var version = await Task.Run(() => SdlVersionProbe.Read(libraryPath));
                operation.Token.ThrowIfCancellationRequested();
                if (version != 3_002_008) throw new InvalidDataException($"SDL 版本不符：{version}。");
            }
            Status = "SDL 3.2.8 版本检查通过。普通租约已释放；运行库仍保留到软件退出。可到设置 → 外部依赖 → Packages 查看。";
            _context.Log.Write(_context.PluginId, "SDL 3.2.8 版本检查通过。");
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            Status = "操作已取消；若已经尝试加载，运行库仍保留到软件退出。";
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
