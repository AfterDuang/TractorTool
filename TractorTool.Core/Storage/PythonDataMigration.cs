using TractorTool.Core.Packages.Python;

namespace TractorTool.Core.Storage;

public sealed partial class DataRootMigration
{
    // The original source hashes remain in TargetMarker.TargetHashes. Generated target
    // files have their own inventory; they are never represented as byte-for-byte copies.
    private sealed record PythonRebuild(PythonMigrationEnvironment Environment,
        List<MigrationEntry>? TargetEntries, Dictionary<string, string>? TargetHashes);

    private TargetMarker RebuildPythonEnvironments(DataMigrationRequest request, TargetMarker marker,
        IProgress<string>? progress, CancellationToken token)
    {
        ValidatePythonPlan(marker);
        VerifyPythonCopyBaseline(request, marker, token);
        var current = PythonEnvironmentService.ReadMigrationEnvironments(Path.Combine(request.Source, "Packages"), validatePayload: true, token);
        if (current.Count != marker.PythonRebuilds!.Count || current.Any(environment =>
            !marker.PythonRebuilds.Any(p => p.Environment == environment)))
            throw new IOException("原 Python 环境身份已改变，迁移未提交，原数据已保留。");

        var replacements = marker.PythonRebuilds.ToList();
        for (var index = 0; index < replacements.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var item = replacements[index];
            if (item.TargetHashes is null)
            {
                var rebuild = rebuildPythonEnvironment ?? throw new InvalidOperationException("当前启动入口未提供 Python 环境迁移能力，原数据已保留。");
                progress?.Report($"正在重建 Python 环境… {index + 1}/{replacements.Count}");
                rebuild(Path.Combine(request.Target, "Packages"), item.Environment, token).GetAwaiter().GetResult();
                PythonEnvironmentService.ValidateMigratedEnvironmentAsync(Path.Combine(request.Target, "Packages"), item.Environment, token)
                    .GetAwaiter().GetResult();
                var prefix = PythonRelativeRoot(item.Environment);
                var entries = Inventory(request.Target, token).Where(e => Under(prefix, e.Relative)).ToList();
                if (!entries.Any(e => SameRelative(e.Relative, prefix) && e.IsDirectory))
                    throw new IOException("Python 重建目标目录缺失，迁移未提交。");
                var hashes = HashEntries(request.Target, entries, token);
                replacements[index] = item with { TargetEntries = entries, TargetHashes = hashes };
                marker = marker with { PythonRebuilds = replacements.ToList() };
                SaveTargetMarker(request, marker);
            }
        }
        // Rebuilding may take time or download files. Recheck both the unchanged source
        // and ordinary copies immediately before allowing the bootstrap commit.
        VerifyPythonCopyBaseline(request, marker, token);
        using var verified = OpenRebuiltPythonTargets(request, marker, token);
        return marker;
    }

