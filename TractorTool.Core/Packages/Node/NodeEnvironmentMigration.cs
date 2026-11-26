using TractorTool.Contracts;
using TractorTool.Contracts.Processes;
using TractorTool.Core.Packages.Environments;

namespace TractorTool.Core.Packages.Node;

public sealed partial class NodeEnvironmentService
{
    public static IReadOnlyList<NodeMigrationEnvironment> ReadMigrationEnvironments(string packageRoot, bool validatePayload = false,
        CancellationToken token = default) =>
        ManagedEnvironmentService<NodeEnvironmentDefinition, NodeEnvironmentPolicy>.ReadMigrationEnvironments(packageRoot, validatePayload, token)
            .Select(item => new NodeMigrationEnvironment(item.RelativeDirectory, item.Definition)).ToArray();
    public static Task RebuildCopiedEnvironmentAsync(string targetPackageRoot, NodeMigrationEnvironment environment,
        HttpClient http, IProcessRunner processes, IAppLog log, Func<string, bool> sourceAllowed, CancellationToken token) =>
        ManagedEnvironmentService<NodeEnvironmentDefinition, NodeEnvironmentPolicy>.RebuildCopiedEnvironmentAsync(targetPackageRoot,
            new(environment.RelativeDirectory, environment.Definition),
            () => new NodeEnvironmentService(targetPackageRoot, environment.Definition.Rid, http, processes, log, sourceAllowed)._service, token);
    public static Task ValidateMigratedEnvironmentAsync(string targetPackageRoot, NodeMigrationEnvironment environment, CancellationToken token) =>
        ManagedEnvironmentService<NodeEnvironmentDefinition, NodeEnvironmentPolicy>.ValidateMigratedEnvironmentAsync(targetPackageRoot,
            new(environment.RelativeDirectory, environment.Definition), token);
}
