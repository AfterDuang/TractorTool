namespace TractorTool.Core.Processes;

/// <summary>Platform adapter used by the host, not a plugin API. Creation must contain the process tree atomically.</summary>
public interface IProcessSession : IDisposable
{
    StreamReader StandardOutput { get; }
    StreamReader StandardError { get; }
    Task<int> ExitCode { get; }
    /// <summary>Stop this invocation's entire tree and confirm it is empty, even after the root has exited.</summary>
    Task StopTreeAsync(CancellationToken cancellationToken);
}
