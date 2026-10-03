using TractorTool.Contracts;
using TractorTool.Core.Storage;

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

public sealed class PluginHost(AppPaths paths, HostCompatibility compatibility, IAppLog log) : IAsyncDisposable
{
    private static readonly TimeSpan StageTimeout = TimeSpan.FromSeconds(3);
    private readonly object _lifecycleGate = new();
    private readonly List<PluginLifetime> _instances = [];
    private Task? _loadTask;
    private Task? _disposeTask;
    private bool _disposed;
    public List<PluginRecord> Records { get; } = [];
    public string? DirectoryError { get; private set; }

    public void Discover(IReadOnlyCollection<string> disabled)
    {
        if (_loadTask is not null || _disposed) throw new InvalidOperationException("插件变更需重启生效。");
        Records.Clear();
        DirectoryError = null;
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
                    foreach (var versionDirectory in System.IO.Directory.GetDirectories(idDirectory)) ReadRecord(versionDirectory, disabled);
                }
                catch (Exception ex) { Records.Add(new(idDirectory, null, PluginState.Invalid, ex.Message)); }
            }
        }
        catch (Exception ex) { DirectoryError = $"无法访问插件目录 {paths.PluginRoot}：{ex.Message}"; }
        foreach (var group in Records.Where(r => r.Manifest is not null).GroupBy(r => r.Manifest!.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            foreach (var record in group) { record.State = PluginState.Duplicate; record.Detail = "同一 ID 有多个候选版本；M1 不猜测活动版本，请仅保留一个。"; }
        foreach (var record in Records) log.Write("Plugins", record.Summary);
        if (DirectoryError is not null) log.Write("Plugins", DirectoryError);
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

    private async Task LoadCoreAsync(Action<PluginRecord, IPlugin> register)
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
                foreach (var file in System.IO.Directory.EnumerateFiles(record.Directory, "*.dll"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (PluginLoadContext.Shared(name) || name is "TractorTool.Core" or "TractorTool")
                        throw new InvalidDataException($"插件包包含宿主共享程序集 {name}，请移除副本并使用宿主 SDK。");
                }
                var assembly = new PluginLoadContext(path, record.Directory).LoadFromAssemblyPath(path);
                if (assembly.GetReferencedAssemblies().Any(a => a.Name is "TractorTool" or "TractorTool.Core"))
                    throw new InvalidDataException("插件引用了宿主或 Core；只允许依赖公开 Contracts/UI SDK。");
                var type = assembly.GetType(manifest.EntryType, throwOnError: true)!;
                if (!typeof(IPlugin).IsAssignableFrom(type) || type.IsAbstract) throw new InvalidDataException("入口类型必须实现 IPlugin。");
                var plugin = (IPlugin)Activator.CreateInstance(type)!;
                lifetime = new(manifest.Id, plugin);
                lock (_lifecycleGate) _instances.Add(lifetime);
                var token = lifetime.Stopping.Token;
                if (Volatile.Read(ref _disposed)) throw new OperationCanceledException("宿主正在退出。");
                var data = paths.GetPluginDataDirectory(manifest.Id);
                System.IO.Directory.CreateDirectory(data);
                await plugin.InitializeAsync(new(manifest.Id, paths, new PluginSettingsStore(data), log, token), token);
                // Commit registration and the background task together; shutdown must not race this handoff.
                lock (_lifecycleGate)
                {
                    if (_disposed) throw new OperationCanceledException("宿主正在退出，不再注册插件页面。");
                    token.ThrowIfCancellationRequested();
                    register(record, plugin);
                    lifetime.Background = plugin is IPluginBackgroundService service
                        ? Task.Run(async () =>
                        {
                            try { await service.RunAsync(token); }
                            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                            catch (Exception ex) { log.Write(manifest.Id, "常驻服务失败", ex); }
                        }) : null;
                    record.State = PluginState.Loaded;
                    record.Detail = "程序集已独立加载，初始化与页面注册完成。";
                }
                log.Write(manifest.Id, record.Detail);
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
                catch (TimeoutException) { log.Write(lifetime.Id, "加载失败后的清理仍在等待；资源保留，完成后继续释放。"); }
            }
        }
    }

    private Task CleanupAsync(PluginLifetime lifetime)
    {
        lock (_lifecycleGate) return lifetime.Cleanup ??= CleanupCoreAsync(lifetime);
    }

    private async Task CleanupCoreAsync(PluginLifetime lifetime)
    {
        // Cancellation callbacks belong to this instance, so one callback cannot block other plugins.
        var cancellation = CancelAndObserveAsync(lifetime.Stopping, lifetime.Id, "生命周期取消回调");
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
        await ObserveStageAsync(Task.Run(async () => await lifetime.Plugin.DisposeAsync()), lifetime.Id, "释放插件");
        lifetime.Stopping.Dispose();
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
            log.Write(id, description + "超时；保留资源并观察后续完成，不并发释放实例。");
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
            log.Write("Plugins", "插件退出等待达到上限；尚未结束的操作保留资源并继续观察，不强行并发释放。");
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

    private sealed class PluginLifetime(string id, IPlugin plugin)
    {
        public string Id { get; } = id;
        public IPlugin Plugin { get; } = plugin;
        public CancellationTokenSource Stopping { get; } = new();
        public TaskCompletionSource Initialized { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? Background { get; set; }
        public Task? Cleanup { get; set; }
    }
}
