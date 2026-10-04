using TractorTool.Contracts;
using TractorTool.Contracts.Tasks;
using TractorTool.Core.Logging;
using TractorTool.Core.Tasks;

internal static class LogChecks
{
    public static void Run(string root, Action<string, Action> check)
    {
        string DirectoryFor(string name) => Path.Combine(root, "logging", name);
        check("Structured logging retains legacy errors, explicit warning and file detail", () =>
        {
            var log = new AppLog(DirectoryFor("levels"));
            IAppLog api = log;
            api.Write("Test", "ready");
            api.Write(AppLogLevel.Warning, "Test", "limited fallback");
            api.Write("Test", "failed", new IOException("fixture detail"));
            var snapshot = log.Snapshot();
            Assert(snapshot.Entries.Select(e => e.Level).SequenceEqual([AppLogLevel.Info, AppLogLevel.Warning, AppLogLevel.Error]));
            Assert(snapshot.UnreadErrors == 1 && File.ReadAllText(log.FilePath).Contains("fixture detail"));
            IAppLog legacy = new LegacyLog();
            legacy.Write(AppLogLevel.Warning, "Test", "default method keeps legacy provider compatible");
        });
        check("Read clears all current errors without deleting records; later errors alert again", () =>
        {
            var log = new AppLog(DirectoryFor("badge"));
            for (var i = 0; i < 350; i++) log.Write(AppLogLevel.Error, "Test", "error " + i);
            var shown = log.Snapshot();
            Assert(shown.Entries.Count == 300 && shown.UnreadErrors == 350);
            log.Write(AppLogLevel.Error, "Test", "new error");
            log.AcknowledgeErrors();
            Assert(log.Snapshot().UnreadErrors == 0 && log.Snapshot().Entries.Count == 300);
            log.Write(AppLogLevel.Error, "Test", "error after read");
            Assert(log.Snapshot().UnreadErrors == 1);
        });
        check("Faulty display subscribers cannot block logging or later subscribers", () =>
        {
            var log = new AppLog(DirectoryFor("subscribers"));
            var notified = 0;
            log.Changed += () => throw new Exception("broken observer");
            log.Changed += () => notified++;
            log.Write("Test", "record retained");
            Assert(notified == 1 && log.Snapshot().Entries.Count == 1);
        });
        check("Failed log sink reports one error per incident and recovers", () =>
        {
            var log = new AppLog(DirectoryFor("sink"));
            Directory.CreateDirectory(log.FilePath); // synthetic obstruction at the fixture's new log path
            log.Write("Test", "one"); log.Write("Test", "two");
            Assert(log.Snapshot().UnreadErrors == 1 && log.Snapshot().WriteError is not null);
            Directory.Delete(log.FilePath);
            log.Write("Test", "recovered");
            Assert(log.Snapshot().WriteError is null && File.ReadAllText(log.FilePath).Contains("recovered"));
        });
        check("Task progress logs milestones, clean cancellation is INFO, failure is ERROR", () =>
        {
            async Task Run()
            {
                var dir = DirectoryFor("tasks");
                var log = new AppLog(dir);
                var service = new BackgroundTaskService(Path.Combine(dir, "tasks.json"), log);
                using var scope = service.CreateScope("Fixture.Logs");
                using var task = await scope.QueueAsync(new(Guid.NewGuid(), "success"));
                var result = await task.RunAsync((progress, _) =>
                {
                    for (var i = 0; i < 100; i++) progress.Report(new(i, "encoding"));
                    return Task.FromResult<string?>("output");
                });
                Assert(result.State == BackgroundTaskState.Succeeded);
                Assert(log.Snapshot().Entries.Count(e => e.Message.Contains("encoding")) == 10);
                using var cancelled = await scope.QueueAsync(new(Guid.NewGuid(), "cancelled"));
                cancelled.Cancel();
                Assert((await cancelled.RunAsync((_, _) => throw new Exception("must not run"))).State == BackgroundTaskState.Cancelled);
                Assert(log.Snapshot().UnreadErrors == 0);
                using var failed = await scope.QueueAsync(new(Guid.NewGuid(), "failure"));
                Assert((await failed.RunAsync((_, _) => throw new IOException("fixture failure"))).State == BackgroundTaskState.Failed);
                Assert(log.Snapshot().UnreadErrors == 1);
                await service.BeginShutdownAsync();
            }
            Run().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        });
    }
    private static void Assert(bool value) { if (!value) throw new Exception("Logging assertion failed."); }
    private sealed class LegacyLog : IAppLog { public void Write(string source, string message, Exception? error = null) { } }
}
