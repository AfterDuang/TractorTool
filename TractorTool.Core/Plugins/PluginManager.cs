using TractorTool.Core.Storage;

namespace TractorTool.Core.Plugins;

/// <summary>One installation owner; all mutations are serialized and no loaded version is overwritten.</summary>
public sealed class PluginManager : IDisposable
{
    private AppPaths _paths;
    private readonly HostCompatibility _compatibility;
    private readonly JsonFileStore<PluginManagementState> _store;
    private readonly FileStream _lease;
    private readonly object _gate = new();
    private readonly string _root;
    private PluginManagementState _state;
    private bool _disposed;
    public IReadOnlyList<ManagedPlugin> Snapshot { get { lock (_gate) return _state.Plugins.ToArray(); } }
    public IReadOnlyList<RetainedPluginRemoval> RetainedRemovals { get { lock (_gate) return _state.RetainedRemovals.ToArray(); } }
    public string StateNotificationError { get; private set; } = "";
    public string? DataMigrationBlockReason
    {
        get { lock (_gate) return _state.Plugins.Any(p => p.Pending is not null || p.AwaitingLoad)
            ? "请先完成插件的待重启变更或处理加载失败，再迁移数据目录。" : null; }
    }
    // Invalidate dependency cleanup before committing a new selection; rebuild before returning.
    internal event Action? StateChanging;
    internal event Action? StateChanged;
    internal void ReadState(Action<IReadOnlyList<ManagedPlugin>> read)
    { lock (_gate) read(_state.Plugins); }

