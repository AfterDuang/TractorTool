using TractorTool.Contracts;
using TractorTool.Contracts.Packages;
using TractorTool.Contracts.Tasks;
using TractorTool.Plugins.FolderHistory.Models;
using TractorTool.Plugins.FolderHistory.Processing;
using TractorTool.Plugins.FolderHistory.Storage;

namespace TractorTool.Plugins.FolderHistory.Services;

public sealed partial class FolderHistoryService : IDisposable
{
    private readonly PluginContext _context;
    private readonly CancellationTokenSource _stopping;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly object _lifetime = new();
    private readonly HashSet<Task> _pending = [];
    private bool _stopped;
    private FolderRegistry _registry;

    public event EventHandler? Changed;

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

    public Task<IReadOnlyList<FolderInfo>> GetFoldersAsync() => ExclusiveAsync<IReadOnlyList<FolderInfo>>(token =>
    {
        var result = new List<FolderInfo>();
        foreach (var folder in _registry.Folders)
        {
            token.ThrowIfCancellationRequested();
            RepositoryState? state = null;
            var pendingRestore = false;
            try
            {
                _ = Open(folder.Root);
                using var ownership = HistoryStore.Lock(folder.Root);
                state = Open(folder.Root);
                pendingRestore = InPlaceRestoreTransaction.ReadPending(folder.Root) is not null;
                var versions = ReadCommitted(folder.Root, state);
                var status = pendingRestore ? "恢复未完成，自动保存已暂停" : state.PendingDeleteIds.Count > 0 ? "历史清理未完成，请检查后重试" :
                    RuntimeStatus(folder.Root) ?? (state.ProtectionEnabled ? "等待核对" : versions.Count == 0 ? "尚无完整版本" : "已暂停");
                result.Add(new(folder.Root, folder.Id, status, versions.Count, versions.FirstOrDefault()?.Version.CreatedAt)
                {
                    ProtectionEnabled = state.ProtectionEnabled,
                    HasPendingRestore = pendingRestore,
                    HasPendingCleanup = state.PendingDeleteIds.Count > 0,
                    NeedsAttention = RuntimeFaulted(folder.Root),
                    HistoryBytes = MeasureDirectory(HistoryPaths.HistoryDirectory(folder.Root), token)
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Add(new(folder.Root, folder.Id, "需要处理：" + ex.GetBaseException().Message, 0, null)
                {
                    NeedsAttention = true,
                    ProtectionEnabled = state?.ProtectionEnabled ?? false,
                    HasPendingRestore = pendingRestore,
                    HasPendingCleanup = state?.PendingDeleteIds.Count > 0
                });
            }
        }
        return Task.FromResult<IReadOnlyList<FolderInfo>>(result);
    });

    public Task<FolderPreparation> InspectFolderAsync(string root, FolderSettings? initialSettings = null)
    {
        initialSettings = HistoryStore.ValidateSettings(initialSettings ?? new());
        return ExclusiveAsync(async token =>
        {
        root = HistoryPaths.NormalizeRoot(root, _context.Paths.AppRoot, _context.Paths.DataRoot);
        var existing = HistoryPaths.Attributes(HistoryPaths.HistoryDirectory(root)) is not null;
        if (existing)
        {
            _ = HistoryStore.Read(root);
            return new FolderPreparation(root, 0, 0, 0, HistoryPaths.HistoryDirectory(root), AvailableBytes(root), true)
                { WorkingTreeScanned = false };
        }
        var settings = initialSettings;
        var entries = await WorkingTree.ScanAsync(root, settings.ExcludedPaths, token).ConfigureAwait(false);
        return new FolderPreparation(root, entries.Count(entry => !entry.IsDirectory), entries.Count(entry => entry.IsDirectory),
            entries.Sum(entry => entry.Length), HistoryPaths.HistoryDirectory(root), AvailableBytes(root), existing);
        });
    }

    public Task AddFolderAsync(string root, FolderSettings? initialSettings = null)
    {
        initialSettings = HistoryStore.ValidateSettings(initialSettings ?? new());
        return ExclusiveAsync(async token =>
        {
        root = HistoryPaths.NormalizeRoot(root, _context.Paths.AppRoot, _context.Paths.DataRoot);
        if (_registry.Folders.Any(folder => HistoryPaths.Overlaps(folder.Root, root)))
            throw new InvalidOperationException("该目录已关联，或与另一个关联目录互相包含。");
        RepositoryState state;
        if (HistoryPaths.Attributes(HistoryPaths.HistoryDirectory(root)) is null)
        {
            await WorkingTree.ScanAsync(root, initialSettings.ExcludedPaths, token).ConfigureAwait(false);
            state = HistoryStore.Create(root, initialSettings);
        }
        else state = HistoryStore.Read(root);
        using var ownership = HistoryStore.Lock(root);
        state = HistoryStore.Read(root);
        if (_registry.Folders.Any(folder => folder.Id == state.Id))
            throw new InvalidOperationException("已有相同身份的历史库。移动目录后请先移除旧关联，再添加新位置。");
        if (state.ProtectionEnabled)
        {
            state.ProtectionEnabled = false;
            HistoryStore.Save(root, state);
        }
        InPlaceRestoreTransaction.RebindPending(root);
        SaveRegistry(_registry.Folders.Append(new(root, state.Id)).ToList());
        NotifyChanged();
        return true;
        });
    }

    public Task<IReadOnlyList<SavedVersion>> GetVersionsAsync(string root) => ExclusiveAsync(token =>
    {
        _ = Open(root);
        using var ownership = HistoryStore.Lock(root);
        token.ThrowIfCancellationRequested();
        return Task.FromResult(ReadCommitted(root, Open(root)));
    });

    public Task<FolderSettings> GetSettingsAsync(string root) => ExclusiveAsync(_ =>
    {
        var settings = Open(root).Settings;
        return Task.FromResult(settings with { ExcludedPaths = [.. settings.ExcludedPaths] });
    });

    public Task SaveSettingsAsync(string root, FolderSettings settings)
    {
        settings = HistoryStore.ValidateSettings(settings);
        return ExclusiveAsync(token =>
        {
            _ = Open(root);
            using var ownership = HistoryStore.Lock(root);
            var state = Open(root);
            RequireNoPendingOperation(root, state);
            state.Settings = settings;
            HistoryStore.Save(root, state);
            ConfigureObservation(root, state, recheck: true);
            NotifyChanged();
            return Task.FromResult(true);
        });
    }

    public Task SetProtectionEnabledAsync(string root, bool enabled) => ExclusiveAsync(async token =>
    {
        _ = Open(root);
        if (enabled)
        {
            using var lease = await RequestEngineAsync(token).ConfigureAwait(false);
            using var ownership = HistoryStore.Lock(root);
            var state = Open(root);
            RequireNoPendingOperation(root, state);
            await CaptureAsync(root, state, Engine(lease), "开启保护", false, token).ConfigureAwait(false);
            state.ProtectionEnabled = true;
            HistoryStore.Save(root, state);
            ConfigureObservation(root, state, recheck: true);
        }
        else
        {
            using var ownership = HistoryStore.Lock(root);
            var state = Open(root);
            state.ProtectionEnabled = false;
            HistoryStore.Save(root, state);
            ConfigureObservation(root, state, recheck: false);
        }
        NotifyChanged();
        return true;
    });

    public Task SaveAsync(string root, string note) => ExclusiveAsync(async token =>
    {
        if (note.Length > 500) throw new ArgumentException("版本说明不能超过 500 个字符。", nameof(note));
        _ = Open(root);
        using var lease = await RequestEngineAsync(token).ConfigureAwait(false);
        using var ownership = HistoryStore.Lock(root);
        var state = Open(root);
        RequireNoPendingOperation(root, state);
        await CaptureAsync(root, state, Engine(lease), note.Trim(), false, token).ConfigureAwait(false);
        ConfigureObservation(root, state, recheck: true);
        NotifyChanged();
        return true;
    });

    private async Task<SavedVersion> CaptureAsync(string root, RepositoryState state, DuplicatiEngine engine,
        string note, bool protection, CancellationToken token, bool automatic = false)
    {
        SavedVersion? saved = null;
        try
        {
        await RunTaskAsync((protection ? "保留恢复前版本 · " : "保存文件夹版本 · ") + Path.GetFileName(root), async (progress, cancellation) =>
        {
            SetRuntimeStatus(root, "正在保存");
            progress.Report(new(null, "正在核对工作文件"));
            var exclusions = protection ? Array.Empty<string>() : state.Settings.ExcludedPaths;
            var before = await WorkingTree.ScanAsync(root, exclusions, cancellation).ConfigureAwait(false);
            var previous = ReadCommitted(root, state);
            if (previous.FirstOrDefault() is { } last && HistoryComparison.Equal(before, last.Version.Entries))
            {
                if (protection)
                {
                    var index = state.Versions.FindIndex(item => item.Id == last.Id);
                    state.Versions[index] = state.Versions[index] with { Pinned = true, RestoreProtection = true };
                    HistoryStore.Save(root, state);
                    saved = last with { Pinned = true, IsRestoreProtection = true };
                }
                else saved = last;
                return "文件内容和目录结构没有变化，保留现有版本。";
            }
            var previousEntries = previous.FirstOrDefault()?.Version.Entries.ToDictionary(entry => entry.Path, HistoryPaths.Comparer);
            var estimate = before.Where(entry => !entry.IsDirectory && (previousEntries is null ||
                !previousEntries.TryGetValue(entry.Path, out var old) || old != entry)).Sum(entry => entry.Length);
            RequireSpace(root, estimate, "保存版本");
            progress.Report(new(null, "正在保存版本"));
            var created = await engine.BackupAsync(root, HistoryPaths.HistoryDirectory(root), cancellation, exclusions).ConfigureAwait(false);
            progress.Report(new(null, "正在校验保存结果"));
            var after = await WorkingTree.ScanAsync(root, exclusions, cancellation).ConfigureAwait(false);
            if (!HistoryComparison.Equal(before, after))
                throw new WorkingTreeChangedException("保存期间文件发生变化，本次未登记为可用版本；将等待稳定后重新核对。");
            if (!HistoryComparison.Equal(after, created.Entries))
                throw new IOException("引擎结果与完整工作文件清单不符，本次未登记为可用版本，请检查后重试。");
            cancellation.ThrowIfCancellationRequested();
            state.Versions.Add(new(created.Id, root, note, protection, protection));
            HistoryStore.Save(root, state);
            saved = new(created, note, protection) { IsRestoreProtection = protection };
            return "已保存并核对文件清单：" + created.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        }, token).ConfigureAwait(false);
        }
        catch (WorkingTreeChangedException)
        {
            DeferChangedCapture(root);
            throw;
        }
        catch (OperationCanceledException)
        {
            SetRuntimeStatus(root, "保存已取消；请手动重新开启自动保护", faulted: true);
            throw;
        }
        catch (Exception error)
        {
            SetRuntimeStatus(root, "保存不完整：" + error.GetBaseException().Message, faulted: true);
            throw;
        }
        if (!automatic) SetRuntimeStatus(root, state.ProtectionEnabled ? "已保存" : "已暂停");
        return saved ?? throw new InvalidDataException("没有可确认的完整版本。");
    }

    public Task SetPinnedAsync(string root, string versionId, bool pinned) => ExclusiveAsync(_ =>
    {
        Open(root);
        using var ownership = HistoryStore.Lock(root);
        var state = Open(root);
        if (!pinned && InPlaceRestoreTransaction.ReadPending(root) is { } pending &&
            (pending.ProtectionVersionId == versionId || pending.Plan.TargetVersionId == versionId))
            throw new InvalidOperationException("未完成恢复仍在使用此版本，不能取消保护。");
        var index = state.Versions.FindIndex(version => version.Id == versionId);
        if (index < 0) throw new InvalidOperationException("选定版本已不可用。");
        state.Versions[index] = state.Versions[index] with { Pinned = pinned };
        HistoryStore.Save(root, state);
        NotifyChanged();
        return Task.FromResult(true);
    });

    public Task RemoveFolderAsync(string root) => ExclusiveAsync(_ =>
    {
        // Disassociation never removes the work directory or its history.
        SaveRegistry(_registry.Folders.Where(folder => !HistoryPaths.Comparer.Equals(folder.Root, root)).ToList());
        RemoveObservation(root);
        NotifyChanged();
        return Task.FromResult(true);
    });

    public async Task StopAsync()
    {
        Task[] pending;
        lock (_lifetime) { _stopped = true; pending = [.. _pending]; }
        await _stopping.CancelAsync().ConfigureAwait(false);
        StopObservations();
        await Task.WhenAll(pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (_automationTask is { } automation) await automation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    public void Dispose()
    {
        StopObservations();
        _stopping.Dispose();
        _operation.Dispose();
        _wake.Dispose();
    }

    private Task<T> ExclusiveAsync<T>(Func<CancellationToken, Task<T>> action) => TrackAsync(async token =>
    {
        await _operation.WaitAsync(token).ConfigureAwait(false);
        try { token.ThrowIfCancellationRequested(); return await action(token).ConfigureAwait(false); }
        finally { _operation.Release(); }
    });

    private Task<T> TrackAsync<T>(Func<CancellationToken, Task<T>> action)
    {
        lock (_lifetime)
        {
            if (_stopped) return Task.FromCanceled<T>(new CancellationToken(true));
            var token = _stopping.Token;
            var task = Task.Run(() => action(token), token);
            _pending.Add(task);
            _ = task.ContinueWith(completed => { lock (_lifetime) _pending.Remove(completed); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }

    private RepositoryState Open(string root)
    {
        root = HistoryPaths.NormalizeRoot(root, _context.Paths.AppRoot, _context.Paths.DataRoot);
        var registry = _registry;
        var registration = registry.Folders.SingleOrDefault(folder => HistoryPaths.Comparer.Equals(folder.Root, root))
            ?? throw new InvalidOperationException("目录尚未关联。");
        if (registry.Folders.Any(folder => folder.Id != registration.Id && HistoryPaths.Overlaps(root, folder.Root)))
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
            if (state.PendingDeleteIds.Contains(receipt.Id) &&
                HistoryPaths.Attributes(Path.Combine(HistoryPaths.HistoryDirectory(root), "repository", receipt.Id)) is null) continue;
            var version = DuplicatiEngine.ReadVersion(HistoryPaths.HistoryDirectory(root), receipt.Id);
            if (version is null || !HistoryPaths.Comparer.Equals(receipt.SourceRoot, version.SourceRoot))
                throw new InvalidDataException("已保存版本缺失或来源不符，请保留历史库并检查磁盘内容。");
            result.Add(new(version, receipt.Note, receipt.Pinned) { IsRestoreProtection = receipt.RestoreProtection });
        }
        return result.OrderByDescending(item => item.Version.CreatedAt).ToArray();
    }

    private static void RequireNoPendingOperation(string root, RepositoryState state)
    {
        if (InPlaceRestoreTransaction.ReadPending(root) is not null)
            throw new InvalidOperationException("这个文件夹有未完成恢复，请先继续恢复或安全撤销本次恢复。");
        if (state.PendingDeleteIds.Count > 0)
            throw new InvalidOperationException("上次历史清理未完成，请在清理预览中核对后重试。");
    }

    private void SaveRegistry(List<FolderRegistration> folders)
    {
        var updated = new FolderRegistry { Folders = folders };
        _context.Settings.Save("folders", updated);
        _registry = updated;
    }

    private DuplicatiEngine Engine(IPackageLease lease) => new(_context.Processes!, lease.Tools[DuplicatiEngine.ToolName], _context.Log);

    private async Task<IPackageLease> RequestEngineAsync(CancellationToken token, bool automatic = false)
    {
        if (_context.Processes is null || _context.Packages is null || _context.Tasks is null)
            throw new InvalidOperationException("宿主缺少所需的依赖、进程或任务接口。");
        var package = new PackageReference(DuplicatiEngine.PackageId, DuplicatiEngine.PackageVersion);
        if (automatic)
        {
            if (_context.Packages.GetStatus(package).Phase != PackagePhase.Ready)
                throw new InvalidOperationException("历史工具尚未就绪，请手动重新开启保护以准备工具。");
            return await _context.Packages.AcquireAsync(package, token).ConfigureAwait(false);
        }
        return await _context.Packages.TryRequestAsync(package, token).ConfigureAwait(false)
            ?? throw new OperationCanceledException("未准备历史工具，本次操作已取消。", token);
    }

    private async Task RunTaskAsync(string title, Func<IProgress<BackgroundTaskProgress>, CancellationToken, Task<string?>> execute,
        CancellationToken token)
    {
        using var task = await _context.Tasks!.QueueAsync(new(Guid.NewGuid(), title), token).ConfigureAwait(false);
        using var stop = token.Register(task.Cancel);
        WorkingTreeChangedException? changed = null;
        var result = await task.RunAsync(async (progress, cancellation) =>
        {
            try { return await execute(progress, cancellation).ConfigureAwait(false); }
            catch (WorkingTreeChangedException error) { changed = error; throw; }
        }).ConfigureAwait(false);
        if (result.State is BackgroundTaskState.Cancelled or BackgroundTaskState.Interrupted)
            throw new OperationCanceledException("操作已取消。", token);
        if (result.State != BackgroundTaskState.Succeeded)
        {
            if (changed is not null) throw changed;
            throw new IOException(result.Error ?? "历史操作未完成。");
        }
    }

    private void NotifyChanged()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception error) { _context.Log.Write(AppLogLevel.Warning, _context.PluginId, "文件夹历史状态通知未完成。", error); }
    }

    private static long? AvailableBytes(string root)
    {
        try { return new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void RequireSpace(string root, long estimate, string action)
    {
        if (AvailableBytes(root) is { } available && available < estimate)
            throw new IOException($"可用空间不足以按未压缩大小预估{action}（约 {estimate / (1024d * 1024):N1} MiB）；历史和工作文件均已保留。");
    }

    private static long MeasureDirectory(string directory, CancellationToken token)
    {
        HistoryPaths.RequireDirectory(directory);
        long bytes = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (HistoryPaths.Attributes(path) is not { } attributes) continue;
                bytes = checked(bytes + (attributes.HasFlag(FileAttributes.Directory) ? MeasureDirectory(path, token) : new FileInfo(path).Length));
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return bytes;
    }

    private void CleanupStaging(string staging, IReadOnlyList<HistoryEntry> entries)
    {
        try { RemoveStaging(staging, entries); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { _context.Log.Write(AppLogLevel.Warning, _context.PluginId, "操作已完成，历史库内的提取暂存未能清理。", ex); }
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
