using System.Security.Cryptography;
using TractorTool.Contracts;
using TractorTool.Core.Plugins;

namespace TractorTool.Core.Storage;

public sealed record DataMigrationRequest(string Id, string Source, string Target, string AppRoot, string? Error = null)
{
    public static void Validate(DataMigrationRequest value)
    {
        if (!Guid.TryParseExact(value.Id, "N", out _)) throw new InvalidDataException("迁移记录标识无效，原记录保留。");
        _ = AppPaths.Absolute(value.Source); _ = AppPaths.Absolute(value.Target); _ = AppPaths.Absolute(value.AppRoot);
        if (Path.GetDirectoryName(value.Source) is null || Path.GetDirectoryName(value.Target) is null)
            throw new InvalidDataException("迁移目录不能是卷根目录。");
        if (AppPaths.Contains(value.Source, value.Target) || AppPaths.Contains(value.Target, value.Source))
            throw new InvalidDataException("迁移目录不能相同或互相包含。");
    }
}
public sealed record DataMigrationResult(AppPaths Paths, InstanceLease? TargetLease, string? Error = null, string? Warning = null);

// A startup transaction, not a live path switch. The caller holds the bootstrap/source leases
// and installation manager, and must not start services until this method has returned.
public sealed class DataRootMigration(AppPaths paths)
{
    private const string MarkerName = ".tractortool-migration.json";
    private const string MarkerBackupName = MarkerName + ".bak";
    private const string TempName = ".tractortool-migration-copy.tmp";
    private readonly object _gate = new();
    private readonly JsonFileStore<BootstrapSettings> _store = new(paths.BootstrapFile, BootstrapSettings.Validate);
    public BootstrapSettings Snapshot { get { lock (_gate) return _store.Load(); } }

    public string? Queue(string target, PluginManager manager)
    {
        lock (_gate)
        {
            var state = _store.Load();
            if (state.Migration is not null) return "已有迁移请求，请先完成或取消。";
            if (!Same(state.DataRoot ?? paths.BootstrapRoot, paths.DataRoot)) return "数据目录已变化，请重新启动后操作。";
            if (manager.DataMigrationBlockReason is { } blocked) return blocked;
            var request = new DataMigrationRequest(Guid.NewGuid().ToString("N"), paths.DataRoot, AppPaths.Absolute(target), paths.AppRoot);
            if (PathIssue(request) is { } issue) return issue;
            if (TargetContentIssue(request.Target) is { } occupied) return occupied;
            Save(state with { Migration = request });
            return null;
        }
    }

    public void Retry()
    {
        lock (_gate)
        {
            var state = _store.Load();
            if (state.Migration is { } request) Save(state with { Migration = request with { Error = null } });
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            var state = _store.Load();
            if (state.Migration is not { } request) return;
            if (Same(state.DataRoot ?? paths.BootstrapRoot, request.Target))
                throw new InvalidOperationException("数据目录已切换，需重启完成收尾，不能再取消。");
            // Partial target files are retained. Never recursively delete a user-selected directory.
            Save(state with { Migration = null });
        }
    }

