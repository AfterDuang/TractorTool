using TractorTool.Core.Packages.Python;
using TractorTool.Core.Packages.Node;
using TractorTool.Core.Packages.Environments;

namespace TractorTool.Core.Storage;

public sealed partial class DataRootMigration
{
    // The original source hashes remain in TargetMarker.TargetHashes. Generated target
    // files have their own inventory; they are never represented as byte-for-byte copies.
    private sealed record PythonRebuild(PythonMigrationEnvironment Environment,
        List<MigrationEntry>? TargetEntries, Dictionary<string, string>? TargetHashes);

    private sealed record NodeRebuild(NodeMigrationEnvironment Environment,
        List<MigrationEntry>? TargetEntries, Dictionary<string, string>? TargetHashes);

    private sealed record RebuildRegion(string Root, List<MigrationEntry>? Entries, Dictionary<string, string>? Hashes,
        PythonMigrationEnvironment? Python, NodeMigrationEnvironment? Node)
    {
        internal Task ValidateAsync(string packageRoot, CancellationToken token) => Python is { } python
            ? PythonEnvironmentService.ValidateMigratedEnvironmentAsync(packageRoot, python, token)
            : NodeEnvironmentService.ValidateMigratedEnvironmentAsync(packageRoot, Node!, token);
    }

    private static IEnumerable<RebuildRegion> RebuildRegions(TargetMarker marker)
    {
        foreach (var item in marker.PythonRebuilds ?? [])
        {
            if (item?.Environment?.Definition is null) throw new InvalidDataException("依赖迁移记录为空。");
            yield return new(PythonRelativeRoot(item.Environment), item.TargetEntries, item.TargetHashes, item.Environment, null);
        }
        foreach (var item in marker.NodeRebuilds ?? [])
        {
            if (item?.Environment?.Definition is null) throw new InvalidDataException("Node 迁移记录为空。");
            yield return new(NodeRelativeRoot(item.Environment), item.TargetEntries, item.TargetHashes, null, item.Environment);
        }
    }

    private TargetMarker RebuildLanguageEnvironments(DataMigrationRequest request, TargetMarker marker,
        IProgress<string>? progress, CancellationToken token)
    {
        ValidateLanguagePlan(marker);
        var packageRoot = Path.Combine(request.Source, "Packages");
        var python = PythonEnvironmentService.ReadMigrationEnvironments(packageRoot, validatePayload: true, token);
        var node = NodeEnvironmentService.ReadMigrationEnvironments(packageRoot, validatePayload: true, token);
        if (python.Count != (marker.PythonRebuilds?.Count ?? 0) || python.Any(environment =>
                marker.PythonRebuilds?.Any(p => p.Environment == environment) != true) ||
            node.Count != (marker.NodeRebuilds?.Count ?? 0) || node.Any(environment =>
                marker.NodeRebuilds?.Any(p => p.Environment == environment) != true))
            throw new IOException("原依赖环境身份已改变，迁移未提交，原数据已保留。");

        marker = RefreshLanguageCopyBaseline(request, marker, progress, token);
        var regions = RebuildRegions(marker).ToArray();
        for (var index = 0; index < regions.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var item = regions[index];
            if (item.Hashes is not null) continue;
            progress?.Report($"正在重建依赖环境… {index + 1}/{regions.Length}");
            var targetPackages = Path.Combine(request.Target, "Packages");
            if (item.Python is { } pythonEnvironment)
                (rebuildPythonEnvironment ?? throw new InvalidOperationException("当前启动入口未提供 Python 环境迁移能力，原数据已保留。"))
                    (targetPackages, pythonEnvironment, token).GetAwaiter().GetResult();
            else
                (rebuildNodeEnvironment ?? throw new InvalidOperationException("当前启动入口未提供 Node 环境迁移能力，原数据已保留。"))
                    (targetPackages, item.Node!, token).GetAwaiter().GetResult();
            item.ValidateAsync(targetPackages, token).GetAwaiter().GetResult();
            var entries = Inventory(request.Target, token).Where(e => Under(item.Root, e.Relative)).ToList();
            if (!entries.Any(e => SameRelative(e.Relative, item.Root) && e.IsDirectory))
                throw new IOException("依赖重建目标目录缺失，迁移未提交。");
            var hashes = HashEntries(request.Target, entries, token);
            marker = item.Python is { } originalPython
                ? marker with { PythonRebuilds = marker.PythonRebuilds!.Select(p => p.Environment == originalPython
                    ? p with { TargetEntries = entries, TargetHashes = hashes } : p).ToList() }
                : marker with { NodeRebuilds = marker.NodeRebuilds!.Select(p => p.Environment == item.Node
                    ? p with { TargetEntries = entries, TargetHashes = hashes } : p).ToList() };
            SaveTargetMarker(request, marker);
        }
        // A rebuild may download or take time. Recheck source and ordinary copies before commit.
        VerifyLanguageCopyBaseline(request, marker, token);
        using var verified = OpenRebuiltLanguageTargets(request, marker, token);
        return marker;
    }

