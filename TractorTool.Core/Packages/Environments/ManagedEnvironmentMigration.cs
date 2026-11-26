using TractorTool.Contracts;
using TractorTool.Contracts.Processes;
using TractorTool.Core.Storage;

namespace TractorTool.Core.Packages.Environments;

internal sealed partial class ManagedEnvironmentService<TDefinition, TPolicy>
{
    /// <summary>Exact owned identity directories; unknown/corrupt contents stop maintenance instead of broadening the scope.</summary>
    public static IReadOnlyList<MigrationEnvironment<TDefinition>> ReadMigrationEnvironments(string packageRoot, bool validatePayload = false,
        CancellationToken token = default)
    {
        packageRoot = AppPaths.Absolute(packageRoot);
        var root = Path.Combine(packageRoot, Policy.Folder);
        if (!IsOwnedRoot(root)) return [];
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            token.ThrowIfCancellationRequested();
            PackageFiles.CheckPath(entry);
            if (Path.GetFileName(entry) == RootMarker && File.Exists(entry)) continue;
            if (Path.GetFileName(entry) == "Environments" && Directory.Exists(entry)) continue;
            throw new InvalidDataException("依赖分类目录包含未知内容，未扩大迁移或清理范围。");
        }
        List<MigrationEnvironment<TDefinition>> result = [];
        var environments = Path.Combine(root, "Environments");
        if (!Directory.Exists(environments)) return result;
        foreach (var plugin in Directory.EnumerateFileSystemEntries(environments))
        {
            PackageFiles.CheckPath(plugin); AppPaths.ValidateId(Path.GetFileName(plugin));
            if (!Directory.Exists(plugin)) throw new InvalidDataException("依赖插件环境目录格式无效。");
            foreach (var identity in Directory.EnumerateFileSystemEntries(plugin))
            {
                token.ThrowIfCancellationRequested();
                PackageFiles.CheckPath(identity);
                if (!Directory.Exists(identity) || !EnvironmentPolicy.IsHash(Path.GetFileName(identity)))
                    throw new InvalidDataException("依赖环境目录标识无效。");
                var record = Read(identity);
                if (validatePayload)
                {
                    RequireNoUnknownFiles(identity, record);
                    foreach (var generation in Directory.EnumerateDirectories(identity))
                    {
                        if (!record.GenerationHashes.TryGetValue(Path.GetFileName(generation), out var hash))
                            throw new InvalidDataException("依赖环境存在未完成的准备代次，请先重试准备或备份处理后迁移。");
                        ValidateGenerationAsync(generation, hash, token,
                            allowMissing: Path.GetFileName(generation) != record.Generation).GetAwaiter().GetResult();
                    }
                    // A consented preparation can fail before creating any generation. Its input record remains ordinary copy data.
                    if (record.Generation is null) continue;
                }
                result.Add(new(Path.GetRelativePath(packageRoot, identity), record.Definition!));
                if (result.Count > 1024) throw new InvalidDataException("依赖环境数量超过限制。");
            }
        }
        return result;
    }

    public static async Task RebuildCopiedEnvironmentAsync(string targetPackageRoot, MigrationEnvironment<TDefinition> environment,
        Func<ManagedEnvironmentService<TDefinition, TPolicy>> createService, CancellationToken token)
    {
        var directory = MigrationDirectory(targetPackageRoot, environment);
        var original = Read(directory);
        if (!EqualityComparer<TDefinition>.Default.Equals(original.Definition, environment.Definition))
            throw new InvalidDataException("依赖迁移输入与原快照不一致。");
        if (original.Generation is not null && Same(original.InstalledRoot, Path.Combine(directory, original.Generation)))
        {
            // Publication can complete before preparation cleanup. Verify every recorded
            // payload first, then resume only that explicitly registered cleanup.
            await ValidateMigrationPayloadAsync(directory, original, token, allowPreparing: true).ConfigureAwait(false);
            var recovered = RecoverPreparingGenerations(directory, original, token);
            await ValidateMigrationPayloadAsync(directory, recovered, token).ConfigureAwait(false);
            return;
        }
        // Check copied bytes before an installation can retire and clean the previous generation.
        await ValidateMigrationPayloadAsync(directory, original, token, allowPreparing: true).ConfigureAwait(false);
        await using var service = createService();
        await service.InstallAsync(environment.Definition.Identity, token).ConfigureAwait(false);
        await ValidateMigratedEnvironmentAsync(targetPackageRoot, environment, token).ConfigureAwait(false);
    }

    public static async Task ValidateMigratedEnvironmentAsync(string targetPackageRoot,
        MigrationEnvironment<TDefinition> environment, CancellationToken token)
    {
        var directory = MigrationDirectory(targetPackageRoot, environment);
        var record = Read(directory);
        if (!EqualityComparer<TDefinition>.Default.Equals(record.Definition, environment.Definition))
            throw new InvalidDataException("目标依赖环境身份已变化。");
        // Retired generations are included in the migration inventory too. Never adopt
        // unrecorded payload merely because the newly installed generation is valid.
        await ValidateMigrationPayloadAsync(directory, record, token).ConfigureAwait(false);
    }

    private static string MigrationDirectory(string packageRoot, MigrationEnvironment<TDefinition> environment)
    {
        Policy.Validate(environment.Definition);
        var root = Path.Combine(AppPaths.Absolute(packageRoot), Policy.Folder);
        if (!IsOwnedRoot(root)) throw new InvalidDataException("依赖迁移目标分类归属丢失。");
        var expected = IdentityDirectory(root, environment.Definition);
        var actual = PackageFiles.SafePath(packageRoot, environment.RelativeDirectory);
        if (!Same(expected, actual)) throw new InvalidDataException("依赖迁移路径不属于记录的环境。");
        return actual;
    }
}
