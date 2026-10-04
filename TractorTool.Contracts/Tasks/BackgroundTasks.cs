namespace TractorTool.Contracts.Tasks;

public enum BackgroundTaskState { Queued, Running, Cancelling, Succeeded, Failed, Cancelled, Interrupted }

public sealed record BackgroundTaskRequest(Guid BatchId, string Title);
public sealed record BackgroundTaskProgress(double? Percent, string Message);
public sealed record BackgroundTaskSnapshot(Guid Id, string PluginId, Guid BatchId, string Title,
    BackgroundTaskState State, double? Percent, string Message, string? Result, string? Error,
    DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt);

/// <summary>Bound to a plugin lifetime. Submission persists a summary before returning a runnable handle.</summary>
public interface IBackgroundTaskService
{
    Task<IBackgroundTask> QueueAsync(BackgroundTaskRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IBackgroundTask>> QueueBatchAsync(IReadOnlyList<BackgroundTaskRequest> requests,
        CancellationToken cancellationToken = default);
}

/// <summary>The caller decides execution order. The callback returns only after its output is committed and verified.</summary>
public interface IBackgroundTask : IDisposable
{
    Guid Id { get; }
    void Cancel();
    Task<BackgroundTaskSnapshot> RunAsync(Func<IProgress<BackgroundTaskProgress>, CancellationToken, Task<string?>> execute);
}
