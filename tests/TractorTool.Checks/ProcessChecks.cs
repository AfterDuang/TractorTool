using System.Diagnostics;
using System.Text.Json;
using TractorTool.Contracts;
using TractorTool.Contracts.Processes;
using TractorTool.Core.Processes;
using TractorTool.Platform;

internal static class ProcessChecks
{
    public static void Run(string root, Action<string, Action> check)
    {
        var directory = Path.Combine(root, "process fixtures 中文");
        Directory.CreateDirectory(directory);
        void Case(string name, Func<Task> run) => check(name, () => run().GetAwaiter().GetResult());
        Case("Stdout callback receives data before exit and beyond capture limit", async () =>
        {
            var session = new FakeSession { StandardOutput = new(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("一段实时输出"))) };
            var received = "";
            await using var runner = new ProcessRunner("Test", new TestLog(), _ => session);
            var result = await runner.RunAsync(Request("echo") with
            {
                OutputLimit = 1,
                StandardOutputReceived = chunk => { Assert(!session.Exit.Task.IsCompleted); received += chunk; session.Exit.TrySetResult(0); }
            });
            Assert(received == "一段实时输出" && result.StandardOutput == "一" && result.StandardOutputTruncated && session.Disposed);
        });
        Case("Stdout callback failure stops and disposes the active process", async () =>
        {
            var session = new FakeSession { StandardOutput = new(new MemoryStream([65])) };
            await using var runner = new ProcessRunner("Test", new TestLog(), _ => session);
            await Throws<IOException>(() => runner.RunAsync(Request("echo") with
                { StandardOutputReceived = _ => throw new InvalidDataException("fixture consumer failure") }));
            Assert(session.StopCalls == 1 && session.Disposed);
        });
        Case("Internal cleanup cancellation cannot masquerade as caller cancellation", async () =>
        {
            var session = new FakeSession();
            session.Exit.TrySetResult(0);
            session.Stop = call => call == 1 ? Task.FromException(new OperationCanceledException("fixture cleanup timeout")) : Task.CompletedTask;
            await using var runner = new ProcessRunner("Test", new TestLog(), _ => session);
            var run = runner.RunAsync(Request("echo"));
            await Throws<IOException>(() => run);
            Assert(run.IsFaulted && !run.IsCanceled && session.StopCalls == 2 && session.Disposed);
        });
        Case("Normal cancellation drains closed pipes without cancelling readers or logging an error", async () =>
        {
            var reader = new EofReader();
            var session = new FakeSession { StandardOutput = reader, Stop = _ => { reader.End.TrySetResult(0); return Task.CompletedTask; } };
            var log = new TestLog();
            await using var runner = new ProcessRunner("Test", log, _ => session);
            using var cancellation = new CancellationTokenSource();
            var run = runner.RunAsync(Request("wait"), cancellation.Token);
            await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Throws<OperationCanceledException>(() => run);
            Assert(run.IsCanceled && session.StopCalls == 1 && session.Disposed);
            Assert(!reader.Cancelled && log.Errors.Count == 0);
        });
        Case("Read failure stops the process and disposes its streams", async () =>
        {
            var session = new FakeSession { StandardOutput = new FailingReader() };
            await using var runner = new ProcessRunner("Test", new TestLog(), _ => session);
            await Throws<IOException>(() => runner.RunAsync(Request("echo")));
            Assert(session.StopCalls == 1 && session.Disposed);
        });
        Case("Unconfirmed process cleanup is faulted and logged rather than reported successful", async () =>
        {
            var session = new FakeSession { Stop = _ => Task.FromException(new IOException("fixture cannot stop")) };
            session.Exit.TrySetResult(0);
            var log = new TestLog();
            await using var runner = new ProcessRunner("Test", log, _ => session);
            await Throws<IOException>(() => runner.RunAsync(Request("echo")));
            Assert(log.Errors.Count == 1 && session.Disposed);
        });
        Case("Shutdown covers an invocation whose platform creation is still in progress", async () =>
        {
            var session = new FakeSession();
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            var runner = new ProcessRunner("Test", new TestLog(), _ =>
            {
                entered.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Fixture setup was not released");
                return session;
            });
            var run = runner.RunAsync(Request("echo"));
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var stopping = runner.DisposeAsync().AsTask();
                Assert(!stopping.IsCompleted);
                release.Set();
                await stopping.WaitAsync(TimeSpan.FromSeconds(5));
                await Throws<OperationCanceledException>(() => run);
                Assert(session.StopCalls == 1 && session.Disposed);
            }
            finally { release.Set(); await runner.DisposeAsync(); }
        });
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("SKIP Windows process adapter checks on this platform"); return; }
        Case("Process arguments, Unicode and working directory roundtrip without shell expansion", async () =>
        {
            await using var runner = NewRunner();
            string[] values = ["", "空 格", "a\"b", @"C:\ending\", "back\\\\\"quote", "line1\nline2", "%PATH%", "$(echo unsafe)", "&|<>;"];
            var result = await runner.RunAsync(Request("echo", values) with { WorkingDirectory = directory });
            var echo = JsonSerializer.Deserialize<Echo>(result.StandardOutput)!;
            Assert(result.ExitCode == 0 && echo.Arguments.SequenceEqual(values));
            Assert(Path.GetFullPath(echo.Directory) == directory);
        });
        Case("Process drains both pipes past bounded capture without a line terminator", async () =>
        {
            await using var runner = NewRunner();
            var result = await runner.RunAsync(Request("flood") with { OutputLimit = 127 });
            Assert(result.ExitCode == 0 && result.StandardOutput == new string('o', 127));
            Assert(result.StandardError == new string('e', 127) && result.StandardOutputTruncated && result.StandardErrorTruncated);
            var discarded = await runner.RunAsync(Request("flood") with { OutputLimit = 0 });
            Assert(discarded.StandardOutput.Length == 0 && discarded.StandardError.Length == 0 && discarded.StandardOutputTruncated);
        });
        Case("Process nonzero exit and stderr are preserved as a result", async () =>
        {
            await using var runner = NewRunner();
            var result = await runner.RunAsync(Request("exit"));
            Assert(result.ExitCode == 23 && result.StandardError == "fixture failure");
        });
        Case("Process rejects relative paths, malformed parameters and invalid limits before starting", async () =>
        {
            await using var runner = NewRunner();
            await Throws<ArgumentException>(() => runner.RunAsync(new("dotnet", [])));
            await Throws<ArgumentException>(() => runner.RunAsync(Request("echo") with { WorkingDirectory = "relative" }));
            await Throws<ArgumentException>(() => runner.RunAsync(Request("echo", ["bad\0arg"])));
            await Throws<ArgumentOutOfRangeException>(() => runner.RunAsync(Request("echo") with { OutputLimit = -1 }));
            await Throws<ArgumentOutOfRangeException>(() => runner.RunAsync(Request("echo") with { Timeout = TimeSpan.Zero }));
        });
        Case("Process start failure does not prevent a subsequent valid run", async () =>
        {
            await using var runner = NewRunner();
            await Throws<System.ComponentModel.Win32Exception>(() => runner.RunAsync(new(Path.Combine(directory, "missing.exe"), [])));
            Assert((await runner.RunAsync(Request("echo"))).ExitCode == 0);
        });
        Case("Process pre-cancellation starts no child", async () =>
        {
            await using var runner = NewRunner();
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            var marker = Path.Combine(directory, "pre-cancel.pid");
            await Throws<OperationCanceledException>(() => runner.RunAsync(Request("wait", [marker]), cancel.Token));
            Assert(!File.Exists(marker));
        });
        Case("Process cancellation waits for owned parent exit", async () =>
        {
            await using var runner = NewRunner();
            using var cancel = new CancellationTokenSource();
            var marker = Path.Combine(directory, "cancel.pid");
            var run = runner.RunAsync(Request("wait", [marker]), cancel.Token);
            var pid = await ReadPidAsync(marker);
            cancel.Cancel();
            await Throws<OperationCanceledException>(() => run);
            AssertExited(pid);
        });
        Case("Process timeout differs from caller cancellation", async () =>
        {
            await using var runner = NewRunner();
            var marker = Path.Combine(directory, "timeout.pid");
            var run = runner.RunAsync(Request("wait", [marker]) with { Timeout = TimeSpan.FromSeconds(4) });
            var pid = await ReadPidAsync(marker);
            await Throws<TimeoutException>(() => run);
            AssertExited(pid);
        });
        Case("Process runner shutdown cancels all active work and rejects later starts", async () =>
        {
            await using var runner = NewRunner();
            var one = Path.Combine(directory, "shutdown-one.pid");
            var two = Path.Combine(directory, "shutdown-two.pid");
            var first = runner.RunAsync(Request("wait", [one]));
            var second = runner.RunAsync(Request("wait", [two]));
            var firstPid = await ReadPidAsync(one);
            var secondPid = await ReadPidAsync(two);
            await runner.DisposeAsync();
            await Throws<OperationCanceledException>(() => first);
            await Throws<OperationCanceledException>(() => second);
            AssertExited(firstPid); AssertExited(secondPid);
            await Throws<ObjectDisposedException>(() => runner.RunAsync(Request("echo")));
        });
        Case("Process arguments are snapshotted before caller mutation", async () =>
        {
            await using var runner = NewRunner();
            var request = Request("echo", ["original"]);
            var arguments = request.Arguments.ToList();
            var run = runner.RunAsync(request with { Arguments = arguments });
            arguments[^1] = "changed";
            Assert(JsonSerializer.Deserialize<Echo>((await run).StandardOutput)!.Arguments.Single() == "original");
        });
        Case("Process stdin is EOF rather than an inherited interactive console", async () =>
        {
            await using var runner = NewRunner();
            Assert((await runner.RunAsync(Request("stdin"))).StandardOutput == "eof");
        });
        Case("Process tree is reclaimed when its root exits before the child", async () =>
        {
            await using var runner = NewRunner();
            var child = Path.Combine(directory, "orphan-child.pid");
            var result = await runner.RunAsync(Request("orphan", [child]));
            Assert(result.ExitCode == 0);
            AssertExited(await ReadPidAsync(child));
        });
        Case("Native tree stop confirms descendants and permits repeated stop after teardown", async () =>
        {
            var child = Path.Combine(directory, "native-tree-child.pid");
            using var session = WindowsProcessSession.Start(Request("tree", [child]));
            var pid = await ReadPidAsync(child);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await session.StopTreeAsync(deadline.Token);
            AssertExited(pid);
            await session.ExitCode.WaitAsync(TimeSpan.FromSeconds(3));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await session.StopTreeAsync(cancelled.Token); // Already empty: no notification or fresh wait is needed.
        });
        Case("Cancelling one invocation stops its descendants without stopping another invocation", async () =>
        {
            await using var runner = NewRunner();
            using var cancel = new CancellationTokenSource();
            var child = Path.Combine(directory, "tree-child.pid");
            var other = Path.Combine(directory, "unrelated.pid");
            var tree = runner.RunAsync(Request("tree", [child]), cancel.Token);
            using var otherCancellation = new CancellationTokenSource();
            var independent = runner.RunAsync(Request("wait", [other]), otherCancellation.Token);
            var childPid = await ReadPidAsync(child);
            var otherPid = await ReadPidAsync(other);
            cancel.Cancel();
            await Throws<OperationCanceledException>(() => tree);
            AssertExited(childPid);
            using (var running = Process.GetProcessById(otherPid)) Assert(!running.HasExited);
            otherCancellation.Cancel();
            await Throws<OperationCanceledException>(() => independent);
            AssertExited(otherPid);
        });
    }

    // Only reachable with an explicit child fixture switch, before the normal suite touches disk.
    public static async Task<int> RunFixtureAsync(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        switch (args[0])
        {
            case "echo":
                Console.Write(JsonSerializer.Serialize(new Echo(args[1..], Environment.CurrentDirectory)));
                return 0;
            case "flood":
                for (var i = 0; i < 128; i++)
                {
                    await Console.Out.WriteAsync(new string('o', 4096));
                    await Console.Error.WriteAsync(new string('e', 4096));
                }
                return 0;
            case "exit": Console.Error.Write("fixture failure"); return 23;
            case "stdin":
                Console.Write((await Console.In.ReadToEndAsync()).Length == 0 ? "eof" : "unexpected input");
                return 0;
            case "orphan":
            case "tree":
                var request = Request("wait", [args[1]]);
                var start = new ProcessStartInfo(request.ExecutablePath) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var argument in request.Arguments) start.ArgumentList.Add(argument);
                using (var child = Process.Start(start) ?? throw new IOException("Child fixture failed to start"))
                {
                    await ReadPidAsync(args[1]);
                    if (args[0] == "tree") await Task.Delay(Timeout.InfiniteTimeSpan);
                }
                return 0;
            case "wait":
                await File.WriteAllTextAsync(args[1], Environment.ProcessId.ToString());
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return 0;
            default: return 64;
        }
    }

    private static ProcessRequest Request(string mode, string[]? arguments = null)
    {
        var executable = Environment.ProcessPath!;
        List<string> args = [];
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            args.Add(typeof(ProcessChecks).Assembly.Location);
        args.Add("--process-fixture"); args.Add(mode); args.AddRange(arguments ?? []);
        return new(executable, args) { Timeout = TimeSpan.FromSeconds(15) };
    }

    private static async Task<int> ReadPidAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (File.Exists(path) && int.TryParse(await File.ReadAllTextAsync(path, timeout.Token), out var pid)) return pid;
            await Task.Delay(20, timeout.Token);
        }
    }

    private static void AssertExited(int pid)
    {
        try { using var process = Process.GetProcessById(pid); Assert(process.HasExited); }
        catch (ArgumentException) { /* Already reaped by the OS. */ }
    }
    private static async Task Throws<T>(Func<Task> run) where T : Exception
    {
        try { await run(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static ProcessRunner NewRunner() => new("ProcessChecks", new TestLog(), WindowsProcessSession.Start);
    private sealed class TestLog : IAppLog
    {
        public List<Exception> Errors { get; } = [];
        public void Write(string source, string message, Exception? error = null) { if (error is not null) Errors.Add(error); }
    }
    private sealed class FakeSession : IProcessSession
    {
        public StreamReader StandardOutput { get; init; } = new(new MemoryStream());
        public StreamReader StandardError { get; } = new(new MemoryStream());
        public TaskCompletionSource<int> Exit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<int> ExitCode => Exit.Task;
        public Func<int, Task>? Stop { get; set; }
        public int StopCalls { get; private set; }
        public bool Disposed { get; private set; }
        public Task StopTreeAsync(CancellationToken cancellationToken)
        {
            StopCalls++;
            Exit.TrySetResult(0);
            return Stop?.Invoke(StopCalls) ?? Task.CompletedTask;
        }
        public void Dispose() { Disposed = true; StandardOutput.Dispose(); StandardError.Dispose(); }
    }
    private sealed class FailingReader() : StreamReader(new MemoryStream())
    {
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("fixture read failure"));
    }
    private sealed class EofReader() : StreamReader(new MemoryStream())
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<int> End { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _registration;
        public bool Cancelled { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            _registration = cancellationToken.Register(() => { Cancelled = true; End.TrySetCanceled(cancellationToken); });
            Started.TrySetResult();
            return new(End.Task);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _registration.Dispose();
            base.Dispose(disposing);
        }
    }
    private sealed record Echo(string[] Arguments, string Directory);
}
