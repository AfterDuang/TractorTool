using TractorTool.Contracts;
using TractorTool.Contracts.Tasks;
using TractorTool.Core.Storage;

namespace TractorTool.Core.Tasks;

/// <summary>Finite task summaries and cancellation. Execution and ordering remain the caller's responsibility.</summary>
public sealed class BackgroundTaskService
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly JsonFileStore<TaskHistory> _store;
    private readonly IAppLog _log;
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly Task _initialization;
    private bool _loaded;
    private bool _closed;
    private Task? _shutdown;
    private string? _persistenceError;

    public BackgroundTaskService(string storePath, IAppLog log)
    {
        _log = log;
        _store = new(storePath, TaskHistory.Validate);
        // Loading and durable writes never perform synchronous file I/O on the UI caller.
        _initialization = Task.Run(InitializeAsync);
    }

    public event EventHandler? Changed;
    public IReadOnlyList<BackgroundTaskSnapshot> Snapshot
    {
        get { lock (_sync) return _entries.Values.Select(e => e.Value).OrderByDescending(s => s.CreatedAt).ToArray(); }
    }
    public bool HasActive { get { lock (_sync) return _entries.Values.Any(e => Active(e.Value.State)); } }
    public string? PersistenceError { get { lock (_sync) return _persistenceError; } }

    public BackgroundTaskScope CreateScope(string pluginId, CancellationToken stoppingToken = default)
    {
        AppPaths.ValidateId(pluginId);
        lock (_sync) ObjectDisposedException.ThrowIf(_closed, this);
        return new(this, pluginId, stoppingToken);
    }

    public void Cancel(Guid id)
    {
        Entry? entry;
        lock (_sync) _entries.TryGetValue(id, out entry);
        if (entry is not null) Cancel(entry);
    }

    public Task BeginShutdownAsync()
    {
        lock (_sync)
        {
            if (_shutdown is not null) return _shutdown;
            _closed = true;
            return _shutdown = Task.Run(async () =>
            {
                await _initialization;
                Entry[] entries;
                lock (_sync) entries = _entries.Values.Where(e => e.Owner is not null).ToArray();
                foreach (var entry in entries) Cancel(entry);
                await Task.WhenAll(entries.Select(e => e.Completion.Task));
                await FlushAsync();
            });
        }
    }

    public async Task FlushAsync()
    {
        await _initialization;
        await _writes.WaitAsync();
        try { await EnsureLoadedAsync(); await SaveAsync(); }
        finally { _writes.Release(); Notify(); }
    }

    private async Task InitializeAsync()
    {
        await _writes.WaitAsync();
        try { await EnsureLoadedAsync(); }
        catch (Exception ex) { ReportPersistence(ex); }
        finally { _writes.Release(); Notify(); }
    }

    // An invalid file stays untouched. Retrying after repair reloads it before allowing new work.
    private async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        TaskHistory history;
        try { history = await Task.Run(_store.Load); }
        catch (Exception ex) { ReportPersistence(ex); throw; }
        lock (_sync)
        {
            foreach (var saved in history.Tasks)
            {
                var value = Active(saved.State) ? saved with
                {
                    State = BackgroundTaskState.Interrupted, Message = "上次运行中断，未自动重试。",
                    FinishedAt = DateTimeOffset.UtcNow
                } : saved;
                var entry = new Entry(this, null, value);
                entry.Completion.TrySetResult(value);
                entry.DisposeResources();
                _entries.Add(value.Id, entry);
            }
            _loaded = true;
            TrimHistory();
        }
        if (history.Tasks.Any(s => Active(s.State))) await SaveAsync();
        else ClearPersistenceError();
    }

    internal async Task<IReadOnlyList<IBackgroundTask>> QueueBatchAsync(BackgroundTaskScope owner, IReadOnlyList<BackgroundTaskRequest> requests,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var batch = requests.ToArray();
        if (batch.Length == 0 || batch.Any(request => request is null || request.BatchId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 512) || batch.Any(request => request.BatchId != batch[0].BatchId))
            throw new ArgumentException("批次不能为空，须使用同一批次标识与不超过 512 字符的任务名称。", nameof(requests));
        cancellationToken.ThrowIfCancellationRequested();
        await _initialization;
        await _writes.WaitAsync(cancellationToken);
        Entry[] entries = [];
        try
        {
            await EnsureLoadedAsync();
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_closed || owner.Closed, owner);
                cancellationToken.ThrowIfCancellationRequested();
                entries = batch.Select(request => new Entry(this, owner,
                    new(Guid.NewGuid(), owner.PluginId, request.BatchId, request.Title,
                        BackgroundTaskState.Queued, null, "等待执行", null, null, DateTimeOffset.UtcNow, null))).ToArray();
                foreach (var entry in entries) _entries.Add(entry.Id, entry);
            }
            try { await SaveAsync(); }
            catch
            {
                lock (_sync)
                    foreach (var entry in entries)
                    {
                        _entries.Remove(entry.Id);
                        entry.Value = entry.Value with { State = BackgroundTaskState.Failed, Message = "任务记录保存失败", FinishedAt = DateTimeOffset.UtcNow };
                        // Cancellation may already be finishing this entry. All paths share cleanup,
                        // which resumes only after this submission releases the write gate.
                        entry.Finishing ??= Task.Run(() => FinishAsync(entry));
                    }
                throw;
            }
            // Registration may synchronously cancel; the callback only schedules asynchronous persistence.
            foreach (var entry in entries) entry.CallerCancellation = cancellationToken.Register(entry.Cancel);
            lock (_sync) foreach (var entry in entries) entry.Ready = true;
            bool stopped;
            lock (_sync) stopped = owner.Closed || _closed;
            if (stopped) foreach (var entry in entries) entry.Cancel();
            return entries;
        }
        finally { _writes.Release(); Notify(); }
    }

    internal async Task StopScopeAsync(BackgroundTaskScope owner)
    {
        Entry[] entries;
        lock (_sync)
        {
            owner.Closed = true;
            entries = _entries.Values.Where(e => ReferenceEquals(e.Owner, owner)).ToArray();
        }
        foreach (var entry in entries) Cancel(entry);
        await Task.WhenAll(entries.Select(e => e.Completion.Task));
    }

    private void Cancel(Entry entry)
    {
        bool queued;
        lock (_sync)
        {
            if (!Active(entry.Value.State)) return;
            queued = entry.Value.State == BackgroundTaskState.Queued;
            entry.Value = entry.Value with
            {
                State = queued ? BackgroundTaskState.Cancelled : BackgroundTaskState.Cancelling,
                Message = queued ? "已取消" : "正在取消", FinishedAt = queued ? DateTimeOffset.UtcNow : null
            };
            entry.Cancellation ??= Task.Run(async () =>
            {
                try { await entry.Stop.CancelAsync(); }
                catch (Exception ex) { _log.Write(entry.Value.PluginId, "任务取消回调失败", ex); }
            });
            if (queued) entry.Finishing ??= Task.Run(() => FinishAsync(entry));
            else _ = Task.Run(PersistObservedAsync);
        }
        Notify();
    }

    private Task<BackgroundTaskSnapshot> RunAsync(Entry entry,
        Func<IProgress<BackgroundTaskProgress>, CancellationToken, Task<string?>> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        lock (_sync)
        {
            if (!entry.Ready) throw new InvalidOperationException("任务记录尚未提交。");
            if (!Active(entry.Value.State)) return entry.Completion.Task;
            if (entry.Started) throw new InvalidOperationException("同一任务只能执行一次。");
            entry.Started = true;
            // Run on the thread pool so neither a callback nor persistence blocks the UI.
            _ = Task.Run(() => RunCoreAsync(entry, execute));
            return entry.Completion.Task;
        }
    }

    private async Task RunCoreAsync(Entry entry, Func<IProgress<BackgroundTaskProgress>, CancellationToken, Task<string?>> execute)
    {
        try
        {
            await _writes.WaitAsync();
            try
            {
                lock (_sync)
                {
                    if (entry.Value.State != BackgroundTaskState.Queued) return;
                    entry.Value = entry.Value with { State = BackgroundTaskState.Running, Message = "正在执行" };
                }
                // A running summary must also be durable before invoking plugin code.
                await SaveAsync();
            }
            finally { _writes.Release(); Notify(); }
            lock (_sync)
                if (entry.Value.State == BackgroundTaskState.Cancelling)
                    throw new OperationCanceledException(entry.Stop.Token);
            entry.Stop.Token.ThrowIfCancellationRequested();
            var result = await execute(new DirectProgress(p => ReportProgress(entry, p)), entry.Stop.Token);
            lock (_sync)
                entry.Value = entry.Value with
                {
                    State = BackgroundTaskState.Succeeded, Percent = 100, Message = "已完成",
                    Result = Limit(result), FinishedAt = DateTimeOffset.UtcNow
                };
        }
        catch (OperationCanceledException) when (entry.Stop.IsCancellationRequested || entry.Value.State == BackgroundTaskState.Cancelling)
        {
            lock (_sync) entry.Value = entry.Value with { State = BackgroundTaskState.Cancelled, Message = "已取消", FinishedAt = DateTimeOffset.UtcNow };
        }
        catch (Exception ex)
        {
            lock (_sync) entry.Value = entry.Value with { State = BackgroundTaskState.Failed, Message = "执行失败", Error = Limit(ex.GetBaseException().Message), FinishedAt = DateTimeOffset.UtcNow };
            _log.Write(entry.Value.PluginId, "有限任务失败", ex);
        }
        finally
        {
            Task finishing;
            lock (_sync) finishing = entry.Finishing ??= Task.Run(() => FinishAsync(entry));
            await finishing;
        }
    }

    private void ReportProgress(Entry entry, BackgroundTaskProgress progress)
    {
        if (progress is null) return;
        lock (_sync)
        {
            if (entry.Value.State != BackgroundTaskState.Running) return;
            entry.Value = entry.Value with { Percent = progress.Percent is double p && double.IsFinite(p) ? Math.Clamp(p, 0, 100) : null, Message = Limit(progress.Message) ?? "" };
        }
        Notify(); // Progress is coalesced in memory, not written for each frame.
    }

    private async Task FinishAsync(Entry entry)
    {
        if (entry.Cancellation is not null) await entry.Cancellation;
        await PersistObservedAsync();
        entry.DisposeResources();
        lock (_sync) { entry.Completion.TrySetResult(entry.Value); TrimHistory(); }
        Notify();
    }

    private async Task PersistObservedAsync()
    {
        await _writes.WaitAsync();
        try { await SaveAsync(); }
        catch (Exception ex) { ReportPersistence(ex); }
        finally { _writes.Release(); Notify(); }
    }

    private async Task SaveAsync()
    {
        TaskHistory history;
        lock (_sync)
        {
            // Do not remove handles until they finish cleanup; only the persisted summaries are bounded here.
            history = new() { Tasks = _entries.Values.Select(e => e.Value).Where(s => Active(s.State))
                .Concat(_entries.Values.Select(e => e.Value).Where(s => !Active(s.State)).OrderByDescending(s => s.FinishedAt).Take(100)).ToArray() };
        }
        try { await Task.Run(() => _store.Save(history)); ClearPersistenceError(); }
        catch (Exception ex) { ReportPersistence(ex); throw; }
    }

    private void TrimHistory()
    {
        var remove = _entries.Values.Where(e => !Active(e.Value.State) && (e.Owner is null || e.Completion.Task.IsCompleted))
            .OrderByDescending(e => e.Value.FinishedAt).Skip(100).Select(e => e.Id).ToArray();
        foreach (var id in remove) _entries.Remove(id);
    }

    private void ReportPersistence(Exception error)
    {
        lock (_sync) _persistenceError = error.GetBaseException().Message;
        _log.Write("Tasks", "任务记录保存或读取失败；原文件保留，新任务必须成功记录后才能执行。", error);
    }
    private void ClearPersistenceError() { lock (_sync) _persistenceError = null; }
    private void Notify()
    {
        var listeners = Changed;
        if (listeners is null) return;
        foreach (EventHandler listener in listeners.GetInvocationList())
            try { listener(this, EventArgs.Empty); }
            catch (Exception ex) { _log.Write("Tasks", "任务状态订阅者失败", ex); }
    }

    private static bool Active(BackgroundTaskState state) => state is BackgroundTaskState.Queued or BackgroundTaskState.Running or BackgroundTaskState.Cancelling;
    private static string? Limit(string? value) => value is { Length: > 4096 } ? value[..4096] : value;

    private sealed class DirectProgress(Action<BackgroundTaskProgress> report) : IProgress<BackgroundTaskProgress>
    {
        public void Report(BackgroundTaskProgress value) => report(value);
    }

    private sealed class Entry(BackgroundTaskService service, BackgroundTaskScope? owner, BackgroundTaskSnapshot value) : IBackgroundTask
    {
        private int _resourcesDisposed;
        public Guid Id => Value.Id;
        public BackgroundTaskScope? Owner { get; } = owner;
        public BackgroundTaskSnapshot Value { get; set; } = value;
        public CancellationTokenSource Stop { get; } = new();
        public CancellationTokenRegistration CallerCancellation { get; set; }
        public TaskCompletionSource<BackgroundTaskSnapshot> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? Cancellation { get; set; }
        public Task? Finishing { get; set; }
        public bool Ready { get; set; }
        public bool Started { get; set; }
        public void Cancel() => service.Cancel(this);
        public Task<BackgroundTaskSnapshot> RunAsync(Func<IProgress<BackgroundTaskProgress>, CancellationToken, Task<string?>> execute) => service.RunAsync(this, execute);
        public void Dispose() => Cancel();
        public void DisposeResources()
        {
            if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0) return;
            CallerCancellation.Dispose();
            Stop.Dispose();
        }
    }
}

