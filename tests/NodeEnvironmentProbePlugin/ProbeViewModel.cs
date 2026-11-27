using System.Globalization;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TractorTool.Contracts;
using TractorTool.Contracts.Packages;
using TractorTool.Contracts.Processes;
using TractorTool.Contracts.Tasks;

namespace NodeEnvironmentProbePlugin;

public sealed class ProbeSettings : IVersionedSettings
{
    public int SchemaVersion { get; init; } = 1;
    public string Input { get; init; } = "42\n3.5\n-10\n1e3\nnot a number\nInfinity\n测试";
}

public sealed partial class ProbeViewModel : ObservableObject
{
    private const int OutputLimit = 16000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static string[] Scenarios { get; } = ["正常处理", "持续运行 60 秒", "预期进程失败"];
    private readonly PluginContext _context;
    private bool _stopped;
    private int _runId;
    [ObservableProperty] private string _input;
    [ObservableProperty] private int _scenarioIndex;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEdit))] private bool _isBusy;
    [ObservableProperty] private string _status = "尚未申请环境。";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _resultText = "运行后显示逐行判断及统计。";
    [ObservableProperty] private string _output = "";
    [ObservableProperty] private string _paths;
    public bool CanEdit => !IsBusy && !_stopped;
    public AsyncRelayCommand RunCommand { get; }
    public System.Windows.Input.ICommand CancelCommand { get; }

    public ProbeViewModel(PluginContext context)
    {
        _context = context;
        _input = context.Settings.Load<ProbeSettings>("probe").Input;
        _paths = "插件数据：" + context.Paths.GetPluginDataDirectory(context.PluginId);
        RunCommand = new AsyncRelayCommand(RunAsync, () => !_stopped);
        CancelCommand = RunCommand.CreateCancelCommand();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _context.ApplicationStopping);
        var runId = ++_runId;
        IsBusy = true; Progress = 0; Output = ""; ResultText = "本次尚无处理结果。";
        Paths = "尚未取得本次环境。";
        try
        {
            var input = Input ?? "";
            var values = input.Split('\n').Select(line => line.Trim()).Where(line => line.Length != 0).ToArray();
            if (input.Length > 16000 || values.Length is 0 or > 200)
                throw new InvalidDataException("请输入 1–200 行，合计不超过 16000 个字符。");
            var scenario = ScenarioIndex switch { 0 => "normal", 1 => "long", 2 => "fail", _ => throw new InvalidDataException("请选择测试场景。") };
            var title = "Node 数值处理 · " + Scenarios[ScenarioIndex];
            _context.Settings.Save("probe", new ProbeSettings { Input = input });
            var packages = _context.Packages ?? throw new NotSupportedException("宿主未提供依赖服务。");
            var tasks = _context.Tasks ?? throw new NotSupportedException("宿主未提供任务服务。");
            Status = "正在申请 Node 环境；首次需要确认下载。";
            using var lease = await packages.TryRequestNodeAsync("worker", operation.Token);
            if (lease is null) { Status = "本次未安装，没有建立任务；可再次申请。"; return; }
            operation.Token.ThrowIfCancellationRequested();
            var runDirectory = Path.Combine(_context.Paths.GetPluginDataDirectory(_context.PluginId), "Reports", Guid.NewGuid().ToString("N"));
            var requestFile = Path.Combine(runDirectory, "request.json");
            var resultFile = Path.Combine(runDirectory, "result.json");
            Paths = $"运行时：{lease.NodePath}\n环境：{lease.Directory}\n本次输入：{requestFile}\n本次结果：{resultFile}";
            Status = "环境已准备，正在执行 Node 任务…";
            using var task = await tasks.QueueAsync(new(Guid.NewGuid(), title), operation.Token);
            NumberReport? report = null;
            var snapshot = await task.RunAsync(async (progress, token) =>
            {
                report = await ExecuteAsync(lease, values, scenario, requestFile, resultFile, progress, runId, token);
                return resultFile;
            });
            Status = snapshot.State switch
            {
                BackgroundTaskState.Succeeded => "处理完成，结果已核对并保存。",
                BackgroundTaskState.Cancelled => "操作已取消；进程已收尾，可重新运行。本次目录可能只保留输入。",
                _ => "任务失败：" + snapshot.Error
            };
            if (snapshot.State == BackgroundTaskState.Succeeded)
            {
                ResultText = FormatResult(report!);
                Progress = 100;
            }
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
        finally { ++_runId; IsBusy = false; }
    }

    private async Task<NumberReport> ExecuteAsync(INodeEnvironmentLease lease, string[] values, string scenario,
        string requestFile, string resultFile, IProgress<BackgroundTaskProgress> progress, int runId, CancellationToken token)
    {
        var runDirectory = Path.GetDirectoryName(requestFile)!;
        Directory.CreateDirectory(runDirectory);
        await File.WriteAllTextAsync(requestFile, JsonSerializer.Serialize(new { values, scenario }, Json), token);
        var script = Path.Combine(Path.GetDirectoryName(typeof(ProbePlugin).Assembly.Location)!, "Node", "probe.cjs");
        var pendingLine = "";
        var streaming = true;
        var result = await lease.RunAsync(new ProcessRequest(lease.NodePath,
            [script, Path.Combine(lease.Directory, "package.json"), requestFile, resultFile])
        {
            WorkingDirectory = runDirectory, Timeout = TimeSpan.FromSeconds(90), OutputLimit = OutputLimit,
            StandardOutputReceived = chunk =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_runId != runId || !streaming) return;
                    var combined = Output + chunk;
                    Output = combined.Length > OutputLimit ? combined[^OutputLimit..] : combined;
                });
                pendingLine += chunk;
                while (pendingLine.IndexOf('\n') is var newline && newline >= 0)
                {
                    var line = pendingLine[..newline].Trim();
                    pendingLine = pendingLine[(newline + 1)..];
                    var parts = line.Split(' ');
                    if (parts.Length == 3 && parts[0] == "PROGRESS" && int.TryParse(parts[1], out var step) &&
                        int.TryParse(parts[2], out var total) && total > 0 && step >= 0 && step <= total)
                    {
                        var percent = 99d * step / total;
                        progress.Report(new(percent, $"真实 Node 进程已处理 {step}/{total} 轮"));
                        Dispatcher.UIThread.Post(() => { if (_runId == runId && streaming) Progress = percent; });
                    }
                }
            }
        }, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            streaming = false;
            var combined = result.StandardOutput + "\n标准错误：\n" + result.StandardError;
            Output = combined.Length > OutputLimit ? combined[^OutputLimit..] : combined;
        });
        if (result.ExitCode != 0)
            throw new InvalidOperationException(result.ExitCode == 42 && scenario == "fail" &&
                result.StandardError.Trim() == "预期进程失败：这是用户选择的测试场景。"
                ? "预期进程失败：Node 按测试场景主动返回退出码 42。切换为正常处理后可重试。"
                : $"Node 退出码 {result.ExitCode}：{result.StandardError.Trim()}");
        if (scenario == "fail" || result.StandardOutputTruncated || result.StandardErrorTruncated)
            throw new InvalidDataException("Node 执行结果与场景不符或输出被截断。");
        if (!result.StandardOutput.Contains("处理完成：", StringComparison.Ordinal) || result.StandardError.Trim() != "中文诊断：Node 进程正常结束。")
            throw new InvalidDataException("Node 标准输出或诊断输出的中文内容不符。");
        var report = JsonSerializer.Deserialize<NumberReport>(await File.ReadAllTextAsync(resultFile, token), Json)
            ?? throw new InvalidDataException("Node 未返回结果。");
        if (report.Schema != 1 || report.NodeVersion != "v24.22.3" || report.PackageVersion != "7.0.0" || report.ProcessId <= 0 ||
            report.Scenario != scenario || !string.Equals(report.ExecutablePath, lease.NodePath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(report.EnvironmentPackageJson, Path.Combine(lease.Directory, "package.json"), StringComparison.OrdinalIgnoreCase) ||
            report.Rows is null || report.Rows.Length != values.Length || report.Rows.Any(row => row is null) ||
            !report.Rows.Select(row => row.Input).SequenceEqual(values) || report.ValidCount != report.Rows.Count(row => row.Valid) ||
            report.InvalidCount != values.Length - report.ValidCount ||
            report.Rows.Any(row => row.Valid ? row.Value is not { } value || !double.IsFinite(value) : row.Value is not null) ||
            report.Sum is { } sum && !double.IsFinite(sum) || report.Mean is { } mean && !double.IsFinite(mean))
            throw new InvalidDataException("Node 版本、环境或处理结果不符，未将本次任务标记为成功。");
        token.ThrowIfCancellationRequested();
        return report;
    }

    private static string FormatResult(NumberReport report) =>
        $"有效 {report.ValidCount} 项；无效 {report.InvalidCount} 项\n合计：{Number(report.Sum)}；均值：{Number(report.Mean)}" +
        (report.AggregateError is null ? "" : "\n" + report.AggregateError) + "\n\n" +
        string.Join('\n', report.Rows.Select(row => row.Valid ? $"{row.Input} → {Number(row.Value)}" : $"{row.Input} → 不是有限数值"));

    private static string Number(double? value) => value?.ToString("G17", CultureInfo.InvariantCulture) ?? "无可用结果";

    public async Task StopAsync()
    {
        _stopped = true;
        RunCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(CanEdit));
        RunCommand.Cancel();
        if (RunCommand.ExecutionTask is { } running) await running;
    }

    private sealed record NumberRow(string Input, bool Valid, double? Value);
    private sealed record NumberReport(int Schema, string NodeVersion, string PackageVersion, int ProcessId,
        string ExecutablePath, string EnvironmentPackageJson, string Scenario, NumberRow[] Rows,
        int ValidCount, int InvalidCount, double? Sum, double? Mean, string? AggregateError);
}
