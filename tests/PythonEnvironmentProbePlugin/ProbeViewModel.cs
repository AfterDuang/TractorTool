using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TractorTool.Contracts;
using TractorTool.Contracts.Processes;
using TractorTool.Contracts.Tasks;

namespace PythonEnvironmentProbePlugin;

public sealed record ProbeSettings : IVersionedSettings
{
    public int SchemaVersion { get; init; } = 1;
    public string Input { get; init; } = "测试\n例子.测试\nexample.com\nbad domain";
}

public sealed partial class ProbeViewModel : ObservableObject
{
    public const int InputLimit = 16_384;
    public static IReadOnlyList<string> Scenarios { get; } = ["正常处理", "持续运行 60 秒", "预期进程失败"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly PluginContext _context;
    private bool _stopped;
    private int _runNumber;
    [ObservableProperty] private string _input;
    [ObservableProperty] private int _selectedScenario;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEdit))] private bool _isRunning;
    [ObservableProperty] private string _status = "尚未申请环境。";
    [ObservableProperty] private string _resultText = "执行后显示每个域名的转换结果。";
    [ObservableProperty] private string _processOutput = "";
    [ObservableProperty] private string _diagnostics = "运行后显示实际解释器、环境及报告路径。";
    public bool CanEdit => !IsRunning && !_stopped;
    public AsyncRelayCommand RunCommand { get; }
    public System.Windows.Input.ICommand CancelCommand { get; }

    public ProbeViewModel(PluginContext context)
    {
        _context = context;
        _input = context.Settings.Load<ProbeSettings>("probe").Input;
        RunCommand = new AsyncRelayCommand(RunAsync, () => !_stopped);
        CancelCommand = RunCommand.CreateCancelCommand();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _context.ApplicationStopping);
        var run = ++_runNumber;
        var output = new StringBuilder();
        var stderr = "";
        IsRunning = true;
        Status = "正在申请 Python 环境…";
        ResultText = "本次尚无完成结果。";
        ProcessOutput = "";
        Diagnostics = "尚未取得本次环境。";
        try
        {
            var input = Input ?? "";
            var domains = input.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (input.Length > InputLimit || domains.Length is < 1 or > 128 || SelectedScenario is < 0 or > 2)
                throw new InvalidDataException("请输入 1–128 个域名，总长度不超过 16 KiB 字符，并选择测试场景。");
            var request = new ProbeRequest(domains, SelectedScenario == 1 ? 60 : 1, SelectedScenario == 2);
            _context.Settings.Save("probe", new ProbeSettings { Input = input });
            var packages = _context.Packages ?? throw new NotSupportedException("宿主未提供依赖服务。");
            var tasks = _context.Tasks ?? throw new NotSupportedException("宿主未提供任务服务。");
            using var lease = await packages.TryRequestPythonAsync("worker", operation.Token);
            if (lease is null) { Status = "本次未安装，可再次执行；未创建后台任务。"; return; }
            operation.Token.ThrowIfCancellationRequested();
            var directory = Path.Combine(_context.Paths.GetPluginDataDirectory(_context.PluginId), "Reports", Guid.NewGuid().ToString("N"));
            var requestPath = Path.Combine(directory, "request.json");
            var resultPath = Path.Combine(directory, "result.json");
            var script = Path.Combine(Path.GetDirectoryName(typeof(ProbePlugin).Assembly.Location)!, "Python", "probe.py");
            Diagnostics = $"解释器：{lease.PythonPath}\n环境：{lease.Directory}\n本次报告目录：{directory}\n输入：request.json\n结果：result.json（仅成功完成后有效）";
            Status = "环境已准备，正在执行；可在日志 → 任务进度查看或取消。";
            var title = request.ExpectedFailure ? "Python：预期进程失败" : request.Iterations == 60 ? "Python：持续运行 60 秒" : "Python：批量域名转换";
            using var task = await tasks.QueueAsync(new(Guid.NewGuid(), title), operation.Token);
            ProbeReport? report = null;
            var snapshot = await task.RunAsync(async (progress, taskToken) =>
            {
                // QueueAsync forwards local and application cancellation through the task's state first.
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, Json), taskToken);
                progress.Report(new(null, "Python 正在处理域名，实时输出见插件页。"));
                var process = await lease.RunAsync(new ProcessRequest(lease.PythonPath, [script, requestPath, resultPath])
                {
                    Timeout = TimeSpan.FromSeconds(90), OutputLimit = InputLimit,
                    StandardOutputReceived = chunk =>
                    {
                        output.Append(chunk);
                        if (output.Length > InputLimit) output.Remove(0, output.Length - InputLimit);
                        var text = output.ToString();
                        Dispatcher.UIThread.Post(() => { if (run == _runNumber && IsRunning) ProcessOutput = text; });
                    }
                }, taskToken);
                stderr = process.StandardError;
                taskToken.ThrowIfCancellationRequested();
                if (process.ExitCode != 0)
                {
                    if (request.ExpectedFailure && process.ExitCode == 17 && stderr.Contains("PYTHON_PROBE_EXPECTED_FAILURE", StringComparison.Ordinal))
                        throw new InvalidOperationException("预期失败：Python 已按所选场景主动以退出码 17 结束。可切回正常处理后重试。");
                    throw new InvalidOperationException($"Python 进程失败（退出码 {process.ExitCode}）：{stderr.Trim()}");
                }
                if (request.ExpectedFailure) throw new InvalidDataException("预期失败场景却正常退出，测试未通过。");
                if (process.StandardOutputTruncated || process.StandardErrorTruncated ||
                    !process.StandardOutput.Contains("处理完成，结果已写入插件报告目录。", StringComparison.Ordinal) || process.StandardError.Trim() != "中文诊断输出：测试")
                    throw new InvalidDataException("Python 输出被截断或中文输出不符。");
                report = JsonSerializer.Deserialize<ProbeReport>(await File.ReadAllTextAsync(resultPath, taskToken), Json)
                    ?? throw new InvalidDataException("Python 结果为空。");
                ValidateReport(report, request, lease.PythonPath);
                return resultPath;
            });
            Status = snapshot.State switch
            {
                BackgroundTaskState.Succeeded => "处理完成，结果已核对并保存。",
                BackgroundTaskState.Cancelled => "操作已取消，可再次执行；本次未产生有效完成结果。",
                _ => "操作失败：" + (snapshot.Error ?? snapshot.Message)
            };
            if (snapshot.State == BackgroundTaskState.Succeeded && report is not null)
            {
                ResultText = string.Join("\n", report.Results.Select(r => r.Error is null ? $"{r.Domain} → {r.Ascii}" : $"{r.Domain}：无效域名（{r.Error}）"));
                Diagnostics += $"\n实际 Python：{report.PythonVersion} / idna {report.DependencyVersion}\n进程 PID：{report.Pid}\n完成轮数：{report.Iterations}";
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { Status = "操作已取消，可再次执行。"; }
        catch (Exception ex)
        {
            Status = "操作失败：" + ex.Message;
            _context.Log.Write(_context.PluginId, Status, ex);
        }
        finally
        {
            IsRunning = false;
            ProcessOutput = Tail(output.ToString() + (stderr.Length == 0 ? "" : "\n[stderr]\n" + stderr));
        }
    }

    private static void ValidateReport(ProbeReport report, ProbeRequest request, string pythonPath)
    {
        if (report.PythonVersion != "3.13.18" || report.DependencyVersion != "3.11" || report.Pid <= 0 ||
            report.Iterations != request.Iterations || !string.Equals(report.PythonPath, pythonPath, StringComparison.OrdinalIgnoreCase) ||
            report.Results is null || report.Results.Length != request.Domains.Length ||
            report.Results.Where((result, index) => result is null || result.Domain != request.Domains[index] ||
                (result.Error is null ? string.IsNullOrWhiteSpace(result.Ascii) : result.Ascii is not null || string.IsNullOrWhiteSpace(result.Error))).Any())
            throw new InvalidDataException("结果中的解释器、依赖版本、轮数或域名记录与本次请求不符。");
    }

    private static string Tail(string text) => text.Length <= InputLimit ? text : text[^InputLimit..];

    public async Task StopAsync()
    {
        _stopped = true;
        OnPropertyChanged(nameof(CanEdit));
        RunCommand.NotifyCanExecuteChanged();
        RunCommand.Cancel();
        if (RunCommand.ExecutionTask is { } running) await running;
    }

    private sealed record ProbeRequest(string[] Domains, int Iterations, bool ExpectedFailure);
    private sealed record ProbeReport(string PythonVersion, string DependencyVersion, string PythonPath, int Pid, int Iterations, DomainResult[] Results);
    private sealed record DomainResult(string Domain, string? Ascii, string? Error);
}
