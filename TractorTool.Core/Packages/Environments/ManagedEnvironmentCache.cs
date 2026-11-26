using TractorTool.Contracts;
using TractorTool.Contracts.Packages;

namespace TractorTool.Core.Packages.Environments;

internal sealed partial class ManagedEnvironmentService<TDefinition, TPolicy>
{
    private bool _cacheCleaning;
    private string CacheDirectory => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(_root)!)!, "Cache", Policy.Folder);
    public bool CanCleanCache
    {
        get
        {
            lock (_gate) return _shutdown is null && !_cacheCleaning && _storageError.Length == 0 &&
                !_entries.Values.Any(e => e.Operation is not null || e.Preparation is not null) && Directory.Exists(CacheDirectory);
        }
    }

    public Task CleanCacheAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Entry? provider;
        lock (_gate)
        {
            EnsureRunning();
            if (!CanCleanCache) throw new InvalidOperationException("依赖仍在准备或维护，请完成后再清理缓存。");
            if (!EnvironmentCacheStorage<TDefinition, TPolicy>.IsOwned(CacheDirectory)) throw new InvalidDataException("依赖缓存归属不明，已保留文件。");
            _cacheCleaning = true;
            provider = _entries.Values.FirstOrDefault(e => e.Phase == PackagePhase.Ready && e.Record?.Generation is not null);
            if (provider is not null) Reserve(provider, "缓存维护");
            _operations.Add(completion.Task);
        }
        Notify();
        _ = Task.Run(async () =>
        {
            Exception? failure = null;
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stopping.Token);
                EnvironmentCacheStorage<TDefinition, TPolicy>.CheckTree(CacheDirectory);
                string? cleaner = null;
                if (provider is not null)
                {
                    try
                    {
                        await ValidateAsync(provider.Directory, provider.Record!, linked.Token).ConfigureAwait(false);
                        cleaner = provider.Record!.InstalledRoot!;
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
                    { _log.Write(AppLogLevel.Warning, "Packages", "已装工具链无法用于缓存维护，将仅清理已确认归属的下载缓存。", ex); }
                }
                if (cleaner is not null)
                {
                    await Policy.CleanCacheAsync(CacheDirectory, cleaner, _processes, _log, linked.Token).ConfigureAwait(false);
                }
                else
                {
                    // No usable cleaner remains. Copy-mode environments have no cache-backed files;
                    // remove only this explicitly owned cache, under the same installation gate.
                    linked.Token.ThrowIfCancellationRequested();
                    PackageFiles.DeleteOwnedTree(Path.GetDirectoryName(CacheDirectory)!, CacheDirectory, EnvironmentCacheStorage<TDefinition, TPolicy>.Marker);
                }
                _log.Write("Packages", $"{Policy.Name} 下载缓存已清理；已安装环境和插件数据保留。");
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                lock (_gate)
                {
                    if (provider is not null) Release(provider, "缓存维护");
                    _cacheCleaning = false; _operations.Remove(completion.Task);
                }
                if (failure is OperationCanceledException canceled) completion.TrySetCanceled(canceled.CancellationToken);
                else if (failure is not null) completion.TrySetException(failure);
                else completion.TrySetResult();
                Notify();
            }
        });
        Observe(completion.Task);
        return completion.Task;
    }
}
