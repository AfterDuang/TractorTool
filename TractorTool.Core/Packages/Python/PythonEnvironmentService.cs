using TractorTool.Contracts;
using TractorTool.Contracts.Packages;
using TractorTool.Contracts.Processes;
using TractorTool.Core.Storage;
using static TractorTool.Core.Packages.Python.PythonEnvironmentStorage;

namespace TractorTool.Core.Packages.Python;

public sealed record PythonInstallPrompt(PythonEnvironmentDefinition Definition, string RequestedBy, bool IsRepair);
public sealed record PythonEnvironmentListItem(PythonEnvironmentDefinition Definition, PackagePhase Phase,
    string Error, string Directory, long? InstalledBytes, IReadOnlyList<string> Users, IReadOnlyList<string> RequiredBy,
    bool CanInstall, bool CanRemove, bool CanCancel)
{
    public string Identity => Definition.Identity;
    public bool IsInstalled => Directory.Length != 0;
}
public sealed record PythonMigrationEnvironment(string RelativeDirectory, PythonEnvironmentDefinition Definition);

/// <summary>Concrete locked Python environments. All generations own their interpreter and uv; only download caches are shared.</summary>
public sealed partial class PythonEnvironmentService : IAsyncDisposable
{
    private readonly string _root;
    private readonly string _rid;
    private readonly IProcessRunner _processes;
    private readonly IAppLog _log;
    private readonly Func<string, bool> _sourceAllowed;
    private readonly PythonEnvironmentInstaller _installer;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _operations = [];
    private readonly CancellationTokenSource _stopping = new();
    private PythonEnvironmentDefinition[] _references = [];
    private bool _referencesComplete;
    private string _referenceError = "正在读取 Python 环境引用。";
    private string _storageError = "";
    private Task? _shutdown;
    public event Action? Changed;
    public Func<PythonInstallPrompt, CancellationToken, Task<bool>>? ConfirmInstallAsync { get; set; }
    public string Error { get { lock (_gate) return string.Join("\n", new[] { _storageError, _referenceError }.Where(v => v.Length != 0)); } }

    public PythonEnvironmentService(string packageRoot, string rid, HttpClient http, IProcessRunner processes,
        IAppLog log, Func<string, bool> sourceAllowed)
    {
        packageRoot = AppPaths.Absolute(packageRoot);
        _root = Path.Combine(packageRoot, "uv"); _rid = rid;
        _processes = processes; _log = log; _sourceAllowed = sourceAllowed;
        _installer = new(http, processes, Path.Combine(Path.GetDirectoryName(packageRoot)!, "Cache", "uv"));
        try
        {
            foreach (var item in ReadMigrationEnvironments(packageRoot))
            {
                var directory = Path.Combine(packageRoot, item.RelativeDirectory);
                var record = Read(directory);
                var e = new Entry(item.Definition, directory) { Record = record };
                e.Phase = record.Generation is not null && Same(record.InstalledRoot, Path.Combine(directory, record.Generation))
                    ? PackagePhase.Ready : PackagePhase.Failed;
                if (e.Phase != PackagePhase.Ready) e.Error = "Python 环境未完成，或位置变化后尚未重建。";
                _entries.Add(item.Definition.Identity, e);
            }
        }
        catch (Exception ex)
        {
            _storageError = "Python 环境记录不可用，已保留文件并暂停维护：" + ex.Message;
            _log.Write(AppLogLevel.Warning, "Packages", _storageError, ex);
        }
    }

    public static bool IsOwnedRoot(string uvDirectory) => PythonEnvironmentStorage.IsOwnedRoot(uvDirectory);

    public void UpdateReferences(IReadOnlyList<PythonEnvironmentDefinition> definitions, bool complete, string error)
    {
        var snapshot = definitions.ToArray();
        foreach (var definition in snapshot) PythonEnvironmentDefinitions.Validate(definition);
        lock (_gate)
        {
            _references = snapshot;
            _referencesComplete = complete;
            _referenceError = complete ? "" : error;
        }
        Notify();
    }

    public void InvalidateReferences()
    {
        lock (_gate) { _referencesComplete = false; _referenceError = "插件依赖正在变化，暂停 Python 环境清理。"; }
        Notify();
    }

    public IReadOnlyList<PythonEnvironmentListItem> GetEnvironments()
    {
        lock (_gate)
            return _entries.Values.Where(e => e.Record is not null || e.Phase == PackagePhase.Failed || e.Operation is not null || e.Preparation is not null)
                .OrderBy(e => e.Definition.PluginName).ThenBy(e => e.Definition.Id).Select(e =>
                new PythonEnvironmentListItem(e.Definition, e.Phase, e.Error,
                    e.Record?.Generation is { } generation ? Path.Combine(e.Directory, generation) : "",
                    e.Record?.InstalledBytes, e.Users.Keys.Order().ToArray(),
                    _references.Where(r => r.Identity == e.Definition.Identity).Select(r => $"{r.PluginName} ({r.PluginVersion})").Distinct().ToArray(),
                    CanMaintain(e) && e.Users.Count == 0 && e.Requests == 0 && e.Phase != PackagePhase.Ready,
                    CanRemove(e), e.Cancellation is not null)).ToArray();
    }

