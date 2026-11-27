using TractorTool.Contracts.Processes;
using TractorTool.Core.Packages.Environments;

namespace TractorTool.Core.Packages.Node;

internal sealed class NodeEnvironmentPolicy : EnvironmentPolicy<NodeEnvironmentDefinition>
{
    internal override string Name => "Node";
    internal override string Folder => "Node";
    internal override string OwnerPrefix => "Node";
    internal override bool IsCacheRegistration(string cache, DirectoryInfo entry) => NodeCacheStorage.IsProjectRegistration(cache, entry);
    internal override void Validate(NodeEnvironmentDefinition definition) => NodeEnvironmentDefinitions.Validate(definition);
    internal override string RuntimePath(string generation, NodeEnvironmentDefinition definition) =>
        NodeEnvironmentDefinitions.NodeExecutable(generation, definition);
    internal override ProcessRequest PrepareRequest(ProcessRequest request)
    {
        var environment = request.EnvironmentVariables is null ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) :
            new Dictionary<string, string?>(request.EnvironmentVariables, StringComparer.OrdinalIgnoreCase);
        foreach (var key in environment.Keys.Where(NodeEnvironmentInstaller.IsNodeVariable).ToArray()) environment[key] = null;
        foreach (var value in NodeEnvironmentInstaller.CleanEnvironment()) environment[value.Key] = value.Value;
        return request with { Arguments = ["--no-global-search-paths", .. request.Arguments], EnvironmentVariables = environment };
    }
}
