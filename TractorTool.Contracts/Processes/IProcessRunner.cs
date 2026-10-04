namespace TractorTool.Contracts.Processes;

/// <summary>Host-owned execution for trusted tools; scoped to the plugin lifetime, not a page.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default);
}

/// <summary>No shell or PATH lookup. Arguments are individual values, never a command line.</summary>
public sealed record ProcessRequest(string ExecutablePath, IReadOnlyList<string> Arguments)
{
    public string? WorkingDirectory { get; init; }
    public TimeSpan? Timeout { get; init; }
    /// <summary>Maximum retained UTF-16 characters per stream. Excess output is drained and discarded.</summary>
    public int OutputLimit { get; init; } = 1024 * 1024;
}

/// <summary>A nonzero exit code is returned unchanged. Cancellation and timeout throw after cleanup.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError,
    bool StandardOutputTruncated, bool StandardErrorTruncated);