    private static void ValidateLanguagePlan(TargetMarker marker)
    {
        if (marker.PythonRebuilds is null && marker.NodeRebuilds is null) return;
        if (marker.PythonRebuilds is { Count: < 1 or > 4096 } || marker.NodeRebuilds is { Count: < 1 or > 4096 } || marker.Cleanup is null || marker.TargetHashes is null)
            throw new InvalidDataException("依赖迁移记录缺少完整的原始复制证据。");
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in RebuildRegions(marker))
        {
            if (item.Python is { } python) PythonEnvironmentDefinitions.Validate(python.Definition);
            else NodeEnvironmentDefinitions.Validate(item.Node!.Definition);
            var root = item.Root;
            if (!roots.Add(root) || !marker.Cleanup.Any(e => e.IsDirectory && SameRelative(e.Relative, root)) ||
                (item.Entries is null) != (item.Hashes is null))
                throw new InvalidDataException("依赖迁移区域无效、重复或缺少校验证据。");
            if (item.Entries is not null)
            {
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!item.Entries.Any(e => e.IsDirectory && SameRelative(e.Relative, root)))
                    throw new InvalidDataException("依赖重建清单缺少环境根目录。");
                foreach (var entry in item.Entries)
                    if (entry is null || !Under(root, entry.Relative) || !SafeRelative(entry.Relative) ||
                        !paths.Add(entry.Relative) || entry.Length < 0 || !entry.IsDirectory &&
                        (!item.Hashes!.TryGetValue(entry.Relative, out var hash) || !EnvironmentPolicy.IsHash(hash)))
                        throw new InvalidDataException("依赖重建清单路径或摘要无效。");
                if (item.Hashes!.Count != item.Entries.Count(e => !e.IsDirectory))
                    throw new InvalidDataException("依赖重建清单与摘要数量不一致。");
            }
        }
    }

    private static string PythonRelativeRoot(PythonMigrationEnvironment environment)
    {
        var expected = Path.Combine("uv", "Environments", environment.Definition.PluginId, environment.Definition.Identity);
        if (!SafeRelative(environment.RelativeDirectory) || !SameRelative(environment.RelativeDirectory, expected))
            throw new InvalidDataException("依赖迁移只能重建已登记的独立环境目录。");
        return Path.Combine("Packages", expected);
    }

    private static bool SafeRelative(string relative) => !string.IsNullOrWhiteSpace(relative) &&
        !Path.IsPathRooted(relative) && !relative.Contains(':') && !relative.Contains('\0') &&
        (Path.AltDirectorySeparatorChar == Path.DirectorySeparatorChar || !relative.Contains(Path.AltDirectorySeparatorChar)) &&
        relative.Split(Path.DirectorySeparatorChar).All(p => p is not ("" or "." or "..") && p.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);

    private static bool SameRelative(string left, string right) => string.Equals(left, right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool Under(string root, string relative) => SameRelative(relative, root) ||
        relative.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string NodeRelativeRoot(NodeMigrationEnvironment environment)
    {
        var expected = Path.Combine("Node", "Environments", environment.Definition.PluginId, environment.Definition.Identity);
        if (!SafeRelative(environment.RelativeDirectory) || !SameRelative(environment.RelativeDirectory, expected))
            throw new InvalidDataException("Node 迁移只能重建已登记的独立环境目录。");
        return Path.Combine("Packages", expected);
    }

    private static bool IsLanguageRebuildPath(TargetMarker marker, string relative) =>
        RebuildRegions(marker).Any(item => Under(item.Root, relative));

    private static Dictionary<string, string> HashEntries(string root, IEnumerable<MigrationEntry> entries, CancellationToken token)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var buffer = new byte[1024 * 1024];
        foreach (var entry in entries.Where(e => !e.IsDirectory))
        {
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(root, entry.Relative); CheckPath(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != entry.Length) throw new IOException("依赖环境文件在校验时发生变化：" + entry.Relative);
            result.Add(entry.Relative, Convert.ToHexString(Hash(stream, buffer, token)));
        }
        return result;
    }

    private static TargetMarker RefreshLanguageCopyBaseline(DataMigrationRequest request, TargetMarker marker,
        IProgress<string>? progress, CancellationToken token)
    {
        var current = Inventory(request.Source, token, includeCache: true).ToDictionary(e => e.Relative, StringComparer.Ordinal);
        var original = marker.Cleanup!.ToDictionary(e => e.Relative, StringComparer.Ordinal);
        foreach (var entry in original.Values.Where(e => RequiresCopy(e.Relative)))
            if (!current.TryGetValue(entry.Relative, out var now) || entry != now)
                throw new IOException("原有数据在依赖环境迁移期间发生变化，未切换数据目录。");
        if (current.Values.Any(e => !original.ContainsKey(e.Relative) && IsLanguageRebuildPath(marker, e.Relative)))
            throw new IOException("原依赖环境出现未登记文件，未切换数据目录。");

        var buffer = new byte[1024 * 1024];
        foreach (var entry in original.Values.Where(e => !e.IsDirectory && RequiresCopy(e.Relative)))
            VerifyRebuildSource(request, entry.Relative, marker.TargetHashes!, buffer, token);

        // A failed startup may add logs or refresh disposable caches. Reuse the non-overwriting
        // copy transaction for ordinary files; rebuilt regions retain their original evidence.
        var (cleanup, hashes) = CopyAndVerify(request, progress, token, marker);
        if (cleanup.Count != current.Count || cleanup.Any(e => !current.TryGetValue(e.Relative, out var before) || e != before) ||
            marker.TargetHashes!.Any(p => !hashes.TryGetValue(p.Key, out var hash) || hash != p.Value))
            throw new IOException("原数据在重试复制期间发生变化，未切换数据目录。");
        marker = marker with { Cleanup = cleanup, TargetHashes = hashes };
        SaveTargetMarker(request, marker);
        return marker;
    }

    private static void VerifyLanguageCopyBaseline(DataMigrationRequest request, TargetMarker marker, CancellationToken token)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(request.Target))
        {
            CheckPath(path);
            if (!DataDirectories.OwnedNames.Contains(Path.GetFileName(path), StringComparer.Ordinal) &&
                Path.GetFileName(path) is not (MarkerName or MarkerBackupName or "host.lock") && Path.GetFileName(path) != MarkerPendingName(request))
                throw new IOException("迁移目标出现不属于此次迁移的根目录内容，原数据已保留。");
        }
        var cleanup = marker.Cleanup!;
        if (!cleanup.SequenceEqual(Inventory(request.Source, token, includeCache: true)))
            throw new IOException("原数据在依赖环境迁移期间发生变化，未切换数据目录。");
        var sourceFiles = cleanup.Where(e => !e.IsDirectory && RequiresCopy(e.Relative)).ToArray();
        if (marker.TargetHashes!.Count != sourceFiles.Length)
            throw new InvalidDataException("依赖迁移缺少原文件摘要。");
        var buffer = new byte[1024 * 1024];
        foreach (var entry in sourceFiles)
            VerifyRebuildSource(request, entry.Relative, marker.TargetHashes, buffer, token);

        var expected = cleanup.Where(e => RequiresCopy(e.Relative) && !IsLanguageRebuildPath(marker, e.Relative))
            .ToDictionary(e => e.Relative, StringComparer.Ordinal);
        var actual = Inventory(request.Target, token).Where(e => !IsLanguageRebuildPath(marker, e.Relative)).ToArray();
        if (expected.Count != actual.Length || actual.Any(e => !expected.TryGetValue(e.Relative, out var original) ||
            original.IsDirectory != e.IsDirectory || original.Length != e.Length))
            throw new IOException("迁移目标的普通文件发生变化，已保留原数据。");
        foreach (var entry in actual.Where(e => !e.IsDirectory))
        {
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(request.Target, entry.Relative); CheckPath(path);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!marker.TargetHashes.TryGetValue(entry.Relative, out var expectedHash) ||
                Convert.ToHexString(Hash(file, buffer, token)) != expectedHash)
                throw new IOException("迁移目标的普通文件摘要发生变化：" + entry.Relative);
        }
    }

    private static void VerifyRebuildSource(DataMigrationRequest request, string relative,
        IReadOnlyDictionary<string, string> hashes, byte[] buffer, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!hashes.TryGetValue(relative, out var expected) || !EnvironmentPolicy.IsHash(expected))
            throw new InvalidDataException("迁移缺少原文件摘要：" + relative);
        var path = Path.Combine(request.Source, relative); CheckPath(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexString(Hash(file, buffer, token)) != expected)
            throw new IOException("原文件与迁移前摘要不一致，已停止清理：" + relative);
    }

    private static VerifiedLanguageTargets OpenRebuiltLanguageTargets(DataMigrationRequest request, TargetMarker marker, CancellationToken token)
    {
        var result = new VerifiedLanguageTargets();
        try
        {
            ValidateLanguagePlan(marker);
            if (marker.PythonRebuilds is null && marker.NodeRebuilds is null) return result;
            var actual = Inventory(request.Target, token);
            var buffer = new byte[1024 * 1024];
            foreach (var rebuilt in RebuildRegions(marker))
            {
                if (rebuilt.Entries is null || rebuilt.Hashes is null)
                    throw new InvalidDataException("依赖环境尚未完成重建，原数据已保留。");
                var root = rebuilt.Root;
                if (!rebuilt.Entries.SequenceEqual(actual.Where(e => Under(root, e.Relative))))
                    throw new IOException("依赖重建环境出现新增、缺失或变化文件，原数据已保留。");
                rebuilt.ValidateAsync(Path.Combine(request.Target, "Packages"), token)
                    .GetAwaiter().GetResult();
                foreach (var entry in rebuilt.Entries.Where(e => !e.IsDirectory))
                {
                    token.ThrowIfCancellationRequested();
                    var path = Path.Combine(request.Target, entry.Relative); CheckPath(path);
                    var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    result.Files.Add(stream);
                    if (stream.Length != entry.Length || Convert.ToHexString(Hash(stream, buffer, token)) != rebuilt.Hashes[entry.Relative])
                        throw new IOException("依赖重建环境与已保存摘要不一致，原数据已保留：" + entry.Relative);
                }
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private sealed class VerifiedLanguageTargets : IDisposable
    {
        internal List<FileStream> Files { get; } = [];
        public void Dispose() { foreach (var file in Files) file.Dispose(); }
    }
}
