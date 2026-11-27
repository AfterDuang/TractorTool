using System.Text.Json;
using TractorTool.Contracts;
using TractorTool.Core.Storage;

namespace TractorTool.Core.Packages.Environments;

internal partial class EnvironmentStorage<TDefinition, TPolicy>
{
    private const string RemovalSuffix = ".removal.json";
    private const string RemovalPendingSuffix = ".removal.pending";

    private static void PublishRemovalRecord(string stage, EnvironmentRecord record)
    {
        // Publish outside the deleted tree, only after its complete record is durable.
        var journal = stage + RemovalSuffix;
        var pending = stage + "." + record.Definition!.Identity + RemovalPendingSuffix;
        PackageFiles.CheckPath(journal); PackageFiles.CheckPath(pending);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions.Value);
        if (bytes.Length > 12 * 1024 * 1024) throw new InvalidDataException("依赖卸载记录过大。");
        var created = false;
        try
        {
            using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { created = true; file.Write(bytes); file.Flush(true); }
            File.Move(pending, journal);
        }
        finally { if (created && File.Exists(pending)) File.Delete(pending); }
    }

    internal static void RecoverRemovedIdentities(string root, IAppLog log)
    {
        var work = WorkDirectory(root);
        try
        {
            PackageFiles.CheckPath(work);
            if (!Directory.Exists(work)) return;
            CheckWork(work);
            foreach (var pending in Directory.EnumerateFiles(work, "*" + RemovalPendingSuffix))
            {
                try { RecoverPendingRemoval(root, pending); }
                catch (Exception ex) { log.Write(AppLogLevel.Warning, "Packages", "依赖卸载准备记录无法确认，已保留：" + pending, ex); }
            }
            foreach (var journal in Directory.EnumerateFiles(work, "*" + RemovalSuffix))
            {
                var stage = journal[..^RemovalSuffix.Length];
                if (HasPendingRemoval(stage)) continue;
                try { CompleteRemoval(stage, verifyPayload: true); }
                catch (Exception ex) { log.Write(AppLogLevel.Warning, "Packages", "依赖卸载暂存清理未完成，已保留：" + stage, ex); }
            }
            // Older hosts moved an entire identity here without a separate removal journal.
            foreach (var stage in Directory.EnumerateDirectories(work))
            {
                if (File.Exists(stage + RemovalSuffix) || HasPendingRemoval(stage)) continue;
                try
                {
                    PackageFiles.CheckPath(stage);
                    if (!Guid.TryParseExact(Path.GetFileName(stage), "N", out _)) continue;
                    var children = Directory.GetFileSystemEntries(stage);
                    if (children.Length != 1 || !Directory.Exists(children[0])) continue;
                    PackageFiles.CheckPath(children[0]);
                    var identities = Directory.GetFileSystemEntries(children[0]);
                    if (identities.Length != 1 || !Directory.Exists(identities[0]) ||
                        !EnvironmentPolicy.IsHash(Path.GetFileName(identities[0]))) continue;
                    var record = Read(identities[0]);
                    RequireNoUnknownFiles(identities[0], record);
                    ValidateRemovalPayload(identities[0], record);
                    PublishRemovalRecord(stage, record);
                    CompleteRemoval(stage);
                }
                catch (Exception ex) { log.Write(AppLogLevel.Warning, "Packages", "依赖暂存目录无法确认或清理，已保留：" + stage, ex); }
            }
        }
        catch (Exception ex) { log.Write(AppLogLevel.Warning, "Packages", "检查依赖卸载暂存失败，已保留文件。", ex); }
    }

    private static bool HasPendingRemoval(string stage) => Directory.EnumerateFiles(Path.GetDirectoryName(stage)!,
        Path.GetFileName(stage) + ".*" + RemovalPendingSuffix).Any();

    private static void RecoverPendingRemoval(string root, string pending)
    {
        PackageFiles.CheckPath(pending);
        var name = Path.GetFileName(pending)[..^RemovalPendingSuffix.Length];
        if (name.Length != 97 || name[32] != '.' || !Guid.TryParseExact(name[..32], "N", out _) || !EnvironmentPolicy.IsHash(name[33..]))
            throw new InvalidDataException("依赖卸载准备路径无效。");
        var stage = PackageFiles.SafePath(Path.GetDirectoryName(pending)!, name[..32]);
        if (File.Exists(stage + RemovalSuffix)) throw new InvalidDataException("依赖卸载已存在正式记录，未替换或清理准备资料。");
        var children = Directory.GetFileSystemEntries(stage);
        if (children.Length != 1 || !Directory.Exists(children[0])) throw new InvalidDataException("依赖卸载暂存结构不明。");
        var parent = children[0]; PackageFiles.CheckPath(parent);
        var identity = name[33..]; var plugin = Path.GetFileName(parent); AppPaths.ValidateId(plugin);
        var directory = PackageFiles.SafePath(parent, identity);
        RequireOnlyDirectory(parent, directory);
        var moved = Directory.Exists(directory);
        if (!moved && !IsOwnedRoot(root)) throw new InvalidDataException("原依赖分类归属缺失，未清理卸载准备资料。");
        var original = PackageFiles.SafePath(root, $"Environments/{plugin}/{identity}");
        var record = Read(moved ? directory : original);
        var expected = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions.Value);
        using (var file = new FileStream(pending, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (file.Length > expected.Length) throw new InvalidDataException("依赖卸载准备资料与归属记录不符。");
            var bytes = new byte[(int)file.Length]; file.ReadExactly(bytes);
            if (!bytes.AsSpan().SequenceEqual(expected.AsSpan(0, bytes.Length)))
                throw new InvalidDataException("依赖卸载准备资料已变化，未删除内容。");
        }
        if (moved)
        {
            RequireNoUnknownFiles(directory, record);
            ValidateRemovalPayload(directory, record);
            File.Delete(pending);
            PublishRemovalRecord(stage, record);
            CompleteRemoval(stage);
        }
        else
        {
            // Publication did not finish, so the original installation was never moved.
            File.Delete(pending); Directory.Delete(parent); Directory.Delete(stage);
        }
    }

    private static void CompleteRemoval(string stage, bool verifyPayload = false)
    {
        PackageFiles.CheckPath(stage);
        if (!Guid.TryParseExact(Path.GetFileName(stage), "N", out _)) throw new InvalidDataException("依赖卸载事务目录无效。");
        if (File.Exists(stage)) throw new InvalidDataException("依赖卸载事务路径已被文件占用。");
        CheckWork(Path.GetDirectoryName(stage)!);
        var journal = stage + RemovalSuffix; PackageFiles.CheckPath(journal);
        EnvironmentRecord record;
        using (var stream = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length is <= 0 or > 12 * 1024 * 1024) throw new InvalidDataException("依赖卸载记录大小无效。");
            record = JsonSerializer.Deserialize<EnvironmentRecord>(stream, JsonOptions.Value)
                ?? throw new InvalidDataException("依赖卸载记录为空。");
        }
        ValidateRecord(record);
        if (Directory.Exists(stage))
        {
            var parent = PackageFiles.SafePath(stage, record.Definition!.PluginId);
            var directory = PackageFiles.SafePath(parent, record.Definition.Identity);
            RequireOnlyDirectory(stage, parent);
            if (Directory.Exists(parent))
            {
                RequireOnlyDirectory(parent, directory);
                if (Directory.Exists(directory))
                {
                    if (verifyPayload) ValidateRemovalPayload(directory, record);
                    // A crash after deleting a generation's marker can leave only its empty directory.
                    foreach (var generation in Directory.EnumerateDirectories(directory))
                    {
                        var name = Path.GetFileName(generation); PackageFiles.CheckPath(generation);
                        if ((record.GenerationHashes.ContainsKey(name) || record.PreparingGenerations.Contains(name, StringComparer.Ordinal)) &&
                            !Directory.EnumerateFileSystemEntries(generation).Any()) Directory.Delete(generation);
                    }
                    if (Directory.EnumerateFileSystemEntries(directory).Any())
                    {
                        RequireNoUnknownFiles(directory, record);
                        foreach (var generation in Directory.EnumerateDirectories(directory))
                        {
                            if (record.PreparingGenerations.Contains(Path.GetFileName(generation), StringComparer.Ordinal))
                                Policy.PrepareGenerationRemoval(generation, record.Definition);
                            PackageFiles.DeleteOwnedTree(directory, generation, OwnerFile);
                            DeleteGenerationManifest(generation);
                        }
                        foreach (var file in Directory.EnumerateFiles(directory).OrderBy(file => Path.GetFileName(file) == OwnerFile))
                        { PackageFiles.CheckPath(file); File.Delete(file); }
                    }
                    Directory.Delete(directory);
                }
                Directory.Delete(parent);
            }
            Directory.Delete(stage);
        }
        File.Delete(journal);
    }

    private static void ValidateRemovalPayload(string directory, EnvironmentRecord record)
    {
        // Recovery has no fresh user action authorizing removal of changed bytes.
        // Reuse the persisted manifests, allowing only files already removed by the interrupted cleanup.
        foreach (var generation in Directory.EnumerateDirectories(directory))
        {
            PackageFiles.CheckPath(generation);
            if (!record.GenerationHashes.TryGetValue(Path.GetFileName(generation), out var hash))
                throw new InvalidDataException("卸载暂存缺少可信的载荷摘要，已保留，请备份检查后处理。");
            ValidateGenerationAsync(generation, hash, CancellationToken.None, allowMissing: true).GetAwaiter().GetResult();
        }
        foreach (var (name, hash) in record.GenerationHashes)
        {
            var generation = PackageFiles.SafePath(directory, name);
            if (!Directory.Exists(generation) && File.Exists(GenerationManifest(generation)))
                _ = ReadManifestAsync(generation, hash, CancellationToken.None).GetAwaiter().GetResult();
        }
        if (record.PreparingGenerations.Any(name => !record.GenerationHashes.ContainsKey(name) && HasGenerationArtifacts(directory, name)))
            throw new InvalidDataException("卸载暂存仍有未经校验的准备资料，已保留，请备份检查后处理。");
    }

    private static void RequireOnlyDirectory(string parent, string expected)
    {
        foreach (var child in Directory.EnumerateFileSystemEntries(parent))
        {
            PackageFiles.CheckPath(child);
            if (child != expected || !Directory.Exists(child))
                throw new InvalidDataException("依赖卸载暂存包含未知内容，未删除文件。");
        }
    }
}
