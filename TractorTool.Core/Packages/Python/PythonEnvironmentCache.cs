using TractorTool.Contracts;
using TractorTool.Contracts.Packages;
using TractorTool.Contracts.Processes;
using TractorTool.Core.Storage;
using static TractorTool.Core.Packages.Python.PythonEnvironmentStorage;

namespace TractorTool.Core.Packages.Python;

internal static class PythonCacheStorage
{
    internal const string Marker = ".python-cache";
    private const string Owner = "TractorTool.PythonCache.v1";
    internal static bool IsOwned(string directory)
    {
        PackageFiles.CheckPath(directory);
        var file = Path.Combine(directory, Marker); PackageFiles.CheckPath(file);
        return File.Exists(file) && new FileInfo(file).Length == Owner.Length && File.ReadAllText(file) == Owner;
    }
    internal static void Ensure(string directory)
    {
        lock (FileLocks.For(directory))
        {
            if (IsOwned(directory)) { CheckTree(directory); return; }
            if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
                throw new InvalidDataException("Cache/uv 含未登记内容，未覆盖缓存目录。");
            Directory.CreateDirectory(directory);
            var pending = Path.Combine(Path.GetDirectoryName(directory)!, ".uv-cache-owner.tmp");
            PackageFiles.CheckPath(pending);
            using (var stream = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None))
            { stream.Write(PythonEnvironmentDefinitions.Utf8.GetBytes(Owner)); stream.Flush(true); }
            File.Move(pending, Path.Combine(directory, Marker));
        }
    }
    internal static void CheckTree(string directory)
    {
        PackageFiles.CheckPath(directory);
        if (!Directory.Exists(directory)) return;
        string[] entries;
        try { entries = Directory.GetFileSystemEntries(directory); }
        // Concurrent uv installs can retire their own cache scratch directories.
        // Link and access errors are still rejected; only an ordinary disappearance is harmless.
        catch (DirectoryNotFoundException) { return; }
        foreach (var path in entries)
        {
            PackageFiles.CheckPath(path);
            if (Directory.Exists(path)) CheckTree(path);
        }
    }
}

public sealed partial class PythonEnvironmentService
{
    private bool _cacheCleaning;
    private string CacheDirectory => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(_root)!)!, "Cache", "uv");
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
            if (!CanCleanCache) throw new InvalidOperationException("Python 依赖仍在准备或维护，请完成后再清理缓存。");
            if (!PythonCacheStorage.IsOwned(CacheDirectory)) throw new InvalidDataException("Python 缓存归属不明，已保留文件。");
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
                PythonCacheStorage.CheckTree(CacheDirectory);
                string? cleaner = null;
                if (provider is not null)
                {
                    try
                    {
                        await ValidateAsync(provider.Directory, provider.Record!, linked.Token).ConfigureAwait(false);
                        var candidates = Directory.GetFiles(Path.Combine(provider.Record!.InstalledRoot!, "uv"), "uv.exe", SearchOption.AllDirectories);
                        if (candidates.Length == 1) cleaner = candidates[0];
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
                    { _log.Write(AppLogLevel.Warning, "Packages", "已装 uv 无法用于缓存维护，将仅清理已确认归属的下载缓存。", ex); }
                }
                if (cleaner is not null)
                {
                    var generation = provider!.Record!.InstalledRoot!;
                    var variables = PythonEnvironmentInstaller.CleanEnvironment();
                    variables["UV_CREDENTIALS_DIR"] = Path.Combine(CacheDirectory, "credentials");
                    variables["NETRC"] = Path.Combine(generation, "empty.netrc");
                    var result = await _processes.RunAsync(new(cleaner, ["cache", "clean", "--cache-dir",
                        Path.Combine(CacheDirectory, "content"), "--no-config", "--offline"])
                        { WorkingDirectory = generation, EnvironmentVariables = variables, Timeout = TimeSpan.FromMinutes(5) }, linked.Token).ConfigureAwait(false);
                    if (result.ExitCode != 0) throw new IOException("Python 缓存清理失败：" + result.StandardError);
                }
                else
                {
                    // No usable cleaner remains. Copy-mode environments have no cache-backed files;
                    // remove only this explicitly owned cache, under the same installation gate.
                    linked.Token.ThrowIfCancellationRequested();
                    PackageFiles.DeleteOwnedTree(Path.GetDirectoryName(CacheDirectory)!, CacheDirectory, PythonCacheStorage.Marker);
                }
                _log.Write("Packages", "Python 下载缓存已清理；已安装环境和插件数据保留。");
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
