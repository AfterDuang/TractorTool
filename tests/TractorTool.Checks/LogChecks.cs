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
        check("New sessions have distinct files and preserve each other's records", () =>
        {
            var first = new AppLog(DirectoryFor("sessions"));
            var second = new AppLog(DirectoryFor("sessions"));
            first.Write("Test", "first session"); second.Write("Test", "second session");
            Assert(first.FilePath != second.FilePath);
            Assert(File.ReadAllText(first.FilePath).Contains("first session") &&
                !File.ReadAllText(first.FilePath).Contains("second session"));
        });
        check("Age cleanup only removes host logs, preserving other files and subdirectories", () =>
        {
            var dir = DirectoryFor("age");
            var log = new AppLog(dir);
            var expired = Archive(dir, 1, "expired");
            File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-15));
            var recent = Archive(dir, 2, "recent");
            var unrelated = Path.Combine(dir, "user-notes.log");
            var similar = Path.Combine(dir, "host-99999999-999999-1.log");
            File.WriteAllText(unrelated, "keep notes"); File.WriteAllText(similar, "keep unknown format");
            File.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddDays(-30));
            File.SetLastWriteTimeUtc(similar, DateTime.UtcNow.AddDays(-30));
            var sub = Path.Combine(dir, "archive"); Directory.CreateDirectory(sub);
            var nested = Archive(sub, 3, "keep nested");
            File.SetLastWriteTimeUtc(nested, DateTime.UtcNow.AddDays(-30));
            log.Write("Test", "new record");
            Assert(!File.Exists(expired) && File.Exists(recent) && File.Exists(unrelated) &&
                File.Exists(similar) && File.Exists(nested));
        });
        check("File count retains newest history and does not affect error acknowledgement", () =>
        {
            var dir = DirectoryFor("count"); var log = new AppLog(dir);
            var oldest = "";
            for (var i = 0; i < 25; i++)
            {
                var file = Archive(dir, i + 1, "history");
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(i - 25));
                if (i == 0) oldest = file;
            }
            log.Write(AppLogLevel.Error, "Test", "visible error");
            Assert(Directory.GetFiles(dir, "*.log").Length == 20 && !File.Exists(oldest));
            Assert(log.Snapshot().UnreadErrors == 1);
            log.AcknowledgeErrors();
            Assert(log.Snapshot().UnreadErrors == 0 && File.ReadAllText(log.FilePath).Contains("visible error"));
        });
        check("Total disk budget reserves space for the active segment", () =>
        {
            var dir = DirectoryFor("budget"); var log = new AppLog(dir);
            var oldest = "";
            for (var i = 0; i < 12; i++)
            {
                var file = Archive(dir, i + 1, "history");
                using (var stream = File.OpenWrite(file)) stream.SetLength(10 * 1024 * 1024);
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(i - 12));
                if (i == 0) oldest = file;
            }
            log.Write("Test", "within budget");
            Assert(!File.Exists(oldest) && Directory.GetFiles(dir).Sum(f => new FileInfo(f).Length) <= 100L * 1024 * 1024);
            Assert(File.ReadAllText(log.FilePath).Contains("within budget"));
        });
        check("Rotation preserves prior records and caps oversized UTF8 entries", () =>
        {
            var log = new AppLog(DirectoryFor("rotation"));
            log.Write("Test", "previous segment");
            var previous = log.FilePath;
            using (var stream = File.OpenWrite(previous)) stream.SetLength(10 * 1024 * 1024 - 1);
            var message = string.Concat(Enumerable.Repeat("中文😀", 12000));
            log.Write("Test", message);
            Assert(log.FilePath != previous && File.ReadAllText(previous).StartsWith(log.Snapshot().Entries[0].Timestamp.ToString("O")));
            var bytes = File.ReadAllBytes(log.FilePath);
            var text = new System.Text.UTF8Encoding(false, true).GetString(bytes);
            Assert(bytes.Length <= 64 * 1024 && text.Contains("已截断"));
            Assert(log.Snapshot().Entries[^1].Message == message);
            log.Write("Test", "after oversized record");
            Assert(File.ReadAllText(log.FilePath).Contains("after oversized record"));
        });
        if (OperatingSystem.IsWindows()) check("Blocked cleanup warns once, keeps logging, and retries on rotation", () =>
        {
            var dir = DirectoryFor("blocked"); var log = new AppLog(dir);
            var blocked = Archive(dir, 1, "locked history");
            File.SetLastWriteTimeUtc(blocked, DateTime.UtcNow.AddDays(-30));
            using (var held = new FileStream(blocked, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                log.Write("Test", "first"); log.Write("Test", "second");
                Assert(log.Snapshot().Entries.Count(e => e.Level == AppLogLevel.Warning) == 1);
                Assert(log.Snapshot().UnreadErrors == 0 && log.Snapshot().WriteError is null && File.Exists(blocked));
                Assert(File.ReadAllText(log.FilePath).Contains("旧日志清理未完成"));
            }
            using (var stream = File.OpenWrite(log.FilePath)) stream.SetLength(10 * 1024 * 1024);
            log.Write("Test", "cleanup retry");
            Assert(!File.Exists(blocked) && log.Snapshot().WriteError is null);
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
    private static string Archive(string directory, int id, string content)
    {
        var path = Path.Combine(directory, $"host-20260101-120000-{id}.log");
        File.WriteAllText(path, content);
        return path;
    }
    private static void Assert(bool value) { if (!value) throw new Exception("Logging assertion failed."); }
    private sealed class LegacyLog : IAppLog { public void Write(string source, string message, Exception? error = null) { } }
}
