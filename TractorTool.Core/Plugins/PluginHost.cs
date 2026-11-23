using TractorTool.Contracts;
using TractorTool.Core.Processes;
using TractorTool.Contracts.Processes;
using TractorTool.Core.Storage;
using TractorTool.Core.Packages;
using TractorTool.Core.Tasks;

namespace TractorTool.Core.Plugins;

public enum PluginState { Discovered, Loaded, Disabled, Incompatible, Invalid, Duplicate, Failed }

public sealed class PluginRecord(string directory, PluginManifest? manifest, PluginState state, string detail)
{
    public string Directory { get; } = directory;
    public PluginManifest? Manifest { get; } = manifest;
    public PluginState State { get; internal set; } = state;
    public string Detail { get; internal set; } = detail;
    public string Name => Manifest?.Name ?? Path.GetFileName(Directory);
    public string Summary => $"{Name}   {Manifest?.Version}\n{State} · {Detail}\n{Directory}";
}

public sealed class PluginHost(AppPaths paths, HostCompatibility compatibility, IAppLog log,
    Func<ProcessRequest, IProcessSession>? startProcess = null, ExternalPackageService? packages = null,
    BackgroundTaskService? tasks = null) : IAsyncDisposable
{
    private static readonly TimeSpan StageTimeout = TimeSpan.FromSeconds(3);
    private readonly object _lifecycleGate = new();
    private readonly List<PluginLifetime> _instances = [];
    private Task? _loadTask;
    private Task? _disposeTask;
    private bool _disposed;
    public List<PluginRecord> Records { get; } = [];
    public string? DirectoryError { get; private set; }

    public void Discover(IReadOnlyCollection<string> disabled, IReadOnlyList<ManagedPlugin>? managed = null, string? startupError = null)
    {
        if (_loadTask is not null || _disposed) throw new InvalidOperationException("插件变更需重启生效。");
        Records.Clear();
        DirectoryError = null;
        if (startupError is not null) { DirectoryError = startupError; log.Write(AppLogLevel.Error, "Plugins", startupError); return; }
        try
        {
            ManifestValidator.CheckNoLinks(paths.PluginRoot);
            System.IO.Directory.CreateDirectory(paths.PluginRoot);
            // Report inability to install at the actual AppRoot; never redirect into DataRoot.
            var probe = Path.Combine(paths.PluginRoot, ".write-test-" + Guid.NewGuid().ToString("N"));
            try { using var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DirectoryError = $"插件目录不可写：{paths.PluginRoot}；{ex.Message}"; }
            foreach (var idDirectory in System.IO.Directory.GetDirectories(paths.PluginRoot).Where(p => !Path.GetFileName(p).StartsWith('.')))
            {
                try
                {
                    ManifestValidator.CheckNoLinks(idDirectory);
                    var selection = managed?.FirstOrDefault(p => p.Id.Equals(Path.GetFileName(idDirectory), StringComparison.OrdinalIgnoreCase));
                    if (selection is not null)
                    {
                        if (selection.ActiveVersion is not null)
                        {
                            var versionDirectory = Path.Combine(idDirectory, selection.ActiveVersion);
                            ReadRecord(versionDirectory, selection.Enabled ? [] : [selection.Id]);
                            if (selection.Error.Length != 0)
                            {
                                Records[^1].State = PluginState.Failed;
                                Records[^1].Detail = selection.Error;
                            }
                        }
                    }
                    else foreach (var versionDirectory in System.IO.Directory.GetDirectories(idDirectory)) ReadRecord(versionDirectory, disabled);
                }
                catch (Exception ex) { Records.Add(new(idDirectory, null, PluginState.Invalid, ex.Message)); }
            }
        }
        catch (Exception ex) { DirectoryError = $"无法访问插件目录 {paths.PluginRoot}：{ex.Message}"; }
        foreach (var group in Records.Where(r => r.Manifest is not null).GroupBy(r => r.Manifest!.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            foreach (var record in group) { record.State = PluginState.Duplicate; record.Detail = "同一 ID 有多个候选版本；M1 不猜测活动版本，请仅保留一个。"; }
        foreach (var record in Records) log.Write(record.State switch
        {
            PluginState.Invalid or PluginState.Duplicate or PluginState.Failed => AppLogLevel.Error,
            PluginState.Incompatible => AppLogLevel.Warning,
            _ => AppLogLevel.Info
        }, "Plugins", record.Summary);
        if (DirectoryError is not null) log.Write(AppLogLevel.Error, "Plugins", DirectoryError);
    }

    private void ReadRecord(string directory, IReadOnlyCollection<string> disabled)
    {
        try
        {
            var manifest = ManifestValidator.Read(directory);
            var issue = compatibility.Check(manifest);
            var isDisabled = disabled.Contains(manifest.Id, StringComparer.OrdinalIgnoreCase);
            Records.Add(new(directory, manifest, isDisabled ? PluginState.Disabled : issue is null ? PluginState.Discovered : PluginState.Incompatible,
                isDisabled ? "已禁用，未执行程序集。" : issue ?? "清单校验通过"));
        }
        catch (Exception ex) { Records.Add(new(directory, null, PluginState.Invalid, ex.Message)); }
    }

    /// <param name="register">Host UI validates into a staging registry and commits atomically.</param>
    public Task LoadAsync(Action<PluginRecord, IPlugin> register)
        => LoadWithResourcesAsync((record, plugin) => { register(record, plugin); return null; });

    /// <summary>Registration resources stop with the plugin and finish before its instance is disposed.</summary>
    public Task LoadWithResourcesAsync(Func<PluginRecord, IPlugin, IAsyncDisposable?> register)
    {
        TaskCompletionSource completion;
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loadTask is not null) return _loadTask;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _loadTask = completion.Task;
        }
        // Preserve the caller's UI context for initialization/registration without holding a lock.
        _ = CompleteAsync(LoadCoreAsync(register), completion);
        return completion.Task;
    }

    private async Task LoadCoreAsync(Func<PluginRecord, IPlugin, IAsyncDisposable?> register)
    {
        foreach (var record in Records.Where(r => r.State == PluginState.Discovered).ToArray())
        {
            if (Volatile.Read(ref _disposed)) break;
            PluginLifetime? lifetime = null;
            try
            {
                var manifest = record.Manifest!;
                var path = ManifestValidator.EntryPath(manifest, record.Directory);
                if (!File.Exists(path)) throw new FileNotFoundException("找不到入口程序集", path);
                foreach (var file in PluginFiles.Files(record.Directory))
                    PluginAssemblyPolicy.ValidatePluginFile(file, manifest);
                var assembly = new PluginLoadContext(path, record.Directory).LoadFromAssemblyPath(path);
                if (assembly.GetReferencedAssemblies().Any(a => PluginAssemblyPolicy.IsHostInternal(a.Name)))
                    throw new InvalidDataException("插件引用了宿主或 Core；只允许依赖公开 Contracts/UI SDK。");
                var type = assembly.GetType(manifest.EntryType, throwOnError: true)!;
                if (!typeof(IPlugin).IsAssignableFrom(type) || type.IsAbstract) throw new InvalidDataException("入口类型必须实现 IPlugin。");
                var plugin = (IPlugin)Activator.CreateInstance(type)!;
                lifetime = new(manifest.Id, plugin, log, startProcess ?? (_ =>
                    throw new PlatformNotSupportedException("当前平台尚未提供受管理的工具进程适配器。")));
                lock (_lifecycleGate) _instances.Add(lifetime);
                var token = lifetime.Stopping.Token;
                lifetime.Packages = packages?.CreateScope(manifest.Id, manifest.Dependencies.Select(d => d.Package).ToArray(), token);
                lifetime.Tasks = tasks?.CreateScope(manifest.Id, token);
                if (manifest.Dependencies.Length != 0 && lifetime.Packages is null)
                    throw new InvalidOperationException("宿主未组装外部工具服务。");
                if (Volatile.Read(ref _disposed)) throw new OperationCanceledException("宿主正在退出。");
                var data = paths.GetPluginDataDirectory(manifest.Id);
                System.IO.Directory.CreateDirectory(data);
                await plugin.InitializeAsync(new(manifest.Id, paths, new PluginSettingsStore(data), log, token)
                {
                    Processes = lifetime.Processes,
                    Packages = lifetime.Packages,
                    Tasks = lifetime.Tasks
                }, token);
                // Commit registration and the background task together; shutdown must not race this handoff.
                lock (_lifecycleGate)
                {
                    if (_disposed) throw new OperationCanceledException("宿主正在退出，不再注册插件页面。");
                    token.ThrowIfCancellationRequested();
                    lifetime.Registration = register(record, plugin);
                    if (_disposed) throw new OperationCanceledException("宿主正在退出，不再启动插件活动。");
                    lifetime.Background = plugin is IPluginBackgroundService service
                        ? Task.Run(async () =>
                        {
                            try { log.Write(manifest.Id, "常驻服务开始。"); await service.RunAsync(token); log.Write(manifest.Id, "常驻服务已结束。"); }
                            catch (OperationCanceledException) when (token.IsCancellationRequested) { log.Write(manifest.Id, "常驻服务已停止。"); }
                            catch (Exception ex) { log.Write(manifest.Id, "常驻服务失败", ex); }
                        }) : null;
                    record.State = PluginState.Loaded;
                    record.Detail = "程序集已独立加载，初始化与界面注册完成。";
                }
                log.Write(manifest.Id, record.Detail);
            }
            catch (OperationCanceledException) when (Volatile.Read(ref _disposed))
            {
                record.Detail = "宿主退出，已停止初始化。";
                log.Write(record.Manifest!.Id, record.Detail);
            }
            catch (Exception ex)
            {
                record.State = PluginState.Failed;
                record.Detail = ex.GetBaseException().Message;
                log.Write(record.Manifest!.Id, "插件加载失败", ex);
            }
            finally { lifetime?.Initialized.TrySetResult(); }
            if (lifetime is not null && record.State != PluginState.Loaded)
            {
                // A failed plugin's slow cleanup must not block discovery of the next healthy plugin.
                try { await CleanupAsync(lifetime).WaitAsync(StageTimeout); }
                catch (TimeoutException) { log.Write(AppLogLevel.Warning, lifetime.Id, "加载失败后的清理仍在等待；资源保留，完成后继续释放。"); }
            }
        }
    }

    private Task CleanupAsync(PluginLifetime lifetime)
    {
        lock (_lifecycleGate) return lifetime.Cleanup ??= CleanupCoreAsync(lifetime);
    }

    private async Task CleanupCoreAsync(PluginLifetime lifetime)
    {
        log.Write(lifetime.Id, "开始停止插件及所属任务。");
        var registration = StopRegistrationAsync(lifetime);
        // Cancellation callbacks belong to this instance, so one callback cannot block other plugins.
        var cancellation = CancelAndObserveAsync(lifetime.Stopping, lifetime.Id, "生命周期取消回调");
        // Stop processes even if plugin initialization/StopAsync fails to respond. No page owns this runner.
        var processes = lifetime.Processes.DisposeAsync().AsTask();
        var finiteTasks = lifetime.Tasks?.BeginShutdownAsync() ?? Task.CompletedTask;
        await ObserveStageAsync(lifetime.Initialized.Task, lifetime.Id, "等待初始化结束");
        using var stopCancellation = new CancellationTokenSource();
        Task stopCallbacks = Task.CompletedTask;
        var stop = Task.Run(async () => await lifetime.Plugin.StopAsync(stopCancellation.Token));
        await ObserveStageAsync(stop, lifetime.Id, "停止插件", () =>
            stopCallbacks = CancelAndObserveAsync(stopCancellation, lifetime.Id, "停止超时取消回调"));
        if (lifetime.Background is not null)
            await ObserveStageAsync(lifetime.Background, lifetime.Id, "等待常驻活动结束");
        // Neither a timed-out operation nor a still-running cancellation callback may use disposed resources.
        await cancellation;
        await stopCallbacks;
        await ObserveStageAsync(processes, lifetime.Id, "停止工具进程");
        await ObserveStageAsync(finiteTasks, lifetime.Id, "等待有限任务结束");
        await ObserveStageAsync(registration, lifetime.Id, "等待插件菜单动作结束");
        await ObserveStageAsync(Task.Run(async () => await lifetime.Plugin.DisposeAsync()), lifetime.Id, "释放插件");
        lifetime.Packages?.Dispose();
        lifetime.Tasks?.Dispose();
        lifetime.Stopping.Dispose();
        log.Write(lifetime.Id, "插件停止与资源释放流程结束。");
    }

    private async Task StopRegistrationAsync(PluginLifetime lifetime)
    {
        // A reentrant shutdown can begin while registration is still returning its resource.
        await lifetime.Initialized.Task;
        if (lifetime.Registration is not null) await lifetime.Registration.DisposeAsync();
    }

    private async Task CancelAndObserveAsync(CancellationTokenSource source, string id, string operation)
    {
        try { await ObserveStageAsync(source.CancelAsync(), id, operation); }
        catch (Exception ex) { log.Write(id, operation + "失败", ex); }
    }

    private async Task ObserveStageAsync(Task operation, string id, string description, Action? onTimeout = null)
    {
        try { await operation.WaitAsync(StageTimeout); }
        catch (TimeoutException)
        {
            log.Write(AppLogLevel.Warning, id, description + "超时；保留资源并观察后续完成，不并发释放实例。");
            onTimeout?.Invoke();
            // The host waits a bounded time overall. This continuation safely observes late completion.
            try { await operation; }
            catch (Exception ex) { log.Write(id, description + "延迟结束失败", ex); }
        }
        catch (Exception ex) { log.Write(id, description + "失败", ex); }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        PluginLifetime[] instances;
        Task? loading;
        lock (_lifecycleGate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _disposed = true;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
            instances = _instances.ToArray();
            loading = _loadTask;
        }
        _ = CompleteAsync(DisposeCoreAsync(instances, loading), completion);
        return new(completion.Task);
    }

    private async Task DisposeCoreAsync(PluginLifetime[] instances, Task? loading)
    {
        // Start every instance's stop sequence before waiting on any one plugin.
        var cleanups = instances.Reverse().Select(CleanupAsync).ToList();
        if (loading is not null) cleanups.Add(CleanupAfterLoadingAsync(loading));
        var all = Task.WhenAll(cleanups);
        try { await all.WaitAsync(TimeSpan.FromSeconds(9)); }
        catch (TimeoutException)
        {
            log.Write(AppLogLevel.Warning, "Plugins", "插件退出等待达到上限；尚未结束的操作保留资源并继续观察，不强行并发释放。");
            _ = ObserveRemainderAsync(all);
        }
        catch (Exception ex) { log.Write("Plugins", "插件退出清理失败", ex); }
    }

    private async Task ObserveRemainderAsync(Task operation)
    {
        try { await operation; }
        catch (Exception ex) { log.Write("Plugins", "插件延迟清理失败", ex); }
    }

    private async Task CleanupAfterLoadingAsync(Task loading)
    {
        try { await loading; }
        finally
        {
            // A constructor on another thread may finish after the initial shutdown snapshot.
            // LoadCore has now handed every instance to a cleanup task, including late arrivals.
            PluginLifetime[] instances;
            lock (_lifecycleGate) instances = _instances.ToArray();
            await Task.WhenAll(instances.Select(CleanupAsync));
        }
    }

    private static async Task CompleteAsync(Task operation, TaskCompletionSource completion)
    {
        try { await operation; completion.TrySetResult(); }
        catch (Exception ex) { completion.TrySetException(ex); }
    }

    private sealed class PluginLifetime(string id, IPlugin plugin, IAppLog log,
        Func<ProcessRequest, IProcessSession> startProcess)
    {
        public string Id { get; } = id;
        public IPlugin Plugin { get; } = plugin;
        public ProcessRunner Processes { get; } = new(id, log, startProcess);
        public PackageScope? Packages { get; set; }
        public BackgroundTaskScope? Tasks { get; set; }
        public IAsyncDisposable? Registration { get; set; }
        public CancellationTokenSource Stopping { get; } = new();
        public TaskCompletionSource Initialized { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? Background { get; set; }
        public Task? Cleanup { get; set; }
    }
}
