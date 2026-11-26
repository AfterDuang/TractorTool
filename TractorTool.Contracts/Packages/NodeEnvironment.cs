using TractorTool.Contracts.Processes;

namespace TractorTool.Contracts.Packages;

/// <summary>A plugin-private, locked Node environment. API 1.10.</summary>
public interface INodeEnvironmentLease : IDisposable
{
    string NodePath { get; }
    string Directory { get; }
    /// <summary>Runs only NodePath; reserves the environment through process-tree cleanup and output drain.</summary>
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default);
}
