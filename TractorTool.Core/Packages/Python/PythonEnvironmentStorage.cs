using TractorTool.Core.Packages.Environments;

namespace TractorTool.Core.Packages.Python;

// Keep the existing storage entry point and serialized record shape.
internal sealed class PythonEnvironmentStorage : EnvironmentStorage<PythonEnvironmentDefinition, PythonEnvironmentPolicy>;