    public PluginManager(AppPaths paths, HostCompatibility compatibility)
    {
        _paths = paths; _compatibility = compatibility;
        _root = PluginFiles.Under(paths.PluginRoot, ".management");
        Directory.CreateDirectory(paths.PluginRoot);
        var leasePath = PluginFiles.Under(paths.PluginRoot, ".installation.lock");
        try
        {
            _lease = File.Exists(leasePath)
                ? new FileStream(leasePath, FileMode.Open, FileAccess.Read, FileShare.None)
                : new FileStream(leasePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new IOException($"插件目录被其他实例占用或不可访问：{paths.PluginRoot}。请退出使用此软件目录的其他实例。", ex); }
        try
        {
            var statePath = PluginFiles.Under(_root, "state.json");
            if (!PathPresent(statePath) && PathPresent(PluginFiles.Under(_root, "state.json.bak")))
                throw new InvalidDataException("插件管理主记录丢失，但备份仍存在。请通过插件页“打开目录”或资源管理器按下方路径定位 .management，先退出软件并备份该文件夹。" +
                    "备份可能早于最近的变更；仅在确认与当前插件启停、活动版本及待处理变更一致时，恢复为主记录并重新启动。无法确认时请保留现场，不会自动恢复。" +
                    $"\n主记录：{statePath}\n备份：{statePath}.bak");
            _store = new(statePath, PluginManagementState.Validate);
            _state = _store.Load();
        }
        catch { _lease.Dispose(); throw; }
    }

    // Called only at startup, before PluginHost discovery or any plugin DLL is loaded.
    internal void ValidateDataRootMigration(string source, AppPaths target, CancellationToken token) => RebindDataRoot(source, target, false, null, token);
    internal void CompleteDataRootMigration(string source, AppPaths target, IReadOnlySet<string>? sourceFiles, CancellationToken token) =>
        RebindDataRoot(source, target, true, sourceFiles, token);

    internal bool IsReboundRecoveryMetadata(string source, string target, string relative)
    {
        lock (_gate)
        {
            Check();
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (var item in _state.Plugins)
            {
                // Uninstalled recovery files are opaque archives and must remain byte-for-byte unchanged.
                if (item.ActiveVersion is null) continue;
                if (item.Recovery is not { } recovery || !string.Equals(recovery.DataRoot, target, comparison)) continue;
                var metadata = Path.Combine("Data", ".plugin-backups", recovery.Token, "snapshot.json");
                var backup = string.Equals(relative, metadata + ".bak", comparison);
                if (!backup && !string.Equals(relative, metadata, comparison)) continue;
                var before = new JsonFileStore<RecoveryMetadata>(PluginFiles.Under(source, relative)).Load();
                var after = new JsonFileStore<RecoveryMetadata>(PluginFiles.Under(target, relative)).Load();
                // Rebinding changes only DataRoot; .bak may still name an earlier migration root.
                return before.Id == item.Id && before.Version == recovery.Version && before.HadData == recovery.HadData &&
                    (backup || string.Equals(before.DataRoot, source, comparison)) &&
                    (string.Equals(after.DataRoot, target, comparison) || backup && string.Equals(after.DataRoot, source, comparison)) &&
                    (before with { DataRoot = after.DataRoot }) == after;
            }
            return false;
        }
    }

    // The caller has confirmed this root is missing, before starting any plugin or data writer.
    public void DiscardDataRootState(string dataRoot)
    {
        lock (_gate)
        {
            Check();
            dataRoot = AppPaths.Absolute(dataRoot);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (var item in _state.Plugins.ToArray())
            {
                var recovery = item.Recovery is { } saved && string.Equals(AppPaths.Absolute(saved.DataRoot), dataRoot, comparison);
                var pending = item.Pending is { } request &&
                    (string.Equals(AppPaths.Absolute(request.DataRoot), dataRoot, comparison) ||
                     recovery && request.Kind == PluginChangeKind.Rollback);
                if (!pending && !recovery) continue;
                if (pending && item.Pending?.Kind == PluginChangeKind.Activate)
                    CleanPendingActivation(item);
                // Uninstall may already have moved code to trash; data reset must not cancel its retry.
                var uninstall = pending && item.Pending!.Kind == PluginChangeKind.Uninstall;
                Save(item with
                {
                    Pending = pending && !uninstall ? null : item.Pending,
                    Recovery = recovery ? null : item.Recovery,
                    AwaitingLoad = false,
                    Error = uninstall ? item.Error : ""
                });
            }
        }
    }

    private void RebindDataRoot(string source, AppPaths target, bool commit, IReadOnlySet<string>? sourceFiles, CancellationToken token)
    {
        lock (_gate)
        {
            Check();
            token.ThrowIfCancellationRequested();
            if (DataMigrationBlockReason is { } blocked) throw new InvalidOperationException(blocked);
            if (target.AppRoot != _paths.AppRoot || target.BootstrapRoot != _paths.BootstrapRoot)
                throw new InvalidDataException("迁移不能更换软件或启动配置位置。");
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var next = _state.Plugins.ToArray();
            for (var i = 0; i < next.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var item = next[i];
                if (item.Recovery is not { } recovery) continue;
                var fromSource = string.Equals(recovery.DataRoot, source, comparison);
                if (!fromSource && (!commit || !string.Equals(recovery.DataRoot, target.DataRoot, comparison))) continue;
                if (item.ActiveVersion is null)
                {
                    // Preserve failed recovery evidence without treating an uninstalled archive as executable rollback state.
                    next[i] = item with { Recovery = recovery with { DataRoot = target.DataRoot } };
                    continue;
                }
                var root = PluginFiles.Under(target.DataRoot, "Data", ".plugin-backups", recovery.Token);
                var metadata = new JsonFileStore<RecoveryMetadata>(PluginFiles.Under(root, "snapshot.json"));
                // Reused target paths can retain references to a deleted profile. The persisted
                // source inventory distinguishes those from snapshots lost during finalization.
                if (!fromSource && sourceFiles is not null && !sourceFiles.Contains(Path.GetRelativePath(target.DataRoot, metadata.FilePath)))
                {
                    next[i] = item with { Recovery = null };
                    continue;
                }
                if (new FileInfo(metadata.FilePath).Attributes == (FileAttributes)(-1))
                    throw new InvalidDataException($"迁移后的插件回退快照无效：{item.Id}，缺少快照文件：{metadata.FilePath}");
                var saved = metadata.Load();
                if (saved.Id != item.Id || saved.Version != recovery.Version || saved.HadData != recovery.HadData ||
                    (!string.Equals(saved.DataRoot, source, comparison) && !string.Equals(saved.DataRoot, target.DataRoot, comparison)) ||
                    PluginFiles.Digest(PluginFiles.Under(root, "saved"), token) != saved.Digest)
                    throw new InvalidDataException("迁移后的插件回退快照无效：" + item.Id);
                token.ThrowIfCancellationRequested();
                if (commit && saved.DataRoot != target.DataRoot)
                {
                    PluginFiles.Under(root, "snapshot.json.bak");
                    metadata.Save(saved with { DataRoot = target.DataRoot });
                }
                next[i] = item with { Recovery = recovery with { DataRoot = target.DataRoot } };
            }
            token.ThrowIfCancellationRequested();
            if (!commit) return;
            if (!next.SequenceEqual(_state.Plugins))
            {
                var state = _state with { Plugins = next };
                PluginFiles.Under(_root, "state.json"); PluginFiles.Under(_root, "state.json.bak");
                _store.Save(state);
                _state = state;
            }
            _paths = target;
        }
    }

    // Called only at startup, before PluginHost discovery or any plugin DLL is loaded.
    public void ApplyPending()
    {
        lock (_gate)
        {
            Check();
            foreach (var entry in _state.Plugins.ToArray())
            {
                if (entry.Pending is null) continue;
                try
                {
                    var next = Apply(entry);
                    Save(next with { Pending = null, Error = "" });
                }
                catch (Exception ex)
                {
                    // Keep the request for an idempotent retry. No silent fallback to another directory.
                    Save((Find(entry.Id) ?? entry) with { Error = "启动时变更失败：" + ex.Message });
                }
            }
        }
    }

    // null means queued; a message is an expected state conflict, not a thrown failure.
    public string? QueueInstall(string archivePath, IReadOnlyList<PluginRecord> current)
    {
        lock (_gate)
        {
            Check();
            var token = Guid.NewGuid().ToString("N");
            var stage = PluginFiles.Under(_root, "staging", token);
            var queued = false;
            try
            {
                var manifest = PluginFiles.Extract(archivePath, stage, _compatibility);
                var item = Find(manifest.Id) ?? Adopt(manifest.Id, current, manifest.Name);
                if (item.Id != manifest.Id) throw new InvalidDataException("更新包的稳定 ID 大小写必须与已安装插件完全一致。");
                if (item.ActiveVersion is null && Directory.Exists(PluginFiles.Under(_paths.PluginRoot, item.Id)) &&
                    Directory.EnumerateFileSystemEntries(PluginFiles.Under(_paths.PluginRoot, item.Id)).Any())
                    throw new InvalidDataException("相同 ID 的目录含未能识别的插件；请先修复清单或移走冲突副本。");
                if (item.Pending is not null) return PendingConflict;
                if (item.AwaitingLoad || item.Error.Length != 0)
                    return "先处理插件上次加载或变更错误，再更新。";
                var target = VersionPath(manifest.Id, manifest.Version);
                if (Directory.Exists(target) || File.Exists(target)) return "此版本已存在；请使用新版本号，不覆盖已安装代码。";
                if (item.ActiveVersion is { } active && Version.Parse(manifest.Version) <= Version.Parse(active))
                    return "更新包版本必须高于当前版本；恢复旧版本请使用回退。";
                var digest = PluginFiles.Digest(PluginFiles.Under(stage, manifest.Id, manifest.Version));
                item = item with { Name = manifest.Name, Pending = new(PluginChangeKind.Activate, token, _paths.DataRoot, manifest.Version, digest), Error = "" };
                Save(item);
                queued = true;
                return null;
            }
            finally
            {
                // Staging is never discovered. Leave inaccessible partial files for diagnosis.
                if (!queued)
                    try { PluginFiles.Delete(_root, stage); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    public string? QueueChange(string id, PluginChangeKind kind, IReadOnlyList<PluginRecord> current)
    {
        lock (_gate)
        {
            Check();
            if (kind == PluginChangeKind.Activate) throw new ArgumentException("安装需要一个已校验的插件包。");
            var item = Find(id) ?? Adopt(id, current, id);
            // Abandoning a failed rollback removes only code; its data and recovery journal remain.
            if (item.Pending is not null && !(kind == PluginChangeKind.Uninstall &&
                item.Pending.Kind == PluginChangeKind.Rollback && item.Error.Length != 0)) return PendingConflict;
            if (item.ActiveVersion is null) return "插件尚未安装。";
            if (kind == PluginChangeKind.Rollback && item.Recovery is null) return "没有可回退版本。";
            Save(item with { Pending = new(kind, Guid.NewGuid().ToString("N"), _paths.DataRoot)
                { DestructiveStarted = item.Pending?.Kind == PluginChangeKind.Rollback }, Error = "" });
            return null;
        }
    }

    public void CancelPending(string id)
    {
        lock (_gate)
        {
            Check();
            var item = Find(id) ?? throw new InvalidOperationException("没有待处理变更。");
            if (item.Pending?.Kind is PluginChangeKind.Rollback or PluginChangeKind.Uninstall ||
                item.Error.Length != 0 && item.Pending?.Kind != PluginChangeKind.Activate)
                ValidateCancellation(item);
            if (item.Pending?.Kind == PluginChangeKind.Activate)
                CleanPendingActivation(item);
            Save(item with { Pending = null, Error = "" });
        }
    }

    private void ValidateCancellation(ManagedPlugin item)
    {
        var pending = item.Pending ?? throw new InvalidOperationException("没有待处理变更。");
        if (pending.Kind is PluginChangeKind.Rollback or PluginChangeKind.Uninstall && pending.DestructiveStarted != false)
            throw new InvalidOperationException("无法确认程序或数据尚未移动，不能取消变更。请保留恢复记录，退出并重新打开软件重试。");
        if (pending.Kind == PluginChangeKind.Uninstall)
        {
            if (!PluginFiles.DirectoryPresent(VersionPath(item.Id, item.ActiveVersion!)) ||
                PathPresent(PluginFiles.Under(_root, "removed", pending.Token)))
                throw new IOException("活动目录已离开原位或卸载回收目录已存在，不能取消变更。");
            return;
        }
        PluginFiles.ValidatePackage(VersionPath(item.Id, item.ActiveVersion!), _compatibility);
        if (pending.Kind == PluginChangeKind.Rollback)
        {
            var recovery = item.Recovery!;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(recovery.DataRoot, _paths.DataRoot, comparison)) throw new IOException("恢复记录属于其他数据目录。");
            var root = BackupPath(recovery.Token);
            if (PathPresent(PluginFiles.Under(root, "before-rollback")) || PathPresent(PluginFiles.Under(root, "restore-ready")))
                throw new IOException("回退已有数据交换记录，不能取消变更。");
            _ = PluginFiles.DirectoryPresent(PluginFiles.Under(root, "restore"));
            _ = PluginFiles.DirectoryPresent(PluginFiles.Under(_paths.DataRoot, "Data", "Plugins", item.Id));
        }
    }

    // Only code outside discovery is retained. Persist ownership before releasing the failed request.
    public void CompleteUninstallKeepingFiles(string id)
    {
        lock (_gate)
        {
            Check();
            var item = Find(id) ?? throw new InvalidOperationException("没有待卸载插件。");
            if (item.Pending is not { Kind: PluginChangeKind.Uninstall } pending || item.Error.Length == 0)
                throw new InvalidOperationException("没有失败的卸载请求。");
            if (PathPresent(PluginFiles.Under(_paths.PluginRoot, item.Id)))
                throw new IOException("插件程序尚未移出安装目录，不能结束卸载。请检查目录权限或解除文件占用后，退出并重新打开软件重试。" +
                    (pending.DestructiveStarted == false ? "也可在插件页尝试“取消变更”。" : "无法确认程序尚未移动，当前不能取消变更。"));
            var retained = _state.RetainedRemovals;
            if (PathPresent(PluginFiles.Under(_root, "removed", pending.Token)))
                retained = [.. retained, new(item.Id, pending.Token)];
            var next = item with { ActiveVersion = null, Pending = null, AwaitingLoad = false, Error = "" };
            SaveState(_state with { Plugins = Replace(next), RetainedRemovals = retained });
        }
    }

    public void CleanRetainedFiles(string id)
    {
        lock (_gate)
        {
            Check();
            foreach (var removal in _state.RetainedRemovals.Where(r => r.PluginId.Equals(id, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                PluginFiles.Delete(_root, PluginFiles.Under(_root, "removed", removal.Token));
                SaveState(_state with { RetainedRemovals = _state.RetainedRemovals.Where(r => r != removal).ToArray() });
            }
        }
    }

    private void CleanPendingActivation(ManagedPlugin item)
    {
        var pending = item.Pending!;
        var staged = PluginFiles.Under(_root, "staging", pending.Token);
        var stagedVersion = PluginFiles.Under(staged, item.Id, pending.Version!);
        var target = VersionPath(item.Id, pending.Version!);
        _ = PluginFiles.DirectoryPresent(staged);
        var stagedVersionPresent = PluginFiles.DirectoryPresent(stagedVersion);
        if (PluginFiles.DirectoryPresent(target))
        {
            if (item.ActiveVersion == pending.Version || stagedVersionPresent ||
                PluginFiles.Digest(target) != pending.Digest)
                throw new InvalidDataException("无法确认待安装代码的清理归属，代码与变更请求已保留，需要人工核对。请通过插件页“打开目录”或资源管理器按下方路径定位，退出软件并备份相关目录，" +
                    "核对原安装包与变更记录后再处理；不要直接删除管理记录。" +
                    $"\n目标版本目录：{target}\n暂存版本目录（若存在）：{stagedVersion}\n管理记录目录：{_root}");
            // Move back before deleting: interrupted deletion can retry in the same staging tree without a full digest.
            Directory.CreateDirectory(Path.GetDirectoryName(stagedVersion)!);
            Directory.Move(target, stagedVersion);
        }
        PluginFiles.Delete(_root, staged);
    }

    public void RecordLoadResults(IReadOnlyList<PluginRecord> records)
    {
        lock (_gate)
        {
            Check();
            foreach (var item in _state.Plugins.Where(p => p.AwaitingLoad).ToArray())
            {
                var record = records.FirstOrDefault(r => r.Manifest?.Id == item.Id && r.Manifest.Version == item.ActiveVersion);
                if (record?.State == PluginState.Loaded) Save(item with { AwaitingLoad = false, Error = "" });
                else if (record?.State != PluginState.Disabled)
                    Save(item with { Error = "新版本未成功加载；可安排回退或卸载。" + (record?.Detail ?? "找不到活动版本。") });
            }
        }
    }

    private ManagedPlugin Apply(ManagedPlugin item)
    {
        var pending = item.Pending!;
        // Uninstall only mutates installation code; it must also finish after a data reset or a restored root.
        if (pending.Kind != PluginChangeKind.Uninstall &&
            !string.Equals(pending.DataRoot, _paths.DataRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("待处理变更属于另一个数据根目录，请使用安排变更时的配置启动。");
        switch (pending.Kind)
        {
            case PluginChangeKind.Activate:
            {
                var staged = PluginFiles.Under(_root, "staging", pending.Token, item.Id, pending.Version!);
                var target = VersionPath(item.Id, pending.Version!);
                var package = Directory.Exists(staged) ? staged : target; // Resume after a committed directory move.
                PluginFiles.ValidatePackage(package, _compatibility);
                if (PluginFiles.Digest(package) != pending.Digest) throw new InvalidDataException("待安装包内容已改变。");
                var recovery = item.ActiveVersion is null ? null : Backup(item, pending.Token);
                if (Directory.Exists(staged))
                {
                    if (Directory.Exists(target)) throw new IOException("目标版本已存在，拒绝覆盖。");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    Directory.Move(staged, target);
                }
                return item with { ActiveVersion = pending.Version, Recovery = recovery, AwaitingLoad = item.Enabled };
            }
            case PluginChangeKind.Enable: return item with { Enabled = true, AwaitingLoad = true };
            case PluginChangeKind.Disable: return item with { Enabled = false, AwaitingLoad = false };
            case PluginChangeKind.Uninstall:
            {
                var source = PluginFiles.Under(_paths.PluginRoot, item.Id);
                var trash = PluginFiles.Under(_root, "removed", pending.Token);
                if (PluginFiles.DirectoryPresent(source))
                {
                    _ = PluginFiles.Files(source).ToArray();
                    Directory.CreateDirectory(Path.GetDirectoryName(trash)!);
                    MarkDestructiveStarted(item);
                    Directory.Move(source, trash);
                }
                // A persistent tombstone prevents an old package from being implicitly re-adopted.
                // Delete only code in the transaction trash; user data and recovery snapshots are retained.
                PluginFiles.Delete(_root, trash);
                return item with { ActiveVersion = null, AwaitingLoad = false };
            }
            case PluginChangeKind.Rollback:
                PluginFiles.ValidatePackage(VersionPath(item.Id, item.Recovery!.Version), _compatibility);
                Restore(item.Id, item.Recovery!, pending.DestructiveStarted, () => MarkDestructiveStarted(item));
                return item with { ActiveVersion = item.Recovery.Version, Recovery = null, AwaitingLoad = item.Enabled };
            default: throw new InvalidDataException("未知插件变更。");
        }
    }

    private PluginRecovery Backup(ManagedPlugin item, string token)
    {
        var root = BackupPath(token);
        var metadata = new JsonFileStore<RecoveryMetadata>(PluginFiles.Under(root, "snapshot.json"));
        if (File.Exists(metadata.FilePath))
        {
            var saved = metadata.Load();
            if (saved.Id != item.Id || saved.Version != item.ActiveVersion || saved.DataRoot != _paths.DataRoot)
                throw new InvalidDataException("插件备份归属不匹配。");
            if (PluginFiles.Digest(PluginFiles.Under(root, "saved")) != saved.Digest) throw new IOException("插件备份内容已改变。");
            return new(item.ActiveVersion!, token, _paths.DataRoot, saved.HadData);
        }
        var data = PluginFiles.Under(_paths.DataRoot, "Data", "Plugins", item.Id);
        var destination = PluginFiles.Under(root, "saved");
        // An interrupted backup has not yet authorized activation. Restart it from still-active data.
        var hadData = DataDirectories.Exists(data);
        PluginFiles.Delete(root, destination);
        if (hadData) PluginFiles.Copy(data, destination); else Directory.CreateDirectory(destination);
        metadata.Save(new() { Id = item.Id, Version = item.ActiveVersion!, DataRoot = _paths.DataRoot, HadData = hadData, Digest = PluginFiles.Digest(destination) });
        return new(item.ActiveVersion!, token, _paths.DataRoot, hadData);
    }

    private void MarkDestructiveStarted(ManagedPlugin item)
    {
        if (item.Pending!.DestructiveStarted != true)
            Save(item with { Pending = item.Pending with { DestructiveStarted = true } });
    }

    private void Restore(string id, PluginRecovery recovery, bool? destructiveStarted, Action beforeDataMove)
    {
        if (!string.Equals(AppPaths.Absolute(recovery.DataRoot), _paths.DataRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("回退必须使用更新时的数据根目录；未覆盖当前配置。");
        var root = BackupPath(recovery.Token);
        var snapshot = new JsonFileStore<RecoveryMetadata>(PluginFiles.Under(root, "snapshot.json")).Load();
        var saved = PluginFiles.Under(root, "saved");
        if (snapshot.Id != id || snapshot.Version != recovery.Version || snapshot.DataRoot != _paths.DataRoot || PluginFiles.Digest(saved) != snapshot.Digest)
            throw new InvalidDataException("插件恢复快照无效。");
        var data = PluginFiles.Under(_paths.DataRoot, "Data", "Plugins", id);
        var work = PluginFiles.Under(root, "restore");
        var displaced = PluginFiles.Under(root, "before-rollback");
        var journal = PluginFiles.Under(root, "restore-ready");
        if (!File.Exists(journal))
        {
            PluginFiles.Delete(root, work);
            PluginFiles.Copy(saved, work);
            using var marker = new FileStream(journal, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            marker.Flush(true);
        }
        if (PluginFiles.DirectoryPresent(work))
        {
            if (PluginFiles.Digest(work) != snapshot.Digest)
                throw new InvalidDataException("回退工作副本与恢复快照不一致，保留全部数据等待处理。");
            beforeDataMove();
            if (PluginFiles.DirectoryPresent(data))
            {
                if (PluginFiles.DirectoryPresent(displaced)) throw new IOException("回退路径有冲突，保留全部数据等待处理。");
                _ = PluginFiles.Files(data).ToArray();
                Directory.Move(data, displaced);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(data)!);
            Directory.Move(work, data);
        }
        else if (!(destructiveStarted == true || destructiveStarted is null && PluginFiles.DirectoryPresent(displaced)) ||
            !PluginFiles.DirectoryPresent(data) || PluginFiles.Digest(data) != snapshot.Digest)
            throw new IOException("无法确认回退数据已完整就位，保留恢复记录与全部数据。");
    }

    private string BackupPath(string token) => PluginFiles.Under(_paths.DataRoot, "Data", ".plugin-backups", token);
    private string VersionPath(string id, string version) => PluginFiles.Under(_paths.PluginRoot, id, version);
    private ManagedPlugin? Find(string id) => _state.Plugins.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    private const string PendingConflict = "此插件已有待重启变更；先取消或重启完成。";
    private static ManagedPlugin Adopt(string id, IReadOnlyList<PluginRecord> current, string name)
    {
        AppPaths.ValidateId(id);
        var records = current.Where(r => r.Manifest?.Id.Equals(id, StringComparison.OrdinalIgnoreCase) == true).ToArray();
        if (records.Length > 1 || records.Any(r => r.State == PluginState.Duplicate)) throw new InvalidOperationException("插件版本冲突，请先保留唯一的初始版本。");
        var record = records.SingleOrDefault();
        return new() { Id = record?.Manifest?.Id ?? id, Name = record?.Name ?? name, ActiveVersion = record?.Manifest?.Version, Enabled = record?.State != PluginState.Disabled };
    }
    private ManagedPlugin[] Replace(ManagedPlugin item) => [.. _state.Plugins.Where(p => !p.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase)), item];
    private void Save(ManagedPlugin item) => SaveState(_state with { Plugins = Replace(item) });
    private void SaveState(PluginManagementState next)
    {
        PluginFiles.Under(_root, "state.json"); PluginFiles.Under(_root, "state.json.bak");
        StateChanging?.Invoke();
        try { _store.Save(next); _state = next; }
        finally
        {
            // Observation cannot turn a committed transaction into a failed install or destroy its staging.
            StateNotificationError = "";
            if (StateChanged is { } changed)
                foreach (Action observer in changed.GetInvocationList())
                    try { observer(); }
                    catch (Exception ex) { StateNotificationError += "插件状态通知失败：" + ex.Message + "\n"; }
        }
    }
    private static bool PathPresent(string path)
    {
        // A missing entry is normal during first startup; do not raise debugger first-chance exceptions.
        if (new FileInfo(path).Attributes != (FileAttributes)(-1)) return true;
        _ = DataDirectories.Exists(Path.GetDirectoryName(path)!);
        return false;
    }
    private void Check() => ObjectDisposedException.ThrowIf(_disposed, this);
    // Production keeps this handle until process exit, including late plugin cleanup after the timeout.
    public void Dispose() { lock (_gate) { if (_disposed) return; _disposed = true; _lease.Dispose(); } }

    public sealed record RecoveryMetadata : TractorTool.Contracts.IVersionedSettings
    {
        public int SchemaVersion { get; init; } = 1;
        public string Id { get; init; } = "";
        public string Version { get; init; } = "";
        public string DataRoot { get; init; } = "";
        public bool HadData { get; init; }
        public string Digest { get; init; } = "";
    }
}
