using System.Security.Cryptography;
using System.Text.Json;
using TractorTool.Contracts;
using TractorTool.Core.Storage;

namespace TractorTool.Core.Packages.Environments;

internal class EnvironmentStorage<TDefinition, TPolicy>
    where TDefinition : class, IEnvironmentDefinition
    where TPolicy : EnvironmentPolicy<TDefinition>, new()
{
    internal static readonly TPolicy Policy = new();
    internal static string RootMarker => Policy.RootMarker;
    internal static string RootOwner => Policy.RootOwner;
    internal const string OwnerFile = ".owner";
    internal const string RecordName = "environment.json";
    internal const string RecordTemp = "environment.json.tmp";
    internal static string WorkMarker => Policy.WorkMarker;
    internal static string WorkOwner => Policy.WorkOwner;
    internal static string EnvironmentOwner => Policy.EnvironmentOwner;
    private const int MaxFiles = 60000;
    private const long MaxBytes = 8L * 1024 * 1024 * 1024;

    internal static bool IsOwnedRoot(string directory)
    {
        PackageFiles.CheckPath(directory);
        var marker = Path.Combine(directory, RootMarker); PackageFiles.CheckPath(marker);
        if (!File.Exists(marker)) return false;
        if (new FileInfo(marker).Length > 128 || File.ReadAllText(marker) != RootOwner)
            throw new InvalidDataException("依赖分类标记损坏，保留目录并暂停维护。");
        return true;
    }

    internal static void EnsureRoot(string root)
    {
        lock (FileLocks.For(root))
        {
        PackageFiles.CheckPath(root);
        if (IsOwnedRoot(root)) return;
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidDataException("依赖分类目录已被旧工具或未知目录占用；未覆盖原内容。");
        PublishMarker(root, RootMarker, RootOwner);
        }
    }

    internal static string IdentityDirectory(string root, TDefinition definition) =>
        PackageFiles.SafePath(root, $"Environments/{definition.PluginId}/{definition.Identity}");

    internal static void EnsureIdentity(string directory, TDefinition definition)
    {
        PackageFiles.CheckPath(directory);
        if (Directory.Exists(directory)) { CheckIdentity(directory, definition); return; }
        var stage = StageDirectory(ClassificationRoot(directory));
        WriteDurable(Path.Combine(stage, OwnerFile), EnvironmentOwner + definition.Identity);
        Store(stage).Save(new() { Definition = definition });
        Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
        Directory.Move(stage, directory); // No interpreter exists yet; publish the identity with its marker and record together.
    }

    internal static EnvironmentRecord Read(string directory)
    {
        PackageFiles.CheckPath(directory);
        var path = Path.Combine(directory, RecordName); PackageFiles.CheckPath(path);
        if (new FileInfo(path).Length is <= 0 or > 12 * 1024 * 1024) throw new InvalidDataException("依赖安装记录大小无效。");
        var record = Store(directory).Load();
        Policy.Validate(record.Definition!);
        CheckIdentity(directory, record.Definition!);
        if (record.Generation is not null && (!Guid.TryParseExact(record.Generation, "N", out _) ||
            !EnvironmentPolicy.IsHash(record.ManifestHash) || record.InstalledRoot is null || record.InstalledBytes is null))
            throw new InvalidDataException("依赖环境代次记录无效。");
        if (record.GenerationHashes is null || record.GenerationHashes.Count > 128 || record.GenerationHashes.Any(p =>
            !Guid.TryParseExact(p.Key, "N", out _) || !EnvironmentPolicy.IsHash(p.Value)) ||
            record.Generation is { } current && record.GenerationHashes.GetValueOrDefault(current) != record.ManifestHash)
            throw new InvalidDataException("依赖环境代次归属清单无效。");
        if (record.PreparingGenerations is null || record.PreparingGenerations.Count > 128 ||
            record.PreparingGenerations.Any(value => !Guid.TryParseExact(value, "N", out _)))
            throw new InvalidDataException("依赖准备代次记录无效。");
        CheckManifestOwnership(directory, record);
        return record;
    }

    internal static EnvironmentRecordFile Store(string directory) => new(directory);

    internal static void CheckIdentity(string directory, TDefinition definition)
    {
        PackageFiles.CheckPath(directory);
        var owner = Path.Combine(directory, OwnerFile); PackageFiles.CheckPath(owner);
        if (!File.Exists(owner) || new FileInfo(owner).Length > 256 || File.ReadAllText(owner) != EnvironmentOwner + definition.Identity ||
            Path.GetFileName(directory) != definition.Identity || Path.GetFileName(Path.GetDirectoryName(directory)) != definition.PluginId)
            throw new InvalidDataException("无法确认依赖环境目录归属，保留文件。");
        foreach (var child in Directory.EnumerateFileSystemEntries(directory))
        {
            PackageFiles.CheckPath(child);
            var name = Path.GetFileName(child);
            if (Directory.Exists(child)) CheckGeneration(child, definition.Identity);
            else if (name is not (OwnerFile or RecordName or RecordName + ".bak" or RecordTemp) && !IsGenerationManifest(name))
                throw new InvalidDataException("依赖环境目录含未知内容，暂停维护。");
        }
    }

    internal static void CheckGeneration(string directory, string identity)
    {
        PackageFiles.CheckPath(directory);
        var marker = Path.Combine(directory, OwnerFile); PackageFiles.CheckPath(marker);
        if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _) || !File.Exists(marker) ||
            new FileInfo(marker).Length > 256 || File.ReadAllText(marker) != EnvironmentOwner + identity)
            throw new InvalidDataException("依赖环境代次归属不明，未删除内容。");
    }

    internal static string NewGeneration(string directory, string identity)
    {
        var generation = PackageFiles.SafePath(directory, Guid.NewGuid().ToString("N"));
        var stage = StageDirectory(ClassificationRoot(directory));
        WriteDurable(Path.Combine(stage, OwnerFile), EnvironmentOwner + identity);
        var record = Read(directory);
        Store(directory).Save(record with { PreparingGenerations = [.. record.PreparingGenerations.Where(g => HasGenerationArtifacts(directory, g)), Path.GetFileName(generation)] });
        Directory.Move(stage, generation); // Move only the empty marked directory, before an installer creates absolute paths.
        return generation;
    }

    private static string ClassificationRoot(string identityDirectory)
    {
        var root = Path.GetFullPath(Path.Combine(identityDirectory, "..", "..", ".."));
        if (!IsOwnedRoot(root)) throw new InvalidDataException("依赖分类目录归属缺失。");
        return root;
    }

    private static string StageDirectory(string root)
    {
        var dataRoot = Path.GetDirectoryName(Path.GetDirectoryName(root))!;
        var work = Path.Combine(dataRoot, "Cache", Policy.Folder + "-work");
        lock (FileLocks.For(work))
        {
            PackageFiles.CheckPath(work);
            if (Directory.Exists(work) && Directory.EnumerateFileSystemEntries(work).Any()) CheckWork(work);
            else
            {
                PublishMarker(work, WorkMarker, WorkOwner);
            }
            var stage = Path.Combine(work, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            return stage;
        }
    }

    internal static void CheckWork(string work)
    {
        PackageFiles.CheckPath(work);
        var marker = Path.Combine(work, WorkMarker); PackageFiles.CheckPath(marker);
        if (!File.Exists(marker) || new FileInfo(marker).Length > 128 || File.ReadAllText(marker) != WorkOwner)
            throw new InvalidDataException("依赖事务暂存目录归属不明。");
    }

    private static void WriteDurable(string path, string contents)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(EnvironmentPolicy.Utf8.GetBytes(contents)); file.Flush(true);
    }

    private static void PublishMarker(string directory, string marker, string contents)
    {
        var parent = Path.GetDirectoryName(directory)!;
        PackageFiles.CheckPath(parent); Directory.CreateDirectory(parent);
        var scratch = Path.Combine(parent, "." + Policy.Folder + "-claim-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            WriteDurable(scratch, contents);
            Directory.CreateDirectory(directory);
            File.Move(scratch, Path.Combine(directory, marker));
        }
        finally { if (File.Exists(scratch)) File.Delete(scratch); }
    }

    internal static async Task<(string Hash, long Bytes)> WriteManifestAsync(string directory, CancellationToken token)
    {
        var files = await InventoryAsync(directory, token).ConfigureAwait(false);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(files, JsonOptions.Value);
        if (bytes.Length > 24 * 1024 * 1024) throw new InvalidDataException("依赖环境文件清单过大。");
        var identityDirectory = Path.GetDirectoryName(directory)!;
        var manifest = GenerationManifest(directory);
        var scratch = manifest + ".tmp";
        PackageFiles.CheckPath(scratch); PackageFiles.CheckPath(manifest);
        await using (var output = new FileStream(scratch, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
        { await output.WriteAsync(bytes, token).ConfigureAwait(false); output.Flush(true); }
        File.Move(scratch, manifest);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        // Record complete-file ownership before returning to the caller's cancel/publication boundary.
        var record = Read(identityDirectory);
        var hashes = record.GenerationHashes.Where(p => HasGenerationArtifacts(identityDirectory, p.Key)).ToDictionary();
        hashes[Path.GetFileName(directory)] = hash;
        Store(identityDirectory).Save(record with { GenerationHashes = hashes });
        return (hash, files.Sum(f => f.Length));
    }

    internal static async Task ValidateAsync(string identityDirectory, EnvironmentRecord record, CancellationToken token)
    {
        var generation = InstalledGeneration(identityDirectory, record);
        await ValidateGenerationAsync(generation, record.ManifestHash!, token).ConfigureAwait(false);
    }

    private static string InstalledGeneration(string identityDirectory, EnvironmentRecord record)
    {
        Policy.Validate(record.Definition!);
        CheckIdentity(identityDirectory, record.Definition!);
        if (record.Generation is null) throw new InvalidDataException("依赖环境尚未完成安装。");
        var generation = PackageFiles.SafePath(identityDirectory, record.Generation);
        if (!string.Equals(generation, record.InstalledRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("依赖环境路径已变化，需要按原锁重建。");
        CheckGeneration(generation, record.Definition!.Identity);
        return generation;
    }

    internal static async Task ValidateGenerationAsync(string generation, string manifestHash, CancellationToken token, bool allowMissing = false)
    {
        var bytes = await ReadManifestAsync(generation, manifestHash, token).ConfigureAwait(false);
        var expected = JsonSerializer.Deserialize<List<EnvironmentFile>>(bytes, JsonOptions.Value);
        var actual = await InventoryAsync(generation, token).ConfigureAwait(false);
        if (expected is null || expected.Count > MaxFiles || expected.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() != expected.Count)
            throw new InvalidDataException("依赖环境文件清单无效，请修复依赖。");
        var matches = allowMissing ? actual.All(expected.ToHashSet().Contains) : expected.SequenceEqual(actual);
        if (!matches)
            throw new InvalidDataException("依赖环境文件已变化，请修复依赖。");
    }

    private static async Task<byte[]> ReadManifestAsync(string generation, string manifestHash, CancellationToken token)
    {
        var manifestPath = GenerationManifest(generation); PackageFiles.CheckPath(manifestPath);
        if (new FileInfo(manifestPath).Length is <= 0 or > 24 * 1024 * 1024) throw new InvalidDataException("依赖文件清单大小无效。");
        var bytes = await File.ReadAllBytesAsync(manifestPath, token).ConfigureAwait(false);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != manifestHash)
            throw new InvalidDataException("依赖环境文件清单损坏，请修复。");
        return bytes;
    }

    internal static async Task ValidateMigrationPayloadAsync(string directory, EnvironmentRecord record, CancellationToken token,
        bool allowPreparing = false)
    {
        if (!allowPreparing) _ = InstalledGeneration(directory, record);
        else if (record.Generation is { } current)
            CheckGeneration(PackageFiles.SafePath(directory, current), record.Definition!.Identity);
        RequireNoUnknownFiles(directory, record);
        if (!allowPreparing && record.PreparingGenerations.Any(name =>
                !record.GenerationHashes.ContainsKey(name) && HasGenerationArtifacts(directory, name)))
            throw new InvalidDataException("依赖环境仍有未经完整验证的准备代次，迁移未提交。");
        foreach (var generation in Directory.EnumerateDirectories(directory))
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(generation);
            if (!record.GenerationHashes.TryGetValue(name, out var hash))
            {
                if (allowPreparing && record.PreparingGenerations.Contains(name, StringComparer.Ordinal)) continue;
                throw new InvalidDataException("依赖环境仍有未经完整验证的准备代次，迁移未提交。");
            }
            // Cleanup may already have removed some retired files; remaining bytes must still match.
            await ValidateGenerationAsync(generation, hash, token, allowMissing: name != record.Generation).ConfigureAwait(false);
        }
        // A completed cleanup may leave only its manifest. It still belongs to the
        // migration inventory, so its original digest must hold until it is removed.
        foreach (var (name, hash) in record.GenerationHashes)
        {
            var generation = PackageFiles.SafePath(directory, name);
            if (!Directory.Exists(generation) && File.Exists(GenerationManifest(generation)))
                _ = await ReadManifestAsync(generation, hash, token).ConfigureAwait(false);
        }
    }

    internal static void RequireNoUnknownFiles(string directory, EnvironmentRecord record)
    {
        CheckIdentity(directory, record.Definition!);
        CheckManifestOwnership(directory, record);
        foreach (var generation in Directory.EnumerateDirectories(directory))
        {
            if (!record.GenerationHashes.TryGetValue(Path.GetFileName(generation), out var hash))
            {
                if (record.PreparingGenerations.Contains(Path.GetFileName(generation), StringComparer.Ordinal)) continue;
                throw new InvalidDataException("存在归属未知的依赖代次，已保留，请先备份检查。");
            }
            RequireKnownGeneration(generation, hash);
        }
    }

    private static void RequireKnownGeneration(string generation, string hash)
    {
        var path = GenerationManifest(generation); PackageFiles.CheckPath(path);
        if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > 24 * 1024 * 1024)
            throw new InvalidDataException("依赖代次清单缺失，不能确认哪些文件可清理。");
        var bytes = File.ReadAllBytes(path);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != hash)
            throw new InvalidDataException("依赖代次清单已变化，保留文件。");
        var files = JsonSerializer.Deserialize<List<EnvironmentFile>>(bytes, JsonOptions.Value)
            ?? throw new InvalidDataException("依赖代次清单为空。");
        if (files.Count > MaxFiles) throw new InvalidDataException("依赖代次清单过大。");
        var owned = files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files) _ = PackageFiles.SafePath(generation, file.Path);
        void Check(string directory)
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                PackageFiles.CheckPath(child);
                if (Directory.Exists(child)) { Check(child); continue; }
                var relative = Path.GetRelativePath(generation, child).Replace('\\', '/');
                if (relative == OwnerFile) continue;
                if (!owned.Contains(relative)) throw new InvalidDataException("依赖环境包含新增的未知文件，未清理：" + relative);
            }
        }
        Check(generation);
    }

    private static async Task<List<EnvironmentFile>> InventoryAsync(string root, CancellationToken token)
    {
        List<EnvironmentFile> result = []; long bytes = 0;
        async Task Visit(string directory)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested(); PackageFiles.CheckPath(path);
                if (Directory.Exists(path)) { await Visit(path).ConfigureAwait(false); continue; }
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (relative == OwnerFile) continue;
                _ = PackageFiles.SafePath(root, relative);
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                bytes = checked(bytes + file.Length);
                if (result.Count >= MaxFiles || bytes > MaxBytes) throw new InvalidDataException("依赖环境大小或文件数超过限制。");
                result.Add(new(relative, file.Length, Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false))));
            }
        }
        await Visit(root).ConfigureAwait(false);
        return result.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
    }

    internal static void CleanGenerations(string directory, EnvironmentRecord current)
    {
        CheckIdentity(directory, current.Definition!);
        foreach (var child in Directory.EnumerateDirectories(directory))
            if (Path.GetFileName(child) != current.Generation)
            {
                CheckGeneration(child, current.Definition!.Identity);
                if (current.PreparingGenerations.Contains(Path.GetFileName(child), StringComparer.Ordinal))
                    Policy.PrepareGenerationRemoval(child, current.Definition);
                if (!current.GenerationHashes.TryGetValue(Path.GetFileName(child), out var hash))
                {
                    if (!current.PreparingGenerations.Contains(Path.GetFileName(child), StringComparer.Ordinal))
                        throw new InvalidDataException("旧依赖代次未记录完整文件归属，已保留。");
                }
                else RequireKnownGeneration(child, hash);
                PackageFiles.DeleteOwnedTree(directory, child, OwnerFile);
                DeleteGenerationManifest(child);
            }
        foreach (var (generation, hash) in current.GenerationHashes)
        {
            if (generation == current.Generation || Directory.Exists(Path.Combine(directory, generation))) continue;
            var path = GenerationManifest(Path.Combine(directory, generation)); PackageFiles.CheckPath(path);
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > 24 * 1024 * 1024 || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) != hash)
                    throw new InvalidDataException("已清理代次的清单发生变化，保留元数据。");
                DeleteGenerationManifest(Path.Combine(directory, generation));
            }
        }
    }

    // Migration calls this only after checking all recorded payload and manifest hashes.
    // Keep ordinary cleanup unchanged; recovery owns only the explicitly pending entries.
    internal static EnvironmentRecord RecoverPreparingGenerations(string directory, EnvironmentRecord record, CancellationToken token)
    {
        var pending = record.PreparingGenerations.Where(name => name != record.Generation).ToHashSet(StringComparer.Ordinal);
        if (pending.Count == 0) return record;
        foreach (var name in pending)
        {
            token.ThrowIfCancellationRequested();
            var generation = PackageFiles.SafePath(directory, name);
            if (Directory.Exists(generation))
            {
                CheckGeneration(generation, record.Definition!.Identity);
                Policy.PrepareGenerationRemoval(generation, record.Definition);
                PackageFiles.DeleteOwnedTree(directory, generation, OwnerFile);
            }
            // An interruption may already have removed the directory, leaving only
            // its registered manifest or scratch file beside the current generation.
            DeleteGenerationManifest(generation);
        }
        token.ThrowIfCancellationRequested();
        Store(directory).Save(record with
        {
            PreparingGenerations = record.PreparingGenerations.Where(name => !pending.Contains(name)).ToList(),
            GenerationHashes = record.GenerationHashes.Where(pair => !pending.Contains(pair.Key)).ToDictionary()
        });
        return Read(directory);
    }

    internal sealed record EnvironmentRecord : IVersionedSettings
    {
        public int SchemaVersion { get; init; } = 1;
        public TDefinition? Definition { get; init; }
        public string? Generation { get; init; }
        public string? InstalledRoot { get; init; }
        public string? ManifestHash { get; init; }
        public long? InstalledBytes { get; init; }
        public Dictionary<string, string> GenerationHashes { get; init; } = [];
        public List<string> PreparingGenerations { get; init; } = [];
    }

    internal static string PrepareRemovalDestination(string directory, EnvironmentRecord record)
    {
        RequireNoUnknownFiles(directory, record);
        foreach (var child in Directory.EnumerateDirectories(directory))
            if (record.PreparingGenerations.Contains(Path.GetFileName(child), StringComparer.Ordinal))
                Policy.PrepareGenerationRemoval(child, record.Definition!);
        var stage = StageDirectory(ClassificationRoot(directory));
        var parent = Path.Combine(stage, record.Definition!.PluginId);
        Directory.CreateDirectory(parent);
        return Path.Combine(parent, record.Definition.Identity);
    }

    internal static void DeleteRemovedIdentity(string directory, EnvironmentRecord record)
    {
        RequireNoUnknownFiles(directory, record);
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (record.PreparingGenerations.Contains(Path.GetFileName(child), StringComparer.Ordinal))
                Policy.PrepareGenerationRemoval(child, record.Definition!);
            PackageFiles.DeleteOwnedTree(directory, child, OwnerFile);
            DeleteGenerationManifest(child);
        }
        // All payload is gone. Only reserved metadata remains, so interruptions here cannot strand installed data.
        foreach (var file in Directory.EnumerateFiles(directory).OrderBy(file => Path.GetFileName(file) == OwnerFile)) File.Delete(file);
        Directory.Delete(directory);
        var parent = Path.GetDirectoryName(directory)!; Directory.Delete(parent);
        Directory.Delete(Path.GetDirectoryName(parent)!);
    }

    internal static void DeleteGenerationManifest(string generation)
    {
        var path = GenerationManifest(generation);
        PackageFiles.CheckPath(path); PackageFiles.CheckPath(path + ".tmp");
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
    }

    private static string GenerationManifest(string generation) => Path.Combine(Path.GetDirectoryName(generation)!, Path.GetFileName(generation) + ".files.json");
    private static bool HasGenerationArtifacts(string directory, string generation) => Directory.Exists(Path.Combine(directory, generation)) ||
        File.Exists(GenerationManifest(Path.Combine(directory, generation))) || File.Exists(GenerationManifest(Path.Combine(directory, generation)) + ".tmp");
    private static bool IsGenerationManifest(string name) => name.Length >= 32 && name[32..] is ".files.json" or ".files.json.tmp" &&
        Guid.TryParseExact(name[..32], "N", out _);
    private static void CheckManifestOwnership(string directory, EnvironmentRecord record)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileName(file);
            if (IsGenerationManifest(name) && !record.GenerationHashes.ContainsKey(name[..32]) &&
                !record.PreparingGenerations.Contains(name[..32], StringComparer.Ordinal))
                throw new InvalidDataException("依赖环境包含未登记的代次清单，已保留文件。");
        }
    }

    // This environment owns one reserved scratch name; an interrupted write cannot strand a random unknown file.
    internal sealed class EnvironmentRecordFile(string directory)
    {
        private readonly string _path = Path.Combine(directory, RecordName);
        internal EnvironmentRecord Load()
        {
            lock (FileLocks.For(_path))
            {
                PackageFiles.CheckPath(_path);
                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length is <= 0 or > 12 * 1024 * 1024) throw new InvalidDataException("依赖安装记录大小无效。");
                var record = JsonSerializer.Deserialize<EnvironmentRecord>(stream, JsonOptions.Value);
                if (record is not { SchemaVersion: 1 }) throw new InvalidDataException("依赖安装记录版本无效。");
                return record;
            }
        }
        internal void Save(EnvironmentRecord record)
        {
            lock (FileLocks.For(_path))
            {
                var scratch = Path.Combine(directory, RecordTemp);
                PackageFiles.CheckPath(_path); PackageFiles.CheckPath(scratch); PackageFiles.CheckPath(_path + ".bak");
                using (var stream = new FileStream(scratch, FileMode.Create, FileAccess.Write, FileShare.None))
                { JsonSerializer.Serialize(stream, record, JsonOptions.Value); stream.Flush(true); }
                if (File.Exists(_path)) File.Replace(scratch, _path, _path + ".bak");
                else File.Move(scratch, _path);
            }
        }
    }
    private sealed record EnvironmentFile(string Path, long Length, string Sha256);
}