    private static void ValidatePythonPlan(TargetMarker marker)
    {
        if (marker.PythonRebuilds is null) return;
        if (marker.PythonRebuilds.Count is < 1 or > 4096 || marker.Cleanup is null || marker.TargetHashes is null)
            throw new InvalidDataException("Python 迁移记录缺少完整的原始复制证据。");
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in marker.PythonRebuilds)
        {
            if (item?.Environment?.Definition is null) throw new InvalidDataException("Python 迁移记录为空。");
            PythonEnvironmentDefinitions.Validate(item.Environment.Definition);
            var root = PythonRelativeRoot(item.Environment);
            if (!roots.Add(root) || !marker.Cleanup.Any(e => e.IsDirectory && SameRelative(e.Relative, root)) ||
                (item.TargetEntries is null) != (item.TargetHashes is null))
                throw new InvalidDataException("Python 迁移区域无效、重复或缺少校验证据。");
            if (item.TargetEntries is not null)
            {
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!item.TargetEntries.Any(e => e.IsDirectory && SameRelative(e.Relative, root)))
                    throw new InvalidDataException("Python 重建清单缺少环境根目录。");
                foreach (var entry in item.TargetEntries)
                    if (entry is null || !Under(root, entry.Relative) || !SafeRelative(entry.Relative) ||
                        !paths.Add(entry.Relative) || entry.Length < 0 || !entry.IsDirectory &&
                        (!item.TargetHashes!.TryGetValue(entry.Relative, out var hash) || !PythonEnvironmentDefinitions.IsHash(hash)))
                        throw new InvalidDataException("Python 重建清单路径或摘要无效。");
                if (item.TargetHashes!.Count != item.TargetEntries.Count(e => !e.IsDirectory))
                    throw new InvalidDataException("Python 重建清单与摘要数量不一致。");
            }
        }
    }

    private static string PythonRelativeRoot(PythonMigrationEnvironment environment)
    {
        var expected = Path.Combine("uv", "Environments", environment.Definition.PluginId, environment.Definition.Identity);
        if (!SafeRelative(environment.RelativeDirectory) || !SameRelative(environment.RelativeDirectory, expected))
            throw new InvalidDataException("Python 迁移只能重建已登记的独立环境目录。");
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

    private static bool IsPythonRebuildPath(TargetMarker marker, string relative) =>
        marker.PythonRebuilds?.Any(item => Under(PythonRelativeRoot(item.Environment), relative)) == true;

    private static Dictionary<string, string> HashEntries(string root, IEnumerable<MigrationEntry> entries, CancellationToken token)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var buffer = new byte[1024 * 1024];
        foreach (var entry in entries.Where(e => !e.IsDirectory))
        {
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(root, entry.Relative); CheckPath(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != entry.Length) throw new IOException("Python 环境文件在校验时发生变化：" + entry.Relative);
            result.Add(entry.Relative, Convert.ToHexString(Hash(stream, buffer, token)));
        }
        return result;
    }

    private static void VerifyPythonCopyBaseline(DataMigrationRequest request, TargetMarker marker, CancellationToken token)
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
            throw new IOException("原数据在 Python 环境迁移期间发生变化，未切换数据目录。");
        var sourceFiles = cleanup.Where(e => !e.IsDirectory && RequiresCopy(e.Relative)).ToArray();
        if (marker.TargetHashes!.Count != sourceFiles.Length)
            throw new InvalidDataException("Python 迁移缺少原文件摘要。");
        var buffer = new byte[1024 * 1024];
        foreach (var entry in sourceFiles)
            VerifyRebuildSource(request, entry.Relative, marker.TargetHashes, buffer, token);

        var expected = cleanup.Where(e => RequiresCopy(e.Relative) && !IsPythonRebuildPath(marker, e.Relative))
            .ToDictionary(e => e.Relative, StringComparer.Ordinal);
        var actual = Inventory(request.Target, token).Where(e => !IsPythonRebuildPath(marker, e.Relative)).ToArray();
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
        if (!hashes.TryGetValue(relative, out var expected) || !PythonEnvironmentDefinitions.IsHash(expected))
            throw new InvalidDataException("迁移缺少原文件摘要：" + relative);
        var path = Path.Combine(request.Source, relative); CheckPath(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexString(Hash(file, buffer, token)) != expected)
            throw new IOException("原文件与迁移前摘要不一致，已停止清理：" + relative);
    }

    private static VerifiedPythonTargets OpenRebuiltPythonTargets(DataMigrationRequest request, TargetMarker marker, CancellationToken token)
    {
        var result = new VerifiedPythonTargets();
        try
        {
            ValidatePythonPlan(marker);
            if (marker.PythonRebuilds is null) return result;
            var actual = Inventory(request.Target, token);
            var buffer = new byte[1024 * 1024];
            foreach (var rebuilt in marker.PythonRebuilds)
            {
                if (rebuilt.TargetEntries is null || rebuilt.TargetHashes is null)
                    throw new InvalidDataException("Python 环境尚未完成重建，原数据已保留。");
                var root = PythonRelativeRoot(rebuilt.Environment);
                if (!rebuilt.TargetEntries.SequenceEqual(actual.Where(e => Under(root, e.Relative))))
                    throw new IOException("Python 重建环境出现新增、缺失或变化文件，原数据已保留。");
                PythonEnvironmentService.ValidateMigratedEnvironmentAsync(Path.Combine(request.Target, "Packages"), rebuilt.Environment, token)
                    .GetAwaiter().GetResult();
                foreach (var entry in rebuilt.TargetEntries.Where(e => !e.IsDirectory))
                {
                    token.ThrowIfCancellationRequested();
                    var path = Path.Combine(request.Target, entry.Relative); CheckPath(path);
                    var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    result.Files.Add(stream);
                    if (stream.Length != entry.Length || Convert.ToHexString(Hash(stream, buffer, token)) != rebuilt.TargetHashes[entry.Relative])
                        throw new IOException("Python 重建环境与已保存摘要不一致，原数据已保留：" + entry.Relative);
                }
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private sealed class VerifiedPythonTargets : IDisposable
    {
        internal List<FileStream> Files { get; } = [];
        public void Dispose() { foreach (var file in Files) file.Dispose(); }
    }
}