    public async Task<IPythonEnvironmentLease?> TryRequestAsync(PythonEnvironmentDefinition definition, string owner,
        CancellationToken token = default, IProcessRunner? runtimeProcesses = null)
    {
        PythonEnvironmentDefinitions.Validate(definition); token.ThrowIfCancellationRequested();
        Entry e; Task<bool> prepare;
        lock (_gate)
        {
            EnsureRunning();
            if (definition.Rid != _rid) throw new NotSupportedException("Python 环境平台不匹配。");
            if (!_entries.TryGetValue(definition.Identity, out e!))
                _entries.Add(definition.Identity, e = new(definition, IdentityDirectory(_root, definition)));
            if (e.Operation is not null && e.Cancellation is null)
                throw new InvalidOperationException("Python 环境正在清理。");
            e.Requests++;
            if (e.Operation is { } operation)
            {
                // A settings-initiated installation already has consent. Join it without taking cancellation ownership.
                prepare = InstallationReadyAsync(operation); Observe(prepare);
            }
            else prepare = e.Phase == PackagePhase.Ready ? Task.FromResult(true) : e.Preparation ?? StartPreparation(e, owner);
        }
        Notify();
        try
        {
            if (!await prepare.WaitAsync(token).ConfigureAwait(false)) return null;
            return await AcquireAsync(e, owner, token, runtimeProcesses ?? _processes).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                e.Requests--;
                if (e.Requests == 0 && token.IsCancellationRequested && e.InstallForRequests) e.Cancellation?.Cancel();
            }
            Notify();
        }
    }

    private Task<bool> StartPreparation(Entry e, string owner)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        e.Preparation = completion.Task;
        _operations.Add(completion.Task);
        _ = Task.Run(async () =>
        {
            Exception? failure = null;
            var accepted = false;
            try
            {
                var confirm = ConfirmInstallAsync ?? throw new InvalidOperationException("宿主尚未连接 Python 环境安装确认。");
                accepted = await confirm(new(e.Definition, owner, e.Record?.Generation is not null), _stopping.Token).ConfigureAwait(false);
                if (accepted)
                {
                    await InstallAsync(e.Definition.Identity, _stopping.Token, requireRequest: true).ConfigureAwait(false);
                }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                lock (_gate)
                {
                    e.Preparation = null; _operations.Remove(completion.Task);
                    if (failure is not null && failure is not OperationCanceledException && e.Phase != PackagePhase.Ready)
                    { e.Phase = PackagePhase.Failed; e.Error = failure.Message; }
                }
                if (failure is OperationCanceledException canceled) completion.TrySetCanceled(canceled.CancellationToken);
                else if (failure is not null) completion.TrySetException(failure);
                else completion.TrySetResult(accepted);
                Notify();
            }
        });
        Observe(completion.Task);
        return completion.Task;
    }

    public Task InstallAsync(string identity, CancellationToken token = default) => InstallAsync(identity, token, requireRequest: false);

    private Task InstallAsync(string identity, CancellationToken token, bool requireRequest)
    {
        token.ThrowIfCancellationRequested();
        Entry e;
        lock (_gate) { EnsureRunning(); e = Find(identity); }
        if (!_sourceAllowed(e.Definition.Source)) throw new InvalidOperationException("请先在依赖来源添加锁文件声明的 PyPI 索引。已装环境可继续离线使用。");
        Task operation;
        lock (_gate)
        {
            EnsureRunning(); RequireStorage();
            if (!_entries.TryGetValue(identity, out var current) || !ReferenceEquals(current, e))
                throw new InvalidOperationException("Python 环境记录已变化，请重新申请。");
            if (_cacheCleaning) throw new InvalidOperationException("Python 缓存正在清理，请完成后重试。");
            if (e.Operation is null)
            {
                // Close the cancellation/source-lookup gap before creating an installation slot.
                if (requireRequest && e.Requests == 0) throw new OperationCanceledException("Python 环境申请已取消。");
                if (e.Users.Count != 0) throw new InvalidOperationException("Python 环境仍在使用，不能修复。");
                EnsureRoot(_root); EnsureIdentity(e.Directory, e.Definition);
                e.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                e.InstallForRequests = requireRequest;
                e.Phase = PackagePhase.Installing; e.Error = "";
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                operation = e.Operation = completion.Task; _operations.Add(operation);
                _ = Task.Run(async () =>
                {
                    Exception? failure = null;
                    try
                    {
                        var record = await _installer.InstallAsync(e.Directory, e.Definition, message =>
                        { lock (_gate) e.Error = message; Notify(); }, e.Cancellation.Token).ConfigureAwait(false);
                        e.Cancellation.Token.ThrowIfCancellationRequested();
                        var prepared = Read(e.Directory);
                        record = record with { GenerationHashes = prepared.GenerationHashes, PreparingGenerations =
                            prepared.PreparingGenerations.Where(g => g != record.Generation).ToList() };
                        Store(e.Directory).Save(record);
                        lock (_gate) { e.Record = record; e.Phase = PackagePhase.Ready; e.Error = ""; }
                        try { CleanGenerations(e.Directory, record); }
                        catch (Exception ex) { _log.Write(AppLogLevel.Warning, "Packages", "Python 新环境已就绪，旧代次清理暂未完成。", ex); }
                        _log.Write("Packages", $"{e.Definition.PluginName}：Python {e.Definition.PythonVersion} 环境已就绪。");
                    }
                    catch (OperationCanceledException ex)
                    {
                        lock (_gate) { e.Phase = PackagePhase.Failed; e.Error = "Python 环境准备已取消。"; }
                        failure = ex;
                    }
                    catch (Exception ex)
                    {
                        lock (_gate) { e.Phase = PackagePhase.Failed; e.Error = ex.Message; }
                        _log.Write("Packages", $"{e.Definition.PluginName}：Python 环境准备失败。", ex);
                        failure = ex;
                    }
                    finally
                    {
                        lock (_gate) { e.Operation = null; e.Cancellation?.Dispose(); e.Cancellation = null; e.InstallForRequests = false; _operations.Remove(completion.Task); }
                        if (failure is OperationCanceledException canceled) completion.TrySetCanceled(canceled.CancellationToken);
                        else if (failure is not null) completion.TrySetException(failure);
                        else completion.TrySetResult();
                        Notify();
                    }
                });
                Observe(operation);
            }
            else if (e.Cancellation is null) throw new InvalidOperationException("Python 环境正在清理。");
            else operation = e.Operation;
        }
        Notify(); return operation.WaitAsync(token);
    }

    public void Cancel(string identity) { lock (_gate) Find(identity).Cancellation?.Cancel(); }

    public Task RemoveAsync(string identity)
    {
        Task operation;
        lock (_gate)
        {
            EnsureRunning(); var e = Find(identity);
            if (!CanRemove(e)) throw new InvalidOperationException("Python 环境仍被引用、使用或状态不明，不能清理。");
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            operation = e.Operation = completion.Task; _operations.Add(operation);
            _ = Task.Run(() =>
            {
                Exception? failure = null;
                try
                {
                    CheckIdentity(e.Directory, e.Definition);
                    var record = Read(e.Directory);
                    var removed = PrepareRemovalDestination(e.Directory, record);
                    lock (_gate)
                    {
                        // Validation can take time. Publish removal under the same gate as new persistent references.
                        EnsureRunning();
                        if (!_referencesComplete || _storageError.Length != 0 || _cacheCleaning ||
                            _references.Any(d => d.Identity == identity) || e.Users.Count != 0 || e.Requests != 0 ||
                            e.Preparation is not null || !ReferenceEquals(e.Operation, completion.Task) ||
                            !_entries.TryGetValue(identity, out var current) || !ReferenceEquals(current, e))
                            throw new InvalidOperationException("Python 环境的引用或使用状态已变化，未清理文件。");
                        Directory.Move(e.Directory, removed);
                        _entries.Remove(identity);
                    }
                    try { DeleteRemovedIdentity(removed, record); }
                    catch (Exception ex) { _log.Write(AppLogLevel.Warning, "Packages", "Python 环境已移出安装目录；暂存文件清理未完成，已保留。", ex); }
                }
                catch (Exception ex) { lock (_gate) e.Error = ex.Message; failure = ex; }
                finally
                {
                    lock (_gate) { e.Operation = null; _operations.Remove(completion.Task); }
                    if (failure is not null) completion.TrySetException(failure); else completion.TrySetResult();
                    Notify();
                }
            });
        }
        Notify(); return operation;
    }

    private async Task<IPythonEnvironmentLease> AcquireAsync(Entry e, string owner, CancellationToken token, IProcessRunner runtimeProcesses)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            EnsureRunning();
            if (e.Phase != PackagePhase.Ready || e.Operation is not null || e.Record is null)
                throw new InvalidOperationException("Python 环境尚未就绪。");
            Reserve(e, owner); _operations.Add(completion.Task);
        }
        Notify();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stopping.Token);
            await ValidateAsync(e.Directory, e.Record, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            return new Lease(this, e, owner, e.Record.InstalledRoot!, runtimeProcesses);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                Release(e, owner);
                if (ex is not OperationCanceledException) { e.Phase = PackagePhase.Failed; e.Error = ex.Message; }
            }
            throw;
        }
        finally { lock (_gate) _operations.Remove(completion.Task); completion.TrySetResult(); Notify(); }
    }

    private bool CanMaintain(Entry e) => _shutdown is null && !_cacheCleaning && _storageError.Length == 0 && e.Operation is null && e.Preparation is null;
    private bool CanRemove(Entry e) => CanMaintain(e) && _referencesComplete && Directory.Exists(e.Directory) && e.Users.Count == 0 && e.Requests == 0 &&
        !_references.Any(d => d.Identity == e.Definition.Identity);
    private Entry Find(string identity) => _entries.TryGetValue(identity, out var entry) ? entry : throw new KeyNotFoundException("Python 环境记录不存在。");
    private void EnsureRunning() => ObjectDisposedException.ThrowIf(_shutdown is not null, this);
    private void RequireStorage() { if (_storageError.Length != 0) throw new InvalidOperationException(_storageError); }
    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static void Reserve(Entry e, string owner) => e.Users[owner] = e.Users.GetValueOrDefault(owner) + 1;
    private static void Release(Entry e, string owner) { if (--e.Users[owner] == 0) e.Users.Remove(owner); }
    private static void Observe(Task task) => _ = task.ContinueWith(t => _ = t.Exception,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    private static async Task<bool> InstallationReadyAsync(Task operation) { await operation.ConfigureAwait(false); return true; }
    private void Notify()
    {
        foreach (var handler in Changed?.GetInvocationList() ?? [])
            try { ((Action)handler)(); } catch (Exception ex) { _log.Write("Packages", "Python 环境状态订阅失败。", ex); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_shutdown ??= Task.Run(async () =>
        {
            await _stopping.CancelAsync();
            Task[] active; lock (_gate) active = _operations.ToArray();
            try { await Task.WhenAll(active).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException) { }
            catch (Exception ex) { _log.Write("Packages", "关闭 Python 环境操作时发生错误。", ex); }
            _stopping.Dispose();
        }));
    }

    private sealed class Entry(PythonEnvironmentDefinition definition, string directory)
    {
        internal PythonEnvironmentDefinition Definition { get; } = definition;
        internal string Directory { get; } = directory;
        internal EnvironmentRecord? Record;
        internal PackagePhase Phase;
        internal string Error = "";
        internal Dictionary<string, int> Users { get; } = [];
        internal int Requests;
        internal Task? Operation;
        internal Task<bool>? Preparation;
        internal CancellationTokenSource? Cancellation;
        internal bool InstallForRequests;
    }

    private sealed class Lease(PythonEnvironmentService service, Entry entry, string owner, string generation, IProcessRunner runtimeProcesses) : IPythonEnvironmentLease
    {
        private bool _disposed;
        public string Directory => Path.Combine(generation, "environment");
        public string PythonPath => Path.Combine(Directory, "Scripts", "python.exe");
        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (!Same(Path.GetFullPath(request.ExecutablePath), PythonPath))
                throw new InvalidOperationException("Python 环境租约只允许调用自身的解释器。");
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (service._gate)
            {
                service.EnsureRunning(); ObjectDisposedException.ThrowIf(_disposed, this);
                Reserve(entry, owner); service._operations.Add(completion.Task);
            }
            service.Notify();
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, service._stopping.Token);
                var environment = request.EnvironmentVariables is null ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) :
                    new Dictionary<string, string?>(request.EnvironmentVariables, StringComparer.OrdinalIgnoreCase);
                // Remove reserved overrides even when the parent process did not define that key.
                foreach (var key in environment.Keys.Where(IsPythonVariable).ToArray()) environment[key] = null;
                foreach (var value in PythonEnvironmentInstaller.CleanEnvironment()) environment[value.Key] = value.Value;
                // Isolated mode excludes user site/PYTHON* and -B keeps immutable installed files free of pyc writes.
                return await runtimeProcesses.RunAsync(request with { Arguments = ["-I", "-B", "-X", "utf8", .. request.Arguments],
                    EnvironmentVariables = environment }, linked.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (service._gate) { Release(entry, owner); service._operations.Remove(completion.Task); }
                completion.TrySetResult(); service.Notify();
            }
        }
        private static bool IsPythonVariable(string key) => key.StartsWith("UV_", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase) || key.StartsWith("PIP_", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("CONDA", StringComparison.OrdinalIgnoreCase) || key.StartsWith("VIRTUAL_ENV", StringComparison.OrdinalIgnoreCase);
        public void Dispose()
        {
            lock (service._gate) { if (_disposed) return; _disposed = true; Release(entry, owner); }
            service.Notify();
        }
    }
}
