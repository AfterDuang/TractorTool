using TractorTool.Contracts;
using TractorTool.Contracts.Packages;
using TractorTool.Contracts.Tasks;
using TractorTool.Plugins.FolderHistory.Models;
using TractorTool.Plugins.FolderHistory.Processing;
using TractorTool.Plugins.FolderHistory.Storage;

namespace TractorTool.Plugins.FolderHistory.Services;

public sealed class FolderHistoryService : IDisposable
{
    private readonly PluginContext _context;
    private readonly CancellationTokenSource _stopping;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly object _lifetime = new();
    private readonly HashSet<Task> _pending = [];
    private bool _stopped;
    private FolderRegistry _registry;

    public FolderHistoryService(PluginContext context)
    {
        _context = context;
        _registry = context.Settings.Load<FolderRegistry>("folders");
        if (_registry.SchemaVersion != 1 || _registry.Folders is null ||
            _registry.Folders.Any(folder => folder is null || folder.Id == Guid.Empty || string.IsNullOrWhiteSpace(folder.Root)) ||
            _registry.Folders.Select(folder => folder.Id).Distinct().Count() != _registry.Folders.Count ||
            _registry.Folders.Select(folder => folder.Root).Distinct(HistoryPaths.Comparer).Count() != _registry.Folders.Count)
            throw new InvalidDataException("文件夹关联列表不兼容或已损坏；原配置已保留。");
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(context.ApplicationStopping);
    }