    public DataMigrationResult ApplyAtStartup(PluginManager manager, IProgress<string>? progress, CancellationToken token)
    {
        lock (_gate)
        {
            var state = _store.Load();
            if (state.Migration is not { } request) return new(paths, null);
            var committed = Same(state.DataRoot ?? paths.BootstrapRoot, request.Target);
            if (!committed && request.Error is not null) return new(paths, null, request.Error);
            if (!Same(paths.DataRoot, committed ? request.Target : request.Source))
                throw new InvalidDataException("启动指针与迁移记录不一致，未更改任何数据。");
            InstanceLease? targetLease = null;
            InstanceLease? cleanupLease = null;
            try
            {
                DataMigrationRequest.Validate(request);
                if (PathIssue(request, checkSource: !committed) is { } issue) throw new InvalidDataException(issue);
                if (manager.DataMigrationBlockReason is { } blocked) throw new InvalidOperationException(blocked);
                if (!committed)
                {
                    token.ThrowIfCancellationRequested();
                    if (!File.Exists(Path.Combine(request.Target, MarkerName)) && TargetContentIssue(request.Target) is { } occupied)
                        throw new InvalidDataException(occupied);
                    targetLease = new InstanceLease(request.Target);
                    var marker = CheckTargetOwner(request, create: true);
                    var (cleanup, hashes) = CopyAndVerify(request, progress, token);
                    progress?.Report("正在校验插件回退快照…");
                    manager.ValidateDataRootMigration(request.Source, new AppPaths(paths.AppRoot, paths.BootstrapRoot, request.Target), token);
                    token.ThrowIfCancellationRequested();
                    SaveTargetMarker(request, marker with { Cleanup = cleanup, TargetHashes = hashes });
                    token.ThrowIfCancellationRequested();
                    var switched = state with { DataRoot = request.Target, PreviousDataRoot = request.Source };
                    Save(switched); // Commit point: all copied files verified; cleanup remains restartable.
                    state = switched;
                    committed = true;
                }
                else
                {
                    var marker = CheckTargetOwner(request, create: false);
                    if (marker.Cleanup is not null && !Same(request.Source, paths.BootstrapRoot) && DataDirectories.Exists(request.Source))
                        cleanupLease = new InstanceLease(request.Source, createDirectory: false);
                }
                progress?.Report("正在完成插件回退记录…");
                var next = new AppPaths(paths.AppRoot, paths.BootstrapRoot, request.Target);
                // Idempotent finalization. A crash here retains the request with the new pointer;
                // the next startup completes it before any plugin can write to either copy.
                var committedMarker = CheckTargetOwner(request, create: false);
                var sourceFiles = committedMarker.Cleanup?.Where(e => !e.IsDirectory).Select(e => e.Relative)
                    .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                manager.CompleteDataRootMigration(request.Source, next, sourceFiles, token);
                token.ThrowIfCancellationRequested();
                var warning = CleanSource(request, committedMarker, manager, progress, token);
                Save(state with { Migration = null });
                return new(next, targetLease, Warning: warning);
            }
            catch (Exception ex)
            {
                targetLease?.Dispose();
                // A store failure must never roll back a pointer that reached disk. If reading
                // the pointer also fails, stop startup rather than write through an unknown root.
                committed |= Same(_store.Load().DataRoot ?? paths.BootstrapRoot, request.Target);
                if (ex is OperationCanceledException && token.IsCancellationRequested) throw;
                if (committed) throw new IOException("数据已复制并切换，迁移收尾尚未完成；请解决以下问题后重启。未清理的旧数据已保留。\n" + ex.Message, ex);
                var error = "迁移未完成，继续使用原目录：" + ex.Message;
                Save(state with { Migration = request with { Error = error } });
                return new(paths, null, error);
            }
            finally { cleanupLease?.Dispose(); }
        }
    }

    private void Save(BootstrapSettings settings)
    {
        CheckPath(paths.BootstrapFile); CheckPath(paths.BootstrapFile + ".bak");
        _store.Save(settings);
    }

    private string? PathIssue(DataMigrationRequest request, bool checkSource = true)
    {
        if (!Same(request.AppRoot, paths.AppRoot)) return "请从安排迁移时的软件位置完成迁移。";
        _ = new AppPaths(paths.AppRoot, paths.BootstrapRoot, request.Source);
        if (Path.GetDirectoryName(request.Target) is null) return "请选择专用子目录，不能使用磁盘根目录。";
        if (AppPaths.Contains(request.Source, request.Target) || AppPaths.Contains(request.Target, request.Source))
            return "迁移目录不能相同或互相包含。";
        if (AppPaths.Contains(paths.PluginRoot, request.Target) || AppPaths.Contains(request.Target, paths.PluginRoot))
            return "数据目录不能与软件 Plugins 目录互相包含。";
        if (AppPaths.Contains(paths.BootstrapRoot, request.Target) || AppPaths.Contains(request.Target, paths.BootstrapRoot))
            return "目标不能与固定启动配置目录重叠，请选择新的专用目录。";
        if (new Uri(request.Target).IsUnc || OperatingSystem.IsWindows() &&
            new DriveInfo(Path.GetPathRoot(request.Target)!).DriveType is DriveType.Network or DriveType.Unknown or DriveType.NoRootDirectory)
            return "首版迁移仅支持本机磁盘，请选择可用的本地目录。";
        if (checkSource) CheckPath(request.Source);
        CheckPath(request.Target); CheckPath(paths.BootstrapFile);
        return null;
    }

