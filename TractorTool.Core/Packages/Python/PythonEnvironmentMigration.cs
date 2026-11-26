using TractorTool.Contracts;
using TractorTool.Contracts.Processes;
using TractorTool.Core.Packages.Environments;

namespace TractorTool.Core.Packages.Python;

public sealed partial class PythonEnvironmentService
{
    public static IReadOnlyList<PythonMigrationEnvironment> ReadMigrationEnvironments(string packageRoot, bool validatePayload = false,
        CancellationToken token = default) =>
        ManagedEnvironmentService<PythonEnvironmentDefinition, PythonEnvironmentPolicy>.ReadMigrationEnvironments(packageRoot, validatePayload, token)
            .Select(item => new PythonMigrationEnvironment(item.RelativeDirectory, item.Definition)).ToArray();
    public static Task RebuildCopiedEnvironmentAsync(string targetPackageRoot, PythonMigrationEnvironment environment,
        HttpClient http, IProcessRunner processes, IAppLog log, Func<string, bool> sourceAllowed, CancellationToken token) =>
        ManagedEnvironmentService<PythonEnvironmentDefinition, PythonEnvironmentPolicy>.RebuildCopiedEnvironmentAsync(targetPackageRoot,
            new(environment.RelativeDirectory, environment.Definition),
            () => new PythonEnvironmentService(targetPackageRoot, environment.Definition.Rid, http, processes, log, sourceAllowed)._service, token);
    public static Task ValidateMigratedEnvironmentAsync(string targetPackageRoot, PythonMigrationEnvironment environment, CancellationToken token) =>
        ManagedEnvironmentService<PythonEnvironmentDefinition, PythonEnvironmentPolicy>.ValidateMigratedEnvironmentAsync(targetPackageRoot,
            new(environment.RelativeDirectory, environment.Definition), token);
}
