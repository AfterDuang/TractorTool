using TractorTool.Contracts.Processes;

namespace TractorTool.Contracts.Packages;

/// <summary>A plugin-private, locked Python environment. API 1.9.</summary>
public interface IPythonEnvironmentLease : IDisposable
{
    string PythonPath { get; }
    string Directory { get; }
    /// <summary>Runs only PythonPath; the environment remains reserved until process-tree cleanup and output drain finish.</summary>
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default);
}