    private static string? TargetContentIssue(string target)
    {
        CheckPath(target);
        if (File.Exists(target) || (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target)
            .Any(p => Path.GetFileName(p) != "host.lock")))
            return "请选择空目录；不会合并或覆盖目标中的已有数据。";
        return null;
    }

    private TargetMarker CheckTargetOwner(DataMigrationRequest request, bool create)
    {
        var markerPath = Path.Combine(request.Target, MarkerName);
        CheckPath(markerPath);
        var marker = new JsonFileStore<TargetMarker>(markerPath);
        if (!File.Exists(markerPath))
        {
            if (!create) throw new InvalidDataException("迁移目标归属标记缺失。");
            if (TargetContentIssue(request.Target) is { } occupied) throw new InvalidDataException(occupied);
            marker.Save(new() { Request = request with { Error = null }, BootstrapRoot = paths.BootstrapRoot });
        }
        var saved = marker.Load();
        if (saved.Request != (request with { Error = null }) || !Same(saved.BootstrapRoot, paths.BootstrapRoot))
            throw new InvalidDataException("目标属于其他迁移记录，原内容保留。");
        return saved;
    }

    private static void SaveTargetMarker(DataMigrationRequest request, TargetMarker marker)
    {
        var path = Path.Combine(request.Target, MarkerName);
        CheckPath(path); CheckPath(path + ".bak");
        new JsonFileStore<TargetMarker>(path).Save(marker);
    }

    private static (List<MigrationEntry> Cleanup, Dictionary<string, string> Hashes) CopyAndVerify(
        DataMigrationRequest request, IProgress<string>? progress, CancellationToken token)
    {
        if (!DataDirectories.Exists(request.Source)) throw new DirectoryNotFoundException("原数据目录不可用，未创建空数据替代。");
        var cleanup = Inventory(request.Source, token, includeCache: true);
        var source = Inventory(request.Source, token, skipDownloads: true);
        var expected = source.ToDictionary(e => e.Relative, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Directory.EnumerateFileSystemEntries(request.Target))
        {
            CheckPath(entry);
            if (!DataDirectories.PersistentNames.Contains(Path.GetFileName(entry), StringComparer.Ordinal) &&
                Path.GetFileName(entry) is not (MarkerName or MarkerBackupName or "host.lock" or TempName))
                throw new IOException("目标目录出现不属于此次迁移的内容，请选择新的空目录。");
        }
        foreach (var entry in Inventory(request.Target, token))
            if (!expected.TryGetValue(entry.Relative, out var original) || original.IsDirectory != entry.IsDirectory)
                throw new IOException("目标目录含已变化或新增的内容，未覆盖；请选择新的空目录。");
        // Ownership and contents have been checked under the target lease. Reclaim only this
        // transaction's incomplete scratch file before measuring the space needed to retry.
        var temp = Path.Combine(request.Target, TempName);
        token.ThrowIfCancellationRequested();
        CheckPath(temp);
        if (File.Exists(temp)) File.Delete(temp);
        var required = source.Where(e => !e.IsDirectory && !File.Exists(Path.Combine(request.Target, e.Relative))).Sum(e => e.Length);
        var drive = new DriveInfo(Path.GetPathRoot(request.Target)!);
        if (!drive.IsReady || drive.AvailableFreeSpace < required + 16L * 1024 * 1024)
            throw new IOException("目标磁盘可用空间不足，需为数据及迁移记录保留空间。");
        long copied = 0, total = source.Where(e => !e.IsDirectory).Sum(e => e.Length);
        var lastPercent = -1;
        var buffer = new byte[1024 * 1024];
        var copiedEntries = new List<MigrationEntry>(source.Count);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in source)
        {
            token.ThrowIfCancellationRequested();
            var inputPath = Path.Combine(request.Source, entry.Relative);
            var outputPath = Path.Combine(request.Target, entry.Relative);
            CheckPath(inputPath); CheckPath(outputPath); CheckPath(temp);
            if (entry.IsDirectory) { Directory.CreateDirectory(outputPath); copiedEntries.Add(entry); continue; }
            using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] digest;
            if (File.Exists(outputPath))
            {
                using var existing = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                digest = Hash(input, buffer, token);
                if (input.Length != existing.Length || !digest.AsSpan().SequenceEqual(Hash(existing, buffer, token)))
                    throw new IOException("目标已有不同内容，未覆盖：" + entry.Relative + "。请取消后选择新的空目录。");
                copied += entry.Length;
            }
            else
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    int count;
                    while ((count = input.Read(buffer)) != 0)
                    {
                        token.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, count); hash.AppendData(buffer, 0, count);
                        copied += count;
                        var percent = total == 0 ? 100 : (int)Math.Min(100, copied * 100d / total);
                        if (percent != lastPercent) { lastPercent = percent; progress?.Report($"正在复制并校验应用数据… {percent}%"); }
                    }
                    output.Flush(true);
                }
                digest = hash.GetHashAndReset();
                using (var verify = File.OpenRead(temp))
                    if (!digest.AsSpan().SequenceEqual(Hash(verify, buffer, token))) throw new IOException("迁移文件校验失败。");
                File.Move(temp, outputPath); // No overwrite, including unexpected target races.
                File.SetLastWriteTimeUtc(outputPath, entry.Modified);
            }
            // FAT/exFAT can round timestamps. Track the target's actual stored value rather
            // than reject an otherwise verified cross-volume copy for timestamp precision.
            copiedEntries.Add(entry with { Modified = File.GetLastWriteTimeUtc(outputPath) });
            hashes.Add(entry.Relative, Convert.ToHexString(digest));
        }
        // The source lease protects cooperating hosts; detect ordinary external changes too.
        if (!source.SequenceEqual(Inventory(request.Source, token, skipDownloads: true))) throw new IOException("源数据在迁移过程中发生变化，启动指针未切换。");
        if (!cleanup.SequenceEqual(Inventory(request.Source, token, includeCache: true))) throw new IOException("旧数据或缓存在迁移过程中发生变化，启动指针未切换。");
        if (!copiedEntries.SequenceEqual(Inventory(request.Target, token))) throw new IOException("目标数据在迁移过程中发生变化，启动指针未切换。");
        token.ThrowIfCancellationRequested();
        return (cleanup, hashes);
    }

    private static string? CleanSource(DataMigrationRequest request, TargetMarker marker, PluginManager manager, IProgress<string>? progress, CancellationToken token)
    {
        if (marker.Cleanup is not { } cleanup)
        {
            if (marker.TargetHashes is not null) throw new InvalidDataException("迁移清理清单缺失，已停止清理。");
            return "迁移已完成。旧版本未记录清理清单，原目录已保留，请确认新数据完整后自行清理：" + request.Source;
        }
        var expected = new Dictionary<string, MigrationEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in cleanup)
        {
            var full = AppPaths.Absolute(Path.Combine(request.Source, entry.Relative));
            var relative = Path.GetRelativePath(request.Source, full);
            if (Path.IsPathRooted(entry.Relative) || relative != entry.Relative ||
                !DataDirectories.OwnedNames.Contains(relative.Split(Path.DirectorySeparatorChar)[0], StringComparer.Ordinal) ||
                !expected.TryAdd(relative, entry))
                throw new InvalidDataException("迁移清理清单无效，旧数据已保留。");
        }
        // shortcut: discarded cache/downloads use size/time only; hash them if exact change detection is needed.
        var sourceExists = DataDirectories.Exists(request.Source);
        var remaining = sourceExists ? Inventory(request.Source, token, includeCache: true) : [];
        var remainingPaths = remaining.Select(e => e.Relative).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in remaining)
            if (!expected.TryGetValue(entry.Relative, out var original) || original != entry)
                throw new IOException("旧目录出现新增或变化的数据，已停止清理并保留剩余内容：" + entry.Relative);
        // Previously deleted source files can only be verified against evidence saved before cleanup.
        var buffer = new byte[1024 * 1024];
        var hashes = marker.TargetHashes ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var saveHashes = marker.TargetHashes is null;
        if (!saveHashes && hashes.Count != cleanup.Count(e => !e.IsDirectory && RequiresCopy(e.Relative)))
            throw new InvalidDataException("迁移清单与目标摘要的文件数量不一致，已停止清理。");
        foreach (var entry in cleanup)
        {
            token.ThrowIfCancellationRequested();
            if (!RequiresCopy(entry.Relative)) continue;
            var output = Path.Combine(request.Target, entry.Relative);
            CheckPath(output);
            if (entry.IsDirectory)
            {
                if (!DataDirectories.Exists(output)) throw new IOException("目标目录缺失，旧数据已保留：" + entry.Relative);
                continue;
            }
            hashes.TryGetValue(entry.Relative, out var expectedHash);
            if (marker.TargetHashes is not null && string.IsNullOrEmpty(expectedHash))
                throw new InvalidDataException("迁移清单缺少目标摘要：" + entry.Relative);
            using var copy = OpenVerifiedCopy(request, entry.Relative, remainingPaths.Contains(entry.Relative),
                expectedHash, manager, buffer, token, out var targetHash, allowRebind: true);
            if (targetHash != expectedHash)
            {
                hashes[entry.Relative] = targetHash;
                saveHashes = true;
            }
        }
        // Freeze migrated legacy evidence and any rewritten rollback metadata before the first deletion.
        if (saveHashes) SaveTargetMarker(request, marker with { TargetHashes = hashes });
        if (!sourceExists) return null;
        progress?.Report("正在清理旧应用数据…");
        var deleted = 0;
        var lastPercent = -1;
        // Missing entries are already cleaned. Delete children first, never recurse through links.
        foreach (var entry in remaining.OrderByDescending(e => e.Relative.Length))
        {
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(request.Source, entry.Relative);
            CheckPath(path);
            if (entry.IsDirectory) Directory.Delete(path, recursive: false);
            else
            {
                // Keep the verified target read-locked through deletion; earlier files may take time to clean.
                using var copy = RequiresCopy(entry.Relative)
                    ? OpenVerifiedCopy(request, entry.Relative, true, hashes[entry.Relative], manager, buffer, token, out _)
                    : null;
                var current = new FileInfo(path);
                if (current.Length != entry.Length || current.LastWriteTimeUtc != entry.Modified)
                    throw new IOException("旧文件在清理过程中发生变化，已保留：" + entry.Relative);
                File.Delete(path);
            }
            var percent = (int)(++deleted * 100L / remaining.Count);
            if (percent != lastPercent) { lastPercent = percent; progress?.Report($"正在清理旧应用数据… {percent}%"); }
        }
        token.ThrowIfCancellationRequested();
        if (Inventory(request.Source, token, includeCache: true).Count != 0)
            throw new IOException("旧目录在清理过程中出现新内容，已保留；请确认后重启。");
        return null;
    }

    private static bool RequiresCopy(string relative)
    {
        var parts = relative.Split(Path.DirectorySeparatorChar);
        return parts[0] != "Cache" && !(parts.Length > 1 && parts[0] == "Packages" && parts[1] == ".work");
    }

    private static FileStream OpenVerifiedCopy(DataMigrationRequest request, string relative, bool hasSource, string? expectedHash,
        PluginManager manager, byte[] buffer, CancellationToken token, out string targetHash, bool allowRebind = false)
    {
        var input = Path.Combine(request.Source, relative);
        var output = Path.Combine(request.Target, relative);
        CheckPath(output);
        var copy = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            targetHash = Convert.ToHexString(Hash(copy, buffer, token));
            if (hasSource)
            {
                CheckPath(input);
                using var original = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read);
                var sourceHash = Convert.ToHexString(Hash(original, buffer, token));
                if (expectedHash is not null && expectedHash != targetHash && (!allowRebind || expectedHash != sourceHash))
                    throw new IOException("迁移文件与已保存的摘要不一致：" + relative);
                if (sourceHash != targetHash && !manager.IsReboundRecoveryMetadata(request.Source, request.Target, relative))
                    throw new IOException("目标副本与旧数据不一致，已停止清理：" + relative);
            }
            else if (expectedHash is null)
                throw new IOException("旧版迁移清单没有文件摘要，请从备份恢复原文件，再重新启动：" + input);
            else if (targetHash != expectedHash)
                throw new IOException("迁移目标文件与已保存的摘要不一致：" + relative);
            return copy;
        }
        catch { copy.Dispose(); throw; }
    }

    private static byte[] Hash(Stream input, byte[] buffer, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int count;
        while ((count = input.Read(buffer)) != 0)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, count);
        }
        return hash.GetHashAndReset();
    }

    private static List<MigrationEntry> Inventory(string root, CancellationToken token, bool skipDownloads = false, bool includeCache = false)
    {
        var entries = new List<MigrationEntry>();
        foreach (var folder in includeCache ? DataDirectories.OwnedNames : DataDirectories.PersistentNames)
        {
            var start = Path.Combine(root, folder); CheckPath(start);
            if (DataDirectories.Exists(start)) Visit(start);
        }
        return entries.OrderBy(e => e.Relative, StringComparer.Ordinal).ToList();
        void Visit(string path)
        {
            token.ThrowIfCancellationRequested();
            CheckPath(path);
            var relative = Path.GetRelativePath(root, path);
            if (skipDownloads && relative == Path.Combine("Packages", ".work")) return; // Interrupted downloads are not installed packages.
            var info = new FileInfo(path);
            var directory = (info.Attributes & FileAttributes.Directory) != 0;
            entries.Add(new(relative, directory, directory ? 0 : info.Length, directory ? default : info.LastWriteTimeUtc));
            if (directory) foreach (var child in Directory.EnumerateFileSystemEntries(path)) Visit(child);
        }
    }

    private static void CheckPath(string path) => DataDirectories.CheckPath(path);
    private static bool Same(string a, string b) => string.Equals(AppPaths.Absolute(a), AppPaths.Absolute(b),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private sealed record MigrationEntry(string Relative, bool IsDirectory, long Length, DateTime Modified);
    private sealed record TargetMarker : IVersionedSettings
    {
        public int SchemaVersion { get; init; } = 1;
        public DataMigrationRequest? Request { get; init; }
        public string BootstrapRoot { get; init; } = "";
        public List<MigrationEntry>? Cleanup { get; init; }
        public Dictionary<string, string>? TargetHashes { get; init; }
    }
}