public sealed class BackgroundTaskScope : IBackgroundTaskService, IDisposable
{
    private readonly BackgroundTaskService _service;
    private readonly CancellationTokenRegistration _stopping;
    internal string PluginId { get; }
    internal bool Closed { get; set; }
    internal BackgroundTaskScope(BackgroundTaskService service, string pluginId, CancellationToken stoppingToken)
    {
        _service = service;
        PluginId = pluginId;
        _stopping = stoppingToken.Register(Dispose);
    }
    public async Task<IBackgroundTask> QueueAsync(BackgroundTaskRequest request, CancellationToken cancellationToken = default) =>
        (await QueueBatchAsync([request], cancellationToken))[0];
    public Task<IReadOnlyList<IBackgroundTask>> QueueBatchAsync(IReadOnlyList<BackgroundTaskRequest> requests,
        CancellationToken cancellationToken = default) => _service.QueueBatchAsync(this, requests, cancellationToken);
    public Task BeginShutdownAsync() => _service.StopScopeAsync(this);
    public void Dispose()
    {
        // StopScopeAsync observes task completion itself; it does not throw for task failure.
        _ = BeginShutdownAsync();
        _stopping.Unregister();
    }
}

public sealed class TaskHistory : IVersionedSettings
{
    public int SchemaVersion { get; init; } = 1;
    public BackgroundTaskSnapshot[] Tasks { get; init; } = [];
    public static void Validate(TaskHistory history)
    {
        if (history.Tasks is null || history.Tasks.Any(s => s is null) || history.Tasks.Select(s => s.Id).Distinct().Count() != history.Tasks.Length)
            throw new InvalidDataException("任务摘要为空或标识重复；原文件已保留。");
        foreach (var task in history.Tasks)
        {
            AppPaths.ValidateId(task.PluginId);
            if (task.Id == Guid.Empty || task.BatchId == Guid.Empty || string.IsNullOrWhiteSpace(task.Title) || task.Title.Length > 512 ||
                !Enum.IsDefined(task.State) || task.Percent is double p && (!double.IsFinite(p) || p < 0 || p > 100) ||
                task.Message is null || task.Message.Length > 4096 || task.Result?.Length > 4096 || task.Error?.Length > 4096 ||
                task.CreatedAt == default ||
                (task.State is BackgroundTaskState.Queued or BackgroundTaskState.Running or BackgroundTaskState.Cancelling) != (task.FinishedAt is null))
                throw new InvalidDataException("任务摘要无效；原文件已保留。");
        }
    }
}
