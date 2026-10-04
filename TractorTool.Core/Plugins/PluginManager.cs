using TractorTool.Core.Storage;

namespace TractorTool.Core.Plugins;

/// <summary>One installation owner; all mutations are serialized and no loaded version is overwritten.</summary>
public sealed class PluginManager : IDisposable
{
    private readonly AppPaths _paths;
    private readonly HostCompatibility _compatibility;
    private readonly JsonFileStore<PluginManagementState> _store;
    private readonly FileStream _lease;
    private readonly object _gate = new();
    private readonly string _root;
    private PluginManagementState _state;
    private bool _disposed;
    public IReadOnlyList<ManagedPlugin> Snapshot { get { lock (_gate) return _state.Plugins.ToArray(); } }
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
            _store = new(PluginFiles.Under(_root, "state.json"), PluginManagementState.Validate);
            _state = _store.Load();
        }
        catch { _lease.Dispose(); throw; }
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
                    Save(entry with { Error = "启动时变更失败：" + ex.Message });
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
            if (item.Pending is not null) return PendingConflict;
            if (item.ActiveVersion is null) return "插件尚未安装。";
            if (kind == PluginChangeKind.Rollback && item.Recovery is null) return "没有可回退版本。";
            Save(item with { Pending = new(kind, Guid.NewGuid().ToString("N"), _paths.DataRoot), Error = "" });
            return null;
        }
    }

    public void CancelPending(string id)
    {
        lock (_gate)
        {
            Check();
            var item = Find(id) ?? throw new InvalidOperationException("没有待处理变更。");
            // Failed activation has not run the new DLL or modified plugin data; the old selection is safe.
            // Rollback/uninstall can already have moved data/code and must retain their recovery journal.
            if (item.Error.Length != 0 && item.Pending?.Kind != PluginChangeKind.Activate)
                throw new InvalidOperationException("启动变更已尝试，需修复原因后重启重试，不能丢弃恢复记录。");
            Save(item with { Pending = null, Error = "" });
            if (item.Pending?.Kind == PluginChangeKind.Activate)
            {
                try
                {
                    var staged = PluginFiles.Under(_root, "staging", item.Pending.Token);
                    var target = VersionPath(item.Id, item.Pending.Version!);
                    // Only delete a moved, unchanged package belonging to this activation request.
                    if (!Directory.Exists(PluginFiles.Under(staged, item.Id, item.Pending.Version!)) && Directory.Exists(target) &&
                        PluginFiles.Digest(target) == item.Pending.Digest)
                        PluginFiles.Delete(_paths.PluginRoot, target);
                    PluginFiles.Delete(_root, staged);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                { throw new IOException("变更已取消，但暂存文件未能清理：" + ex.Message, ex); }
            }
        }
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
        if (!string.Equals(pending.DataRoot, _paths.DataRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
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
                if (Directory.Exists(source))
                {
                    _ = PluginFiles.Files(source).ToArray();
                    Directory.CreateDirectory(Path.GetDirectoryName(trash)!);
                    Directory.Move(source, trash);
                }
                // A persistent tombstone prevents an old package from being implicitly re-adopted.
                // Delete only code in the transaction trash; user data and recovery snapshots are retained.
                PluginFiles.Delete(_root, trash);
                return item with { ActiveVersion = null, AwaitingLoad = false, Recovery = null };
            }
            case PluginChangeKind.Rollback:
                PluginFiles.ValidatePackage(VersionPath(item.Id, item.Recovery!.Version), _compatibility);
                Restore(item.Id, item.Recovery!);
                return item with { ActiveVersion = item.Recovery.Version, Recovery = null, AwaitingLoad = true, Enabled = true };
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
        PluginFiles.Delete(root, destination);
        var hadData = Directory.Exists(data);
        if (hadData) PluginFiles.Copy(data, destination); else Directory.CreateDirectory(destination);
        metadata.Save(new() { Id = item.Id, Version = item.ActiveVersion!, DataRoot = _paths.DataRoot, HadData = hadData, Digest = PluginFiles.Digest(destination) });
        return new(item.ActiveVersion!, token, _paths.DataRoot, hadData);
    }

    private void Restore(string id, PluginRecovery recovery)
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
        if (Directory.Exists(work))
        {
            if (Directory.Exists(data))
            {
                if (Directory.Exists(displaced)) throw new IOException("回退路径有冲突，保留全部数据等待处理。");
                _ = PluginFiles.Files(data).ToArray();
                Directory.Move(data, displaced);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(data)!);
            Directory.Move(work, data);
        }
        else if (!Directory.Exists(data)) throw new IOException("恢复目录缺失，保留恢复记录。");
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
    private void Save(ManagedPlugin item)
    {
        PluginFiles.Under(_root, "state.json"); PluginFiles.Under(_root, "state.json.bak");
        var next = _state with { Plugins = [.. _state.Plugins.Where(p => !p.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase)), item] };
        StateChanging?.Invoke();
        try { _store.Save(next); _state = next; }
        finally { StateChanged?.Invoke(); }
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