    public Task<IReadOnlyList<FolderInfo>> GetFoldersAsync() => ExclusiveAsync<IReadOnlyList<FolderInfo>>(_ =>
    {
        var result = new List<FolderInfo>();
        foreach (var folder in _registry.Folders)
        {
            try
            {
                var state = Open(folder.Root);
                var versions = ReadCommitted(folder.Root, state);
                result.Add(new(folder.Root, folder.Id, versions.Count == 0 ? "尚无完整版本" : "手动保存",
                    versions.Count, versions.FirstOrDefault()?.Version.CreatedAt));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
            {
                result.Add(new(folder.Root, folder.Id, "需要处理：" + ex.Message, 0, null));
            }
        }
        return Task.FromResult<IReadOnlyList<FolderInfo>>(result);
    });

    public Task AddFolderAsync(string root) => ExclusiveAsync(async token =>
    {
        root = HistoryPaths.NormalizeRoot(root, _context.Paths.AppRoot, _context.Paths.DataRoot);
        if (_registry.Folders.Any(folder => HistoryPaths.Overlaps(folder.Root, root)))
            throw new InvalidOperationException("该目录已关联，或与另一个关联目录互相包含。");
        RepositoryState state;
        if (HistoryPaths.Attributes(HistoryPaths.HistoryDirectory(root)) is null)
        {
            await WorkingTree.ScanAsync(root, token).ConfigureAwait(false);
            state = HistoryStore.Create(root);
        }
        else state = HistoryStore.Read(root);
        using var ownership = HistoryStore.Lock(root);
        if (_registry.Folders.Any(folder => folder.Id == state.Id))
            throw new InvalidOperationException("已有相同身份的历史库。移动目录后请先移除旧关联，再添加新位置。");
        SaveRegistry(_registry.Folders.Append(new(root, state.Id)).ToList());
        return true;
    });

    public Task<IReadOnlyList<SavedVersion>> GetVersionsAsync(string root) => ExclusiveAsync(token =>
    {
        _ = Open(root);
        using var ownership = HistoryStore.Lock(root);
        token.ThrowIfCancellationRequested();
        return Task.FromResult(ReadCommitted(root, Open(root)));
    });

    public Task SaveAsync(string root, string note) => ExclusiveAsync(async token =>
    {
        if (note.Length > 500) throw new ArgumentException("版本说明不能超过 500 个字符。", nameof(note));
        _ = Open(root);
        using var lease = await RequestEngineAsync(token).ConfigureAwait(false);
        _ = Open(root);
        using var ownership = HistoryStore.Lock(root);
        var state = Open(root);
        var engine = new DuplicatiEngine(_context.Processes!, lease.Tools[DuplicatiEngine.ToolName], _context.Log);
        var history = HistoryPaths.HistoryDirectory(root);
        await RunTaskAsync("保存文件夹版本 · " + Path.GetFileName(root), async (progress, cancellation) =>
        {
            progress.Report(new(null, "正在核对工作文件"));
            var before = await WorkingTree.ScanAsync(root, cancellation).ConfigureAwait(false);
            var previous = ReadCommitted(root, state);
            if (previous.FirstOrDefault() is { } last && HistoryComparison.Equal(before, last.Version.Entries))
                return "文件内容和目录结构没有变化，保留现有版本。";
            progress.Report(new(null, "正在保存版本"));
            var created = await engine.BackupAsync(root, history, cancellation).ConfigureAwait(false);
            progress.Report(new(null, "正在校验保存结果"));
            var after = await WorkingTree.ScanAsync(root, cancellation).ConfigureAwait(false);
            if (!HistoryComparison.Equal(before, after) || !HistoryComparison.Equal(after, created.Entries))
                throw new IOException("保存期间文件发生变化，或引擎结果不完整。本次未登记为可用版本，请停止编辑后重试。");
            cancellation.ThrowIfCancellationRequested();
            state.Versions.Add(new(created.Id, root, note.Trim(), false));
            HistoryStore.Save(root, state);
            return "已保存并核对文件清单：" + created.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        }, token).ConfigureAwait(false);
        return true;
    });

    public Task<string> ExportAsync(string root, string versionId, string parentDirectory, string? relativePath = null) =>
        ExclusiveAsync(async token =>
        {
            _ = Open(root);
            var parent = HistoryPaths.Absolute(parentDirectory);
            HistoryPaths.RejectHistoryContainer(parent);
            HistoryPaths.RequireLocalVolume(parent);
            HistoryPaths.RequireDirectory(parent);
            using var lease = await RequestEngineAsync(token).ConfigureAwait(false);
            _ = Open(root);
            using var ownership = HistoryStore.Lock(root);
            var state = Open(root);
            var saved = ReadCommitted(root, state).SingleOrDefault(item => item.Id == versionId)
                ?? throw new InvalidOperationException("选定版本不可用，请刷新历史列表。");
            if (relativePath is not null)
            {
                relativePath = HistoryPaths.NormalizeRelative(relativePath);
                if (!saved.Version.Entries.Any(item => HistoryPaths.Comparer.Equals(item.Path, relativePath)))
                    throw new InvalidOperationException("这个路径在选定版本中不存在；请改选含有该文件的版本。");
            }
            var engine = new DuplicatiEngine(_context.Processes!, lease.Tools[DuplicatiEngine.ToolName], _context.Log);
            var history = HistoryPaths.HistoryDirectory(root);
            var staging = Path.Combine(history, "temp", "restore-" + Guid.NewGuid().ToString("N"));
            var destination = Path.Combine(parent, Path.GetFileName(root) + "-" + saved.Version.CreatedAt.ToLocalTime().ToString("yyyyMMdd-HHmmss") +
                "-" + Guid.NewGuid().ToString("N")[..8]);
            if (_registry.Folders.Any(folder => HistoryPaths.Overlaps(destination, folder.Root)) ||
                HistoryPaths.Overlaps(destination, _context.Paths.AppRoot) || HistoryPaths.Overlaps(destination, _context.Paths.DataRoot))
                throw new InvalidOperationException("另存位置应与受管理目录、软件目录和应用数据目录互不包含。");
            await RunTaskAsync("另存历史版本 · " + Path.GetFileName(root), async (progress, cancellation) =>
            {
                progress.Report(new(null, "正在提取并校验历史内容"));
                await engine.RestoreAsync(saved.Version, history, staging, cancellation).ConfigureAwait(false);
                progress.Report(new(null, "正在另存到新目录"));
                await RestoreTransaction.SaveAsAsync(staging, destination, relativePath, saved.Version.Entries, cancellation).ConfigureAwait(false);
                return destination;
            }, token).ConfigureAwait(false);
            // A failed extraction is retained for diagnosis; successful staging contains only verified entries.
            try { RemoveStaging(staging, saved.Version.Entries); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                _context.Log.Write(AppLogLevel.Warning, _context.PluginId, "另存已完成，历史库内的提取暂存未能清理。", ex);
            }
            return destination;
        });

    public Task SetPinnedAsync(string root, string versionId, bool pinned) => ExclusiveAsync(_ =>
    {
        Open(root);
        using var ownership = HistoryStore.Lock(root);
        var state = Open(root);
        var index = state.Versions.FindIndex(version => version.Id == versionId);
        if (index < 0) throw new InvalidOperationException("选定版本已不可用。");
        state.Versions[index] = state.Versions[index] with { Pinned = pinned };
        HistoryStore.Save(root, state);
        return Task.FromResult(true);
    });

    public Task RemoveFolderAsync(string root) => ExclusiveAsync(_ =>
    {
        SaveRegistry(_registry.Folders.Where(folder => !HistoryPaths.Comparer.Equals(folder.Root, root)).ToList());
        return Task.FromResult(true);
    });

    public async Task StopAsync()
    {
        Task[] pending;
        lock (_lifetime) { _stopped = true; pending = [.. _pending]; }
        await _stopping.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    public void Dispose() { _stopping.Dispose(); _operation.Dispose(); }

    private Task<T> ExclusiveAsync<T>(Func<CancellationToken, Task<T>> operation)
    {
        lock (_lifetime)
        {
            if (_stopped) return Task.FromCanceled<T>(new CancellationToken(true));
            var token = _stopping.Token;
            var task = Task.Run(async () =>
            {
                await _operation.WaitAsync(token).ConfigureAwait(false);
                try { token.ThrowIfCancellationRequested(); return await operation(token).ConfigureAwait(false); }
                finally { _operation.Release(); }
            }, token);
            _pending.Add(task);
            _ = task.ContinueWith(completed => { lock (_lifetime) _pending.Remove(completed); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }

    private RepositoryState Open(string root)
    {
        root = HistoryPaths.NormalizeRoot(root, _context.Paths.AppRoot, _context.Paths.DataRoot);
        var registration = _registry.Folders.SingleOrDefault(folder => HistoryPaths.Comparer.Equals(folder.Root, root))
            ?? throw new InvalidOperationException("目录尚未关联。");
        if (_registry.Folders.Any(folder => folder.Id != registration.Id && HistoryPaths.Overlaps(root, folder.Root)))
            throw new InvalidDataException("关联目录互相包含，已停止操作。");
        var state = HistoryStore.Read(root);
        if (state.Id != registration.Id) throw new InvalidDataException("这个位置的历史库身份已改变，请先移除旧关联再重新添加。");
        return state;
    }

    private static IReadOnlyList<SavedVersion> ReadCommitted(string root, RepositoryState state)
    {
        var result = new List<SavedVersion>();
        foreach (var receipt in state.Versions)
        {
            var version = DuplicatiEngine.ReadVersion(HistoryPaths.HistoryDirectory(root), receipt.Id);
            if (version is null || !HistoryPaths.Comparer.Equals(receipt.SourceRoot, version.SourceRoot))
                throw new InvalidDataException("已保存版本缺失或来源不符，请保留历史库并检查磁盘内容。");
            result.Add(new(version, receipt.Note, receipt.Pinned));
        }
        return result.OrderByDescending(item => item.Version.CreatedAt).ToArray();
    }

    private void SaveRegistry(List<FolderRegistration> folders)
    {
        var updated = new FolderRegistry { Folders = folders };
        _context.Settings.Save("folders", updated);
        _registry = updated;
    }

    private async Task<IPackageLease> RequestEngineAsync(CancellationToken token)
    {
        if (_context.Processes is null || _context.Packages is null || _context.Tasks is null)
            throw new InvalidOperationException("宿主缺少所需的依赖、进程或任务接口。");
        return await _context.Packages.TryRequestAsync(new(DuplicatiEngine.PackageId, DuplicatiEngine.PackageVersion), token).ConfigureAwait(false)
            ?? throw new OperationCanceledException("未准备历史工具，本次操作已取消。", token);
    }

    private async Task RunTaskAsync(string title, Func<IProgress<BackgroundTaskProgress>, CancellationToken, Task<string?>> execute,
        CancellationToken token)
    {
        using var task = await _context.Tasks!.QueueAsync(new(Guid.NewGuid(), title), token).ConfigureAwait(false);
        using var stop = token.Register(task.Cancel);
        var result = await task.RunAsync(execute).ConfigureAwait(false);
        if (result.State is BackgroundTaskState.Cancelled or BackgroundTaskState.Interrupted)
            throw new OperationCanceledException("操作已取消。", token);
        if (result.State != BackgroundTaskState.Succeeded) throw new IOException(result.Error ?? "历史操作未完成。");
    }

    private static void RemoveStaging(string directory, IReadOnlyList<HistoryEntry> entries)
    {
        HistoryPaths.RequireDirectory(directory);
        foreach (var entry in entries.OrderByDescending(item => item.Path.Length))
        {
            var path = HistoryPaths.ResolveWorkPath(directory, entry.Path);
            if (entry.IsDirectory) Directory.Delete(path, recursive: false);
            else File.Delete(path);
        }
        Directory.Delete(directory);
    }
}
