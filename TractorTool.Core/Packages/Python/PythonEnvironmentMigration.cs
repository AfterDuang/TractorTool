using TractorTool.Contracts;
using TractorTool.Contracts.Processes;
using TractorTool.Core.Storage;
using static TractorTool.Core.Packages.Python.PythonEnvironmentStorage;

namespace TractorTool.Core.Packages.Python;

public sealed partial class PythonEnvironmentService
{
    /// <summary>Exact owned identity directories; unknown/corrupt contents stop maintenance instead of broadening the scope.</summary>
    public static IReadOnlyList<PythonMigrationEnvironment> ReadMigrationEnvironments(string packageRoot, bool validatePayload = false,
        CancellationToken token = default)
    {
        packageRoot = AppPaths.Absolute(packageRoot);
        var root = Path.Combine(packageRoot, "uv");
        if (!IsOwnedRoot(root)) return [];
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            token.ThrowIfCancellationRequested();
            PackageFiles.CheckPath(entry);
            if (Path.GetFileName(entry) == RootMarker && File.Exists(entry)) continue;
            if (Path.GetFileName(entry) == "Environments" && Directory.Exists(entry)) continue;
            throw new InvalidDataException("Python 分类目录包含未知内容，未扩大迁移或清理范围。");
        }
        List<PythonMigrationEnvironment> result = [];
        var environments = Path.Combine(root, "Environments");
        if (!Directory.Exists(environments)) return result;
        foreach (var plugin in Directory.EnumerateFileSystemEntries(environments))
        {
            PackageFiles.CheckPath(plugin); AppPaths.ValidateId(Path.GetFileName(plugin));
            if (!Directory.Exists(plugin)) throw new InvalidDataException("Python 插件环境目录格式无效。");
            foreach (var identity in Directory.EnumerateFileSystemEntries(plugin))
            {
                token.ThrowIfCancellationRequested();
                PackageFiles.CheckPath(identity);
                if (!Directory.Exists(identity) || !PythonEnvironmentDefinitions.IsHash(Path.GetFileName(identity)))
                    throw new InvalidDataException("Python 环境目录标识无效。");
                var record = Read(identity);
                if (validatePayload)
                {
                    RequireNoUnknownFiles(identity, record);
                    foreach (var generation in Directory.EnumerateDirectories(identity))
                    {
                        if (!record.GenerationHashes.TryGetValue(Path.GetFileName(generation), out var hash))
                            throw new InvalidDataException("Python 环境存在未完成的准备代次，请先重试准备或备份处理后迁移。");
                        // A retired generation may have been partly cleaned before an interrupted shutdown.
                        // RequireNoUnknownFiles already proves the remaining paths are a subset of its recorded payload.
                        if (Path.GetFileName(generation) == record.Generation)
                            ValidateGenerationAsync(generation, hash, token).GetAwaiter().GetResult();
                    }
                    // A consented preparation can fail before creating any generation. Its input record remains ordinary copy data.
                    if (record.Generation is null) continue;
                }
                result.Add(new(Path.GetRelativePath(packageRoot, identity), record.Definition!));
                if (result.Count > 1024) throw new InvalidDataException("Python 环境数量超过限制。");
            }
        }
        return result;
    }

    public static async Task RebuildCopiedEnvironmentAsync(string targetPackageRoot, PythonMigrationEnvironment environment,
        HttpClient http, IProcessRunner processes, IAppLog log, Func<string, bool> sourceAllowed, CancellationToken token)
    {
        var directory = MigrationDirectory(targetPackageRoot, environment);
        var original = Read(directory);
        if (original.Definition != environment.Definition) throw new InvalidDataException("Python 迁移输入与原快照不一致。");
        if (original.Generation is not null && Same(original.InstalledRoot, Path.Combine(directory, original.Generation)))
        {
            await ValidateAsync(directory, original, token).ConfigureAwait(false);
            return;
        }
        await using var service = new PythonEnvironmentService(targetPackageRoot, environment.Definition.Rid, http, processes, log, sourceAllowed);
        await service.InstallAsync(environment.Definition.Identity, token).ConfigureAwait(false);
        await ValidateMigratedEnvironmentAsync(targetPackageRoot, environment, token).ConfigureAwait(false);
    }

    public static async Task ValidateMigratedEnvironmentAsync(string targetPackageRoot,
        PythonMigrationEnvironment environment, CancellationToken token)
    {
        var directory = MigrationDirectory(targetPackageRoot, environment);
        var record = Read(directory);
        if (record.Definition != environment.Definition) throw new InvalidDataException("目标 Python 环境身份已变化。");
        await ValidateAsync(directory, record, token).ConfigureAwait(false);
    }

    private static string MigrationDirectory(string packageRoot, PythonMigrationEnvironment environment)
    {
        PythonEnvironmentDefinitions.Validate(environment.Definition);
        var root = Path.Combine(AppPaths.Absolute(packageRoot), "uv");
        if (!IsOwnedRoot(root)) throw new InvalidDataException("Python 迁移目标分类归属丢失。");
        var expected = IdentityDirectory(root, environment.Definition);
        var actual = PackageFiles.SafePath(packageRoot, environment.RelativeDirectory);
        if (!Same(expected, actual)) throw new InvalidDataException("Python 迁移路径不属于记录的环境。");
        return actual;
    }
}
