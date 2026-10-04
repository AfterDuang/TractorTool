using System.Collections.Concurrent;
using TractorTool.Contracts;
using TractorTool.Contracts.Tasks;
using TractorTool.Core.Storage;
using TractorTool.Core.Tasks;

internal static class TaskChecks
{
    public static void Run(string root, Action<string, Action> check)
    {
        var directory = Path.Combine(root, "finite-tasks");
        Directory.CreateDirectory(directory);
        void Case(string name, Func<Task> run) => check(name, () => run().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult());
        string FileFor(string name) => Path.Combine(directory, name + ".json");
        BackgroundTaskRequest Request(string title = "fixture") => new(Guid.NewGuid(), title);

        Case("Finite task submission is durable; the caller controls execution order", async () =>
        {
            var path = FileFor("serial");
            var service = new BackgroundTaskService(path, new TestLog());
            using var scope = service.CreateScope("Test.Serial");
            using var first = await scope.QueueAsync(Request("first"));
            using var second = await scope.QueueAsync(Request("second"));
            Assert(new JsonFileStore<TaskHistory>(path).Load().Tasks.Length == 2);
            var calls = new List<string>();
            var started = Signal(); var release = Signal();
            var running = first.RunAsync(async (progress, token) =>
            {
                calls.Add("first"); progress.Report(new(25, "working")); started.SetResult();
                await release.Task.WaitAsync(token); return "committed-first";
            });
            await started.Task;
            Assert(service.Snapshot.Single(s => s.Id == second.Id).State == BackgroundTaskState.Queued && calls.Count == 1);
            release.SetResult();
            Assert((await running).State == BackgroundTaskState.Succeeded);
            Assert((await second.RunAsync((_, _) => { calls.Add("second"); return Task.FromResult<string?>("committed-second"); })).State == BackgroundTaskState.Succeeded);
            Assert(calls.SequenceEqual(new[] { "first", "second" }));
            await service.BeginShutdownAsync();
        });

        Case("Cancelling a queued finite task never invokes its callback", async () =>
        {
            var service = new BackgroundTaskService(FileFor("waiting"), new TestLog());
            using var scope = service.CreateScope("Test.Waiting");
            using var task = await scope.QueueAsync(Request());
            task.Cancel();
            var result = await task.RunAsync((_, _) => throw new Exception("must not execute"));
            Assert(result.State == BackgroundTaskState.Cancelled && !service.HasActive);
            await service.BeginShutdownAsync();
        });

        Case("A batch is visible atomically and cancelling it prevents every queued callback", async () =>
        {
            var service = new BackgroundTaskService(FileFor("batch"), new TestLog());
            using var scope = service.CreateScope("Test.Batch");
            var batch = Guid.NewGuid();
            var partial = false;
            service.Changed += (_, _) =>
            {
                var count = service.Snapshot.Count(s => s.BatchId == batch);
                if (count is > 0 and < 3) partial = true;
            };
            var tasks = await scope.QueueBatchAsync([new(batch, "one"), new(batch, "two"), new(batch, "three")]);
            Assert(!partial && service.Snapshot.Count(s => s.BatchId == batch) == 3);
            foreach (var item in service.Snapshot.Where(s => s.BatchId == batch)) service.Cancel(item.Id);
            foreach (var task in tasks)
            {
                using (task) Assert((await task.RunAsync((_, _) => throw new Exception("must not execute"))).State == BackgroundTaskState.Cancelled);
            }
            await Throws<ArgumentException>(() => scope.QueueBatchAsync([]));
            await service.BeginShutdownAsync();
        });

        Case("A callback's normal return preserves committed output when cancellation arrives late", async () =>
        {
            var service = new BackgroundTaskService(FileFor("late-cancellation"), new TestLog());
            using var scope = service.CreateScope("Test.Committed");
            using var task = await scope.QueueAsync(Request());
            var committed = Signal(); var returned = Signal();
            var running = task.RunAsync(async (_, _) => { committed.SetResult(); await returned.Task; return "committed-output.mp4"; });
            await committed.Task;
            task.Cancel();
            returned.SetResult();
            var result = await running;
            Assert(result.State == BackgroundTaskState.Succeeded && result.Result == "committed-output.mp4");
            await service.BeginShutdownAsync();
        });

        Case("Running cancellation waits for callback cleanup and observes throwing cancellation callbacks", async () =>
        {
            var log = new TestLog();
            var service = new BackgroundTaskService(FileFor("running-cancel"), log);
            using var scope = service.CreateScope("Test.Cancellation");
            using var task = await scope.QueueAsync(Request());
            var started = Signal(); var cancellation = Signal(); var cleanup = Signal();
            var running = task.RunAsync(async (_, token) =>
            {
                using var registration = token.Register(() => { cancellation.TrySetResult(); throw new IOException("callback fixture"); });
                started.SetResult(); await cancellation.Task; await cleanup.Task;
                token.ThrowIfCancellationRequested(); return null;
            });
            await started.Task;
            task.Dispose();
            await cancellation.Task;
            var stopping = scope.BeginShutdownAsync();
            Assert(!stopping.IsCompleted && service.Snapshot.Single().State == BackgroundTaskState.Cancelling);
            cleanup.SetResult();
            Assert((await running).State == BackgroundTaskState.Cancelled);
            await stopping;
            Assert(log.Errors.Any(e => e.Contains("任务取消回调失败")));
            await service.BeginShutdownAsync();
        });

        Case("A failed finite task returns a terminal error without blocking the next task", async () =>
        {
            var service = new BackgroundTaskService(FileFor("failure"), new TestLog());
            using var scope = service.CreateScope("Test.Failure");
            using var bad = await scope.QueueAsync(Request());
            var result = await bad.RunAsync((_, _) => Task.FromException<string?>(new IOException("fixture failure")));
            Assert(result.State == BackgroundTaskState.Failed && result.Error == "fixture failure");
            using var good = await scope.QueueAsync(Request());
            Assert((await good.RunAsync((_, _) => Task.FromResult<string?>("valid"))).State == BackgroundTaskState.Succeeded);
            await service.BeginShutdownAsync();
        });

        Case("Concurrent shutdown seals submission and waits for active work", async () =>
        {
            var service = new BackgroundTaskService(FileFor("shutdown"), new TestLog());
            using var scope = service.CreateScope("Test.Shutdown");
            using var task = await scope.QueueAsync(Request());
            var started = Signal(); var cleanup = Signal();
            var running = task.RunAsync(async (_, token) => { started.SetResult(); await cleanup.Task; token.ThrowIfCancellationRequested(); return null; });
            await started.Task;
            var shutdown = service.BeginShutdownAsync();
            Assert(ReferenceEquals(shutdown, service.BeginShutdownAsync()) && !shutdown.IsCompleted);
            await Throws<ObjectDisposedException>(() => scope.QueueAsync(Request()));
            cleanup.SetResult();
            await shutdown;
            Assert((await running).State == BackgroundTaskState.Cancelled && !service.HasActive);
        });

        Case("Stopping one scope cannot cancel another owner, including a same-ID scope", async () =>
        {
            var service = new BackgroundTaskService(FileFor("owners"), new TestLog());
            using var first = service.CreateScope("Test.Owner");
            using var second = service.CreateScope("Test.Owner");
            using var a = await first.QueueAsync(Request());
            using var b = await second.QueueAsync(Request());
            await first.BeginShutdownAsync();
            Assert(service.Snapshot.Single(s => s.Id == a.Id).State == BackgroundTaskState.Cancelled);
            Assert(service.Snapshot.Single(s => s.Id == b.Id).State == BackgroundTaskState.Queued);
            Assert((await b.RunAsync((_, _) => Task.FromResult<string?>(null))).State == BackgroundTaskState.Succeeded);
            await Throws<ObjectDisposedException>(() => first.QueueAsync(Request()));
            await service.BeginShutdownAsync();
        });

        Case("Startup marks unfinished summaries interrupted without invoking plugin code", async () =>
        {
            var path = FileFor("restart");
            var original = new BackgroundTaskSnapshot(Guid.NewGuid(), "Test.Restart", Guid.NewGuid(), "unfinished",
                BackgroundTaskState.Running, 45, "old progress", null, null, DateTimeOffset.UtcNow, null);
            new JsonFileStore<TaskHistory>(path).Save(new() { Tasks = [original] });
            var service = new BackgroundTaskService(path, new TestLog());
            await service.FlushAsync();
            var restored = service.Snapshot.Single();
            Assert(restored.Id == original.Id && restored.State == BackgroundTaskState.Interrupted && restored.FinishedAt is not null && !service.HasActive);
            Assert(new JsonFileStore<TaskHistory>(path).Load().Tasks.Single().State == BackgroundTaskState.Interrupted);
            await service.BeginShutdownAsync();
        });

        Case("Invalid history is preserved and blocks submission until repaired", async () =>
        {
            var path = FileFor("corrupt");
            System.IO.File.WriteAllText(path, "{broken fixture");
            var service = new BackgroundTaskService(path, new TestLog());
            using var scope = service.CreateScope("Test.Corrupt");
            await Throws<System.Text.Json.JsonException>(() => scope.QueueAsync(Request()));
            Assert(service.PersistenceError is not null && service.Snapshot.Count == 0 && System.IO.File.ReadAllText(path) == "{broken fixture");
            // Only the synthetic corrupt fixture is repaired by this check.
            System.IO.File.Delete(path);
            using var task = await scope.QueueAsync(Request());
            Assert(service.PersistenceError is null);
            task.Cancel();
            await service.BeginShutdownAsync();
        });

        Case("Write failure never returns an executable unrecorded task and can be retried", async () =>
        {
            var path = FileFor("write-failure");
            Directory.CreateDirectory(path);
            var service = new BackgroundTaskService(path, new TestLog());
            using var scope = service.CreateScope("Test.WriteFailure");
            await Throws<IOException>(() => scope.QueueAsync(Request()));
            Assert(service.Snapshot.Count == 0 && service.PersistenceError is not null);
            Directory.Delete(path); // Empty directory created by this fixture.
            using var task = await scope.QueueAsync(Request());
            Assert((await task.RunAsync((_, _) => Task.FromResult<string?>("result"))).State == BackgroundTaskState.Succeeded);
            await service.BeginShutdownAsync();
        });

        Case("Progress is coalesced in memory and subscriber failures are isolated", async () =>
        {
            var path = FileFor("progress");
            var service = new BackgroundTaskService(path, new TestLog());
            service.Changed += (_, _) => throw new IOException("subscriber fixture");
            using var scope = service.CreateScope("Test.Progress");
            using var task = await scope.QueueAsync(Request());
            var result = await task.RunAsync((progress, _) =>
            {
                var persisted = System.IO.File.ReadAllText(path);
                for (var i = 0; i < 200; i++) progress.Report(new(i, "progress"));
                Assert(System.IO.File.ReadAllText(path) == persisted);
                Assert(service.Snapshot.Single().Percent == 100);
                return Task.FromResult<string?>("committed");
            });
            Assert(result.State == BackgroundTaskState.Succeeded);
            await service.BeginShutdownAsync();
        });

        Case("Task history retains all active summaries and only the latest 100 terminal ones", async () =>
        {
            var path = FileFor("bounded");
            var service = new BackgroundTaskService(path, new TestLog());
            using var scope = service.CreateScope("Test.Bounded");
            using var waiting = await scope.QueueAsync(Request("still queued"));
            for (var i = 0; i < 102; i++)
            {
                using var task = await scope.QueueAsync(Request("finished " + i));
                await task.RunAsync((_, _) => Task.FromResult<string?>(null));
            }
            Assert(service.Snapshot.Count == 101 && new JsonFileStore<TaskHistory>(path).Load().Tasks.Length == 101);
            Assert(service.Snapshot.Any(s => s.Id == waiting.Id && s.State == BackgroundTaskState.Queued));
            await service.BeginShutdownAsync();
            Assert(service.Snapshot.Count == 100);
        });
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Assert(bool value) { if (!value) throw new InvalidOperationException("Assertion failed"); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private sealed class TestLog : IAppLog
    {
        public ConcurrentQueue<string> Errors { get; } = new();
        public void Write(string source, string message, Exception? error = null) { if (error is not null) Errors.Enqueue(message); }
    }
}
