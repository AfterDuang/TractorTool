using TractorTool.Core.Packages.Environments;

namespace TractorTool.Core.Packages.Node;

// Keep the existing storage entry point and serialized record shape.
internal sealed class NodeEnvironmentStorage : EnvironmentStorage<NodeEnvironmentDefinition, NodeEnvironmentPolicy>;
