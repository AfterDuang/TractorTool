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
        if (AppPaths.Contains(value.Source, value.Target) || AppPaths.Contains(value.Target, value.Source))
            throw new InvalidDataException("迁移目录不能相同或互相包含。");
    }
}
public sealed record DataMigrationResult(AppPaths Paths, InstanceLease? TargetLease, string? Error = null);

// A startup transaction, not a live path switch. The caller holds the bootstrap/source leases
// and installation manager, and must not start services until this method has returned.
public sealed class DataRootMigration(AppPaths paths)
{
    private const string MarkerName = ".tractortool-migration.json";
    private const string TempName = ".tractortool-migration-copy.tmp";
    private static readonly string[] Folders = ["Config", "Data", "Packages", "Logs"];
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
            try
            {
                DataMigrationRequest.Validate(request);
                if (PathIssue(request) is { } issue) throw new InvalidDataException(issue);
                if (manager.DataMigrationBlockReason is { } blocked) throw new InvalidOperationException(blocked);
                if (!committed)
                {
                    token.ThrowIfCancellationRequested();
                    if (!File.Exists(Path.Combine(request.Target, MarkerName)) && TargetContentIssue(request.Target) is { } occupied)
                        throw new InvalidDataException(occupied);
                    targetLease = new InstanceLease(request.Target);
                    CheckTargetOwner(request, create: true);
                    CopyAndVerify(request, progress, token);
                    progress?.Report("正在校验插件回退快照…");
                    manager.ValidateDataRootMigration(request.Source, new AppPaths(paths.AppRoot, paths.BootstrapRoot, request.Target), token);
                    token.ThrowIfCancellationRequested();
                    var switched = state with { DataRoot = request.Target, PreviousDataRoot = request.Source };
                    Save(switched); // Commit point: all copied files verified; source is untouched.
                    state = switched;
                    committed = true;
                }
                else CheckTargetOwner(request, create: false);
                progress?.Report("正在完成插件回退记录…");
                var next = new AppPaths(paths.AppRoot, paths.BootstrapRoot, request.Target);
                // Idempotent finalization. A crash here retains the request with the new pointer;
                // the next startup completes it before any plugin can write to either copy.
                manager.CompleteDataRootMigration(request.Source, next, token);
                token.ThrowIfCancellationRequested();
                Save(state with { Migration = null });
                return new(next, targetLease);
            }
            catch (Exception ex)
            {
                targetLease?.Dispose();
                // A store failure must never roll back a pointer that reached disk. If reading
                // the pointer also fails, stop startup rather than write through an unknown root.
                committed |= Same(_store.Load().DataRoot ?? paths.BootstrapRoot, request.Target);
                if (ex is OperationCanceledException && token.IsCancellationRequested) throw;
                if (committed) throw new IOException("数据已复制并切换，迁移收尾尚未完成；请解决以下问题后重启。旧目录仍保留。\n" + ex.Message, ex);
                var error = "迁移未完成，继续使用原目录：" + ex.Message;
                Save(state with { Migration = request with { Error = error } });
                return new(paths, null, error);
            }
        }
    }

    private void Save(BootstrapSettings settings)
    {
        CheckPath(paths.BootstrapFile); CheckPath(paths.BootstrapFile + ".bak");
        _store.Save(settings);
    }

    private string? PathIssue(DataMigrationRequest request)
    {
        if (!Same(request.AppRoot, paths.AppRoot)) return "请从安排迁移时的软件位置完成迁移。";
        if (Path.GetDirectoryName(request.Target) is null) return "请选择专用子目录，不能使用磁盘根目录。";
        if (AppPaths.Contains(request.Source, request.Target) || AppPaths.Contains(request.Target, request.Source))
            return "迁移目录不能相同或互相包含。";
        if (AppPaths.Contains(paths.PluginRoot, request.Target) || AppPaths.Contains(request.Target, paths.PluginRoot))
            return "数据目录不能与软件 Plugins 目录互相包含。";
        if (AppPaths.Contains(paths.BootstrapRoot, request.Target) || AppPaths.Contains(request.Target, paths.BootstrapRoot))
            return "目标不能与固定启动配置目录重叠，请选择新的专用目录。";
        if (new Uri(request.Target).IsUnc) return "首版迁移仅支持本机磁盘，请选择本地目录。";
        CheckPath(request.Source); CheckPath(request.Target); CheckPath(paths.BootstrapFile);
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

    private void CheckTargetOwner(DataMigrationRequest request, bool create)
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
    }

    private static void CopyAndVerify(DataMigrationRequest request, IProgress<string>? progress, CancellationToken token)
    {
        if (!Directory.Exists(request.Source)) throw new DirectoryNotFoundException("原数据目录不可用，未创建空数据替代。");
        var source = Inventory(request.Source, token, skipDownloads: true);
        var expected = source.ToDictionary(e => e.Relative, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Directory.EnumerateFileSystemEntries(request.Target))
        {
            CheckPath(entry);
            if (!Folders.Contains(Path.GetFileName(entry), StringComparer.Ordinal) &&
                Path.GetFileName(entry) is not (MarkerName or "host.lock" or TempName))
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
        foreach (var entry in source)
        {
            token.ThrowIfCancellationRequested();
            var inputPath = Path.Combine(request.Source, entry.Relative);
            var outputPath = Path.Combine(request.Target, entry.Relative);
            CheckPath(inputPath); CheckPath(outputPath); CheckPath(temp);
            if (entry.IsDirectory) { Directory.CreateDirectory(outputPath); copiedEntries.Add(entry); continue; }
            using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (File.Exists(outputPath))
            {
                using var existing = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length != existing.Length || !Hash(input, buffer, token).AsSpan().SequenceEqual(Hash(existing, buffer, token)))
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
                using (var verify = File.OpenRead(temp))
                    if (!hash.GetHashAndReset().AsSpan().SequenceEqual(Hash(verify, buffer, token))) throw new IOException("迁移文件校验失败。");
                File.Move(temp, outputPath); // No overwrite, including unexpected target races.
                File.SetLastWriteTimeUtc(outputPath, entry.Modified);
            }
            // FAT/exFAT can round timestamps. Track the target's actual stored value rather
            // than reject an otherwise verified cross-volume copy for timestamp precision.
            copiedEntries.Add(entry with { Modified = File.GetLastWriteTimeUtc(outputPath) });
        }
        // The source lease protects cooperating hosts; detect ordinary external changes too.
        if (!source.SequenceEqual(Inventory(request.Source, token, skipDownloads: true))) throw new IOException("源数据在迁移过程中发生变化，启动指针未切换。");
        if (!copiedEntries.SequenceEqual(Inventory(request.Target, token))) throw new IOException("目标数据在迁移过程中发生变化，启动指针未切换。");
        token.ThrowIfCancellationRequested();
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

    private static List<MigrationEntry> Inventory(string root, CancellationToken token, bool skipDownloads = false)
    {
        var entries = new List<MigrationEntry>();
        foreach (var folder in Folders)
        {
            var start = Path.Combine(root, folder); CheckPath(start);
            if (File.Exists(start)) throw new IOException("应用数据目录被文件占用：" + start);
            if (Directory.Exists(start)) Visit(start);
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

    private static void CheckPath(string path)
    {
        for (string? current = AppPaths.Absolute(path); current is not null; current = Path.GetDirectoryName(current))
        {
            var attributes = new FileInfo(current).Attributes;
            if (attributes != (FileAttributes)(-1) && (attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("迁移不接受链接或重解析目录：" + current);
        }
    }
    private static bool Same(string a, string b) => string.Equals(AppPaths.Absolute(a), AppPaths.Absolute(b),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private sealed record MigrationEntry(string Relative, bool IsDirectory, long Length, DateTime Modified);
    private sealed record TargetMarker : IVersionedSettings
    {
        public int SchemaVersion { get; init; } = 1;
        public DataMigrationRequest? Request { get; init; }
        public string BootstrapRoot { get; init; } = "";
    }
}
