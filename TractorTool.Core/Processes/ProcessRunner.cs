using System.Collections.ObjectModel;
using System.Text;
using TractorTool.Contracts;
using TractorTool.Contracts.Processes;

namespace TractorTool.Core.Processes;

/// <summary>Owns only processes started by this instance. Disposing rejects new work and cancels active runs.</summary>
public sealed class ProcessRunner(string owner, IAppLog log, Func<ProcessRequest, IProcessSession> startProcess)
    : IProcessRunner, IAsyncDisposable
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(3);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly HashSet<Task<ProcessResult>> _runs = [];
    private Task? _shutdown;

    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        var snapshot = Validate(request);
        TaskCompletionSource<ProcessResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_shutdown is not null, this);
            cancellationToken.ThrowIfCancellationRequested();
            _runs.Add(completion.Task);
        }
        // Native process creation and initial synchronous pipe reads must never block the caller's UI thread.
        _ = Task.Run(async () =>
        {
            try
            {
                var operation = ExecuteAsync(snapshot, cancellationToken);
                // Observe the task state directly; do not throw/catch the same cancellation again at this bridge.
                await ((Task)operation).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                if (operation.IsCanceled) completion.TrySetCanceled(cancellationToken.IsCancellationRequested ? cancellationToken : _stopping.Token);
                else if (operation.IsFaulted) completion.TrySetException(operation.Exception!.InnerExceptions);
                else completion.TrySetResult(operation.Result);
            }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally { lock (_gate) _runs.Remove(completion.Task); }
        });
        return completion.Task;
    }

    private async Task<ProcessResult> ExecuteAsync(ProcessRequest request, CancellationToken callerToken)
    {
        using var timeout = new CancellationTokenSource();
        if (request.Timeout is { } duration) timeout.CancelAfter(duration);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(callerToken, _stopping.Token, timeout.Token);
        using var reading = new CancellationTokenSource();
        var token = stopping.Token;
        IProcessSession? process = null;
        TaskCompletionSource<Exception> readFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task readers = Task.CompletedTask;
        try
        {
            token.ThrowIfCancellationRequested();
            process = startProcess(request);
            var output = ReadAsync(process.StandardOutput, request.OutputLimit, reading.Token, readFailure, request.StandardOutputReceived);
            var error = ReadAsync(process.StandardError, request.OutputLimit, reading.Token, readFailure);
            readers = Task.WhenAll(output, error);
            var first = await Task.WhenAny(process.ExitCode, readFailure.Task).WaitAsync(token).ConfigureAwait(false);
            if (first == readFailure.Task)
                throw new IOException("读取工具进程输出失败。", await readFailure.Task.ConfigureAwait(false));
            var exitCode = await process.ExitCode.ConfigureAwait(false);
            // No background descendants may outlive a completed invocation or keep its pipes open.
            using (var cleanup = new CancellationTokenSource(CleanupTimeout))
            {
                try { await process.StopTreeAsync(cleanup.Token).ConfigureAwait(false); }
                catch (OperationCanceledException ex)
                {
                    throw new IOException("工具退出后的进程树清理超过时限或被适配器中断。", ex);
                }
            }
            try { await readers.WaitAsync(CleanupTimeout, token).ConfigureAwait(false); }
            catch (TimeoutException ex) { throw new IOException("工具已退出，但输出管道未关闭，无法确认完整输出。", ex); }
            token.ThrowIfCancellationRequested();
            var stdout = await output.ConfigureAwait(false);
            var stderr = await error.ConfigureAwait(false);
            return new(exitCode, stdout.Text, stderr.Text, stdout.Truncated, stderr.Truncated);
        }
        catch (Exception failure)
        {
            var cleanupError = process is not null
                ? await TerminateAsync(process, readers, reading).ConfigureAwait(false) : null;
            if (cleanupError is not null)
            {
                // Never report a clean cancellation when the process or pipes could not be reclaimed.
                var exception = new IOException("工具进程清理失败；请检查日志，不能确认进程树已停止。",
                    new AggregateException(failure, cleanupError));
                log.Write(owner, "工具进程清理失败（不记录参数和输出内容）", exception);
                throw exception;
            }
            if (failure is OperationCanceledException)
            {
                if (callerToken.IsCancellationRequested || _stopping.IsCancellationRequested) throw;
                if (timeout.IsCancellationRequested) throw new TimeoutException("工具执行超过设定时限。", failure);
                throw new IOException("工具适配器意外中断操作，并非调用方取消或执行超时。", failure);
            }
            throw;
        }
        finally
        {
            if (process is not null) Observe(process.ExitCode);
            process?.Dispose();
        }
    }

    private static async Task<Exception?> TerminateAsync(IProcessSession process, Task readers,
        CancellationTokenSource reading)
    {
        List<Exception> errors = [];
        using var cleanup = new CancellationTokenSource(CleanupTimeout);
        try { await process.StopTreeAsync(cleanup.Token).ConfigureAwait(false); }
        catch (Exception ex) { errors.Add(ex); }
        // A confirmed empty tree closes its pipes naturally; cancelling healthy reads creates needless exceptions.
        if (errors.Count != 0) await reading.CancelAsync().ConfigureAwait(false);
        try { await readers.WaitAsync(CleanupTimeout).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (TimeoutException ex)
        {
            errors.Add(ex);
            await reading.CancelAsync().ConfigureAwait(false);
            process.StandardOutput.Dispose();
            process.StandardError.Dispose();
            Observe(readers);
        }
        catch (Exception) { /* Original read failures are observed by the execution path. */ }
        return errors.Count == 0 ? null : new AggregateException(errors);
    }

    private static void Observe(Task task) => _ = task.ContinueWith(t => _ = t.Exception,
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);

    private static async Task<CapturedOutput> ReadAsync(StreamReader reader, int limit, CancellationToken token,
        TaskCompletionSource<Exception> failure, Action<string>? received = null)
    {
        try
        {
            var text = new StringBuilder(Math.Min(limit, 4096));
            var buffer = new char[4096];
            var truncated = false;
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
            {
                received?.Invoke(new string(buffer, 0, count));
                var retain = Math.Min(count, limit - text.Length);
                text.Append(buffer, 0, retain);
                truncated |= retain != count;
            }
            return new(text.ToString(), truncated);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // This private reader token is cancelled only during failure cleanup, never on a success path.
            return new("", false);
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) failure.TrySetResult(ex);
            throw;
        }
    }

    private static ProcessRequest Validate(ProcessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ExecutablePath) || !Path.IsPathFullyQualified(request.ExecutablePath) ||
            request.ExecutablePath.Contains('\0'))
            throw new ArgumentException("工具路径必须是绝对路径，不通过 PATH 或 shell 查找。", nameof(request));
        ArgumentNullException.ThrowIfNull(request.Arguments);
        var arguments = request.Arguments.ToArray();
        if (arguments.Any(a => a is null || a.Contains('\0')))
            throw new ArgumentException("工具参数不可为 null 或包含 NUL。", nameof(request));
        var executable = Path.GetFullPath(request.ExecutablePath);
        var workingDirectory = request.WorkingDirectory ?? Path.GetDirectoryName(executable)!;
        if (!Path.IsPathFullyQualified(workingDirectory) || workingDirectory.Contains('\0'))
            throw new ArgumentException("工具工作目录必须是绝对路径。", nameof(request));
        if (request.Timeout is { } timeout && (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(nameof(request), "工具超时须大于零且不超过约 49 天。");
        if (request.OutputLimit is < 0 or > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(request), "每路输出保留上限为 0–16 Mi 字符。");
        return request with
        {
            ExecutablePath = executable, Arguments = arguments, WorkingDirectory = Path.GetFullPath(workingDirectory),
            EnvironmentVariables = SnapshotEnvironment(request.EnvironmentVariables)
        };
    }

    private static IReadOnlyDictionary<string, string?>? SnapshotEnvironment(IReadOnlyDictionary<string, string?>? variables)
    {
        if (variables is null) return null;
        var result = new Dictionary<string, string?>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        long characters = 1;
        foreach (var (name, value) in variables)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 32766 || name.Contains('=') || name.Contains('\0') ||
                value is not null && (value.Length > 32766 || value.Contains('\0')))
                throw new ArgumentException("环境变量名称不能为空或包含等号/NUL，值不能包含 NUL；每项名称和值各最多 32766 字符。", nameof(variables));
            characters += name.Length + (value?.Length ?? 0) + 2L;
            if (result.Count >= 4096 || characters > 1024 * 1024)
                throw new ArgumentException("环境变量覆盖最多 4096 项、合计 1 Mi 字符。", nameof(variables));
            if (!result.TryAdd(name, value))
                throw new ArgumentException("环境变量名称重复，请移除大小写别名。", nameof(variables));
        }
        return new ReadOnlyDictionary<string, string?>(result);
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        Task<ProcessResult>[] runs;
        lock (_gate)
        {
            if (_shutdown is not null) return new(_shutdown);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _shutdown = completion.Task;
            runs = _runs.ToArray();
        }
        _ = StopAsync(runs, completion);
        return new(completion.Task);
    }

    private async Task StopAsync(Task<ProcessResult>[] runs, TaskCompletionSource completion)
    {
        try
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
            // Each RunAsync retains its result/error; cleanup failures are already logged.
            await ((Task)Task.WhenAll(runs)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _stopping.Dispose();
            completion.TrySetResult();
        }
        catch (Exception ex) { completion.TrySetException(ex); }
    }

    private sealed record CapturedOutput(string Text, bool Truncated);
}
