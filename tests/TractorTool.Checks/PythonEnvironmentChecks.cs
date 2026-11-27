using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TractorTool.Contracts;
using TractorTool.Contracts.Packages;
using TractorTool.Contracts.Processes;
using TractorTool.Core.Packages.Python;
using TractorTool.Core.Storage;

// Synthetic owned files exercise public service lifetimes. No interpreter, uv or downloaded package is executed.
internal static class PythonEnvironmentChecks
{
    public static void Run(string root, Action<string, Action> check)
    {
        void Test(string name, Func<string, Task> action) => check("Python environments: " + name, () =>
            action(Path.Combine(root, "python-environments-" + Guid.NewGuid().ToString("N"))).GetAwaiter().GetResult());

        Test("installed environment reuses offline with no source or consent access", async path =>
        {
            var fixture = WriteFixture(path);
            using var handler = new NoNetwork(); using var http = new HttpClient(handler);
            var process = new NoProcesses(); var confirmations = 0; var sourceQueries = 0;
            await using var service = new PythonEnvironmentService(fixture.PackageRoot, "win-x64", http, process, new Log(),
                _ => { sourceQueries++; return false; });
            service.UpdateReferences([fixture.Definition], true, "");
            service.ConfirmInstallAsync = (_, _) => { confirmations++; return Task.FromResult(false); };
            using var lease = await service.TryRequestAsync(fixture.Definition, "Fixture.Plugin");
            Require(lease is not null && lease.PythonPath == Path.Combine(fixture.Generation!, "environment", "Scripts", "python.exe"));
            Require(handler.Calls == 0 && process.Calls == 0 && confirmations == 0 && sourceQueries == 0);
            Require(service.GetEnvironments().Single().Phase == PackagePhase.Ready);
        });

        foreach (var changed in new[] { false, true })
        Test("locked installed environment retries validation " + (changed ? "and still rejects changed bytes" : "without reinstalling"), async path =>
        {
            var fixture = WriteFixture(path);
            var payload = Path.Combine(fixture.Generation!, "environment", "Scripts", "python.exe");
            var recordPath = Path.Combine(fixture.IdentityDirectory, "environment.json");
            var originalRecord = File.ReadAllText(recordPath);
            using var handler = new NoNetwork(); using var http = new HttpClient(handler);
            var runner = new NoProcesses(); var confirmations = 0;
            await using var service = new PythonEnvironmentService(fixture.PackageRoot, "win-x64", http, runner, new Log(),
                _ => throw new Exception("Existing generation queried a source."));
            service.ConfirmInstallAsync = (prompt, _) =>
            {
                Require(prompt.IsRepair); confirmations++; return Task.FromResult(false);
            };
            using (var held = new FileStream(payload, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await Throws<IOException>(() => service.TryRequestAsync(fixture.Definition, "Fixture.Plugin"));
                await Throws<IOException>(() => service.TryRequestAsync(fixture.Definition, "Fixture.Plugin"));
                var failed = service.GetEnvironments().Single();
                Require(failed.Phase == PackagePhase.Failed && failed.Error.Length != 0 && failed.Users.Count == 0);
                Require(confirmations == 0);
            }
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                await Throws<OperationCanceledException>(() => service.TryRequestAsync(fixture.Definition, "Fixture.Plugin", cancellation.Token));
            }
            if (changed)
            {
                var timestamp = File.GetLastWriteTimeUtc(payload);
                Write(payload, "X" + File.ReadAllText(payload)[1..]); File.SetLastWriteTimeUtc(payload, timestamp);
                await Throws<InvalidDataException>(() => service.TryRequestAsync(fixture.Definition, "Fixture.Plugin"));
                Require(await service.TryRequestAsync(fixture.Definition, "Fixture.Plugin") is null && confirmations == 1);
                Require(service.GetEnvironments().Single().Phase == PackagePhase.Failed);
            }
            else
            {
                using var lease = await service.TryRequestAsync(fixture.Definition, "Fixture.Plugin");
                Require(lease?.PythonPath == payload && confirmations == 0);
                var recovered = service.GetEnvironments().Single();
                Require(recovered.Phase == PackagePhase.Ready && recovered.Error.Length == 0);
            }
            Require(handler.Calls == 0 && runner.Calls == 0 && File.ReadAllText(recordPath) == originalRecord);
        });

        Test("last request canceled during source lookup never starts a download", async path =>
        {
            var fixture = WriteFixture(path, ready: false);
            using var handler = new NoNetwork(); using var http = new HttpClient(handler);
            using var cancellation = new CancellationTokenSource();
            var runner = new NoProcesses();
            var sourceEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var preparationFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var service = new PythonEnvironmentService(fixture.PackageRoot, "win-x64", http, runner, new Log(), _ =>
            {
                sourceEntered.TrySetResult();
                releaseSource.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                return true;
            });
            service.ConfirmInstallAsync = (_, _) => Task.FromResult(true);
            service.Changed += () =>
            {
                if (service.GetEnvironments().Single().CanInstall) preparationFinished.TrySetResult();
            };
            var request = service.TryRequestAsync(fixture.Definition, "Fixture.Plugin", cancellation.Token);
            try
            {
                await sourceEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancellation.Cancel();
                await Throws<OperationCanceledException>(() => request);
                // Let the preparation finish naturally; shutting down here would mask a late-download bug.
                releaseSource.TrySetResult();
                await preparationFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Require(handler.Calls == 0 && runner.Calls == 0);
            }
            finally { cancellation.Cancel(); releaseSource.TrySetResult(); await Observe(request); }
        });

        foreach (var faultOnRetirement in new[] { false, true })
        Test("a later caller starts fresh after canceled consent " + (faultOnRetirement ? "faults" : "retires"), async path =>
        {
            var fixture = WriteFixture(path, ready: false);
            using var http = new HttpClient(new NoNetwork());
            using var cancellation = new CancellationTokenSource();
            var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var retire = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondConsent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var confirmations = 0;
            await using var service = Create(fixture, http, new NoProcesses());
            service.ConfirmInstallAsync = async (_, token) =>
            {
                if (Interlocked.Increment(ref confirmations) != 1) { secondConsent.TrySetResult(); return false; }
                using var registration = token.Register(() => canceled.TrySetResult());
                entered.TrySetResult(token);
                await retire.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (faultOnRetirement) throw new IOException("retiring consent failed");
                token.ThrowIfCancellationRequested();
                return true;
            };
            var first = service.TryRequestAsync(fixture.Definition, "first", cancellation.Token);
            Task<IPythonEnvironmentLease?>? next = null;
            try
            {
                var confirmationToken = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancellation.Cancel();
                await Throws<OperationCanceledException>(() => first);
                await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Require(confirmationToken.IsCancellationRequested);
                next = service.TryRequestAsync(fixture.Definition, "next");
                Require(!next.IsCompleted && confirmations == 1);
                retire.TrySetResult();
                await secondConsent.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Require(await next is null && confirmations == 2 &&
                    (!faultOnRetirement || service.GetEnvironments().Single().Error == "retiring consent failed"));
            }
            finally { cancellation.Cancel(); retire.TrySetResult(); await Observe(first); if (next is not null) await Observe(next); }
        });

        Test("one caller canceling preserves consent for another waiter", async path =>
        {
            var fixture = WriteFixture(path, ready: false);
            using var http = new HttpClient(new NoNetwork()); using var cancellation = new CancellationTokenSource();
            var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            var decide = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var confirmations = 0;
            await using var service = Create(fixture, http, new NoProcesses());
            service.ConfirmInstallAsync = (_, token) =>
            { Interlocked.Increment(ref confirmations); entered.TrySetResult(token); return decide.Task.WaitAsync(token); };
            var first = service.TryRequestAsync(fixture.Definition, "first", cancellation.Token);
            var token = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = service.TryRequestAsync(fixture.Definition, "second");
            try
            {
                cancellation.Cancel(); await Throws<OperationCanceledException>(() => first);
                Require(!token.IsCancellationRequested && confirmations == 1 && !second.IsCompleted);
                decide.TrySetResult(false); Require(await second is null);
            }
            finally { cancellation.Cancel(); decide.TrySetResult(false); await Observe(first); await Observe(second); }
        });

        Test("plugin cancellation does not own a settings installation or ask for consent again", async path =>
        {
            var fixture = WriteFixture(path, ready: false);
            using var handler = new BlockingDownload(); using var http = new HttpClient(handler);
            using var cancellation = new CancellationTokenSource();
            var runner = new NoProcesses(); var confirmations = 0;
            await using var service = Create(fixture, http, runner);
            service.ConfirmInstallAsync = (_, _) => { Interlocked.Increment(ref confirmations); return Task.FromResult(false); };
            var installation = service.InstallAsync(fixture.Definition.Identity);
            Task<IPythonEnvironmentLease?>? request = null;
            try
            {
                var downloadToken = await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                request = service.TryRequestAsync(fixture.Definition, "Fixture.Plugin", cancellation.Token);
                Require(!request.IsCompleted && confirmations == 0);
                cancellation.Cancel();
                await Throws<OperationCanceledException>(() => request);
                Require(confirmations == 0 && !downloadToken.IsCancellationRequested && !installation.IsCompleted);
                service.Cancel(fixture.Definition.Identity);
                await handler.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Throws<OperationCanceledException>(() => installation);
                Require(handler.Calls == 1 && runner.Calls == 0);
            }
            finally
            {
                cancellation.Cancel(); service.Cancel(fixture.Definition.Identity);
                if (request is not null) await Observe(request);
                await Observe(installation);
            }
        });

        Test("running process retains files after lease disposal and uses the plugin runner", async path =>
        {
            var fixture = WriteFixture(path);
            using var http = new HttpClient(new NoNetwork());
            var sharedRunner = new NoProcesses(); var pluginRunner = new BlockingProcess();
            await using var service = Create(fixture, http, sharedRunner);
            service.UpdateReferences([], true, "");
            var lease = await service.TryRequestAsync(fixture.Definition, "Fixture.Plugin", runtimeProcesses: pluginRunner)
                ?? throw new Exception("Ready environment unexpectedly requested consent.");
            var run = lease.RunAsync(new(lease.PythonPath, ["-c", "pass"]));
            try
            {
                await pluginRunner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                lease.Dispose();
                var busy = service.GetEnvironments().Single();
                Require(busy.Users.SequenceEqual(["Fixture.Plugin"]) && !busy.CanRemove && sharedRunner.Calls == 0);
                await Throws<InvalidOperationException>(() => service.RemoveAsync(fixture.Definition.Identity));
                Require(Directory.Exists(fixture.Generation));
                pluginRunner.Release();
                await run;
                Require(service.GetEnvironments().Single().Users.Count == 0);
                await service.RemoveAsync(fixture.Definition.Identity);
                Require(!Directory.Exists(fixture.IdentityDirectory) && service.GetEnvironments().Count == 0);
            }
            finally { pluginRunner.Release(); lease.Dispose(); await Observe(run); }
        });

        foreach (var cancelByShutdown in new[] { false, true })
            Test(cancelByShutdown ? "shutdown waits for process drain before completing" : "caller cancellation keeps ownership until drain", async path =>
            {
                var fixture = WriteFixture(path);
                using var http = new HttpClient(new NoNetwork()); using var cancellation = new CancellationTokenSource();
                var runner = new BlockingProcess();
                await using var service = Create(fixture, http, runner);
                service.UpdateReferences([], true, "");
                var lease = await service.TryRequestAsync(fixture.Definition, "Fixture.Plugin") ?? throw new Exception("Missing lease.");
                var run = lease.RunAsync(new(lease.PythonPath, ["-c", "pass"]), cancellation.Token);
                Task? shutdown = null;
                try
                {
                    await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    lease.Dispose();
                    if (cancelByShutdown) shutdown = service.DisposeAsync().AsTask();
                    else cancellation.Cancel();
                    await runner.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Require(!run.IsCompleted && service.GetEnvironments().Single().Users.Count == 1);
                    if (shutdown is not null) Require(!shutdown.IsCompleted);
                    else await Throws<InvalidOperationException>(() => service.RemoveAsync(fixture.Definition.Identity));
                    runner.Release();
                    await Throws<OperationCanceledException>(() => run);
                    if (shutdown is not null) await shutdown;
                    Require(service.GetEnvironments().Single().Users.Count == 0 && Directory.Exists(fixture.Generation));
                }
                finally { runner.Release(); lease.Dispose(); await Observe(run); if (shutdown is not null) await shutdown; }
            });

        Test("execution forces isolated UTF8 and clears only reserved overrides", async path =>
        {
            var fixture = WriteFixture(path);
            using var http = new HttpClient(new NoNetwork());
            var runner = new BlockingProcess();
            await using var service = Create(fixture, http, runner);
            using var lease = await service.TryRequestAsync(fixture.Definition, "Fixture.Plugin") ?? throw new Exception("Missing lease.");
            var request = new ProcessRequest(lease.PythonPath, ["-c", "pass"])
            {
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    ["PYTHONPATH"] = "outside", ["UV_INDEX"] = "https://outside.example/", ["PIP_INDEX_URL"] = "outside",
                    ["CONDA_PREFIX"] = "outside", ["VIRTUAL_ENV"] = "outside", ["PLUGIN_VALUE"] = "中文"
                }
            };
            var run = lease.RunAsync(request);
            try
            {
                var observed = await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Require(observed.Arguments.Take(4).SequenceEqual(["-I", "-B", "-X", "utf8"]));
                Require(observed.Arguments.Skip(4).SequenceEqual(request.Arguments));
                foreach (var name in new[] { "PYTHONPATH", "UV_INDEX", "PIP_INDEX_URL", "CONDA_PREFIX", "VIRTUAL_ENV" })
                    Require(observed.EnvironmentVariables!.TryGetValue(name, out var value) && value is null);
                Require(observed.EnvironmentVariables!["PLUGIN_VALUE"] == "中文" && request.EnvironmentVariables!["PYTHONPATH"] == "outside");
                runner.Release();
                var result = await run;
                Require(result.StandardOutput == "中文输出\n" && result.StandardError == "中文诊断\n");
            }
            finally { runner.Release(); await Observe(run); }
        });

        foreach (var added in new[] { "unknown.txt", ".files.json", "environment/new-folder/unknown.txt" })
            Test("unknown payload blocks removal and source migration: " + added, async path =>
            {
                var fixture = WriteFixture(path);
                using var http = new HttpClient(new NoNetwork());
                await using var service = Create(fixture, http, new NoProcesses());
                service.UpdateReferences([], true, "");
                var file = Path.Combine(fixture.Generation!, added.Replace('/', Path.DirectorySeparatorChar));
                Write(file, "user-owned additional content");
                await Throws<InvalidDataException>(() => service.RemoveAsync(fixture.Definition.Identity));
                Throws<InvalidDataException>(() => PythonEnvironmentService.ReadMigrationEnvironments(fixture.PackageRoot, validatePayload: true));
                Require(File.ReadAllText(file) == "user-owned additional content" && Directory.Exists(fixture.IdentityDirectory));
            });

        Test("preparation record without a generation is ordinary migration data", path =>
        {
            var fixture = WriteFixture(path, ready: false);
            var before = File.ReadAllText(Path.Combine(fixture.IdentityDirectory, "environment.json"));
            Require(PythonEnvironmentService.ReadMigrationEnvironments(fixture.PackageRoot, validatePayload: true).Count == 0);
            Require(PythonEnvironmentService.ReadMigrationEnvironments(fixture.PackageRoot).Count == 1);
            Require(File.ReadAllText(Path.Combine(fixture.IdentityDirectory, "environment.json")) == before);
            return Task.CompletedTask;
        });

        Test("a trailing separator keeps installation and management on the same cache", async path =>
        {
            var fixture = WriteFixture(path, ready: false);
            using var handler = new NoNetwork(); using var http = new HttpClient(handler);
            await using var service = new PythonEnvironmentService(fixture.PackageRoot + Path.DirectorySeparatorChar,
                "win-x64", http, new NoProcesses(), new Log(), _ => true);
            await Throws<HttpRequestException>(() => service.InstallAsync(fixture.Definition.Identity));
            Require(File.Exists(Path.Combine(path, "Cache", "uv", ".python-cache")) && service.CanCleanCache);
            Require(handler.Calls == 1 && !Directory.Exists(Path.Combine(fixture.PackageRoot, "Cache")));
        });

        foreach (var owned in new[] { true, false })
            Test(owned ? "owned cache without a ready interpreter can be cleared independently" : "unknown cache is preserved", async path =>
            {
                var fixture = WriteFixture(path, ready: false);
                var cache = Path.Combine(path, "Cache", "uv");
                Write(Path.Combine(cache, "content", "download.bin"), "cache bytes");
                if (owned) Write(Path.Combine(cache, ".python-cache"), "TractorTool.PythonCache.v1");
                var data = Path.Combine(path, "Data", "Plugins", "Fixture.Python", "business.txt");
                var otherCache = Path.Combine(path, "Cache", "Node", "other.txt");
                Write(data, "business data"); Write(otherCache, "other cache");
                var original = File.ReadAllText(Path.Combine(fixture.IdentityDirectory, "environment.json"));
                using var http = new HttpClient(new NoNetwork()); var runner = new NoProcesses();
                await using var service = Create(fixture, http, runner);
                Require(service.CanCleanCache);
                if (owned) { await service.CleanCacheAsync(); Require(!Directory.Exists(cache)); }
                else
                {
                    await Throws<InvalidDataException>(() => service.CleanCacheAsync());
                    Require(File.ReadAllText(Path.Combine(cache, "content", "download.bin")) == "cache bytes");
                }
                Require(File.ReadAllText(data) == "business data" && File.ReadAllText(otherCache) == "other cache" && runner.Calls == 0);
                Require(File.ReadAllText(Path.Combine(fixture.IdentityDirectory, "environment.json")) == original);
            });
    }

    private sealed record Fixture(string PackageRoot, string IdentityDirectory, string? Generation, PythonEnvironmentDefinition Definition);

    private static Fixture WriteFixture(string path, bool ready = true)
    {
        var inputs = Path.Combine(path, "inputs");
        const string project = "[project]\nname = \"fixture\"\nversion = \"1.0.0\"\n";
        const string locked = "version = 1\nrevision = 5\n";
        Write(Path.Combine(inputs, "pyproject.toml"), project); Write(Path.Combine(inputs, "uv.lock"), locked);
        var definition = PythonEnvironmentDefinitions.Snapshot(new()
        {
            Id = "worker", PythonVersion = PythonEnvironmentDefinitions.SupportedPythonVersion,
            Project = "pyproject.toml", Lock = "uv.lock", ProjectSha256 = Digest(project), LockSha256 = Digest(locked)
        }, "Fixture.Python", "1.0.0", "Python 夹具", inputs, "win-x64");
        var packages = Path.Combine(path, "Packages"); var category = Path.Combine(packages, "uv");
        Write(Path.Combine(category, ".python-packages"), "TractorTool.PythonPackages.v1");
        var identity = Path.Combine(category, "Environments", definition.PluginId, definition.Identity);
        var owner = "TractorTool.PythonEnvironment.v1\n" + definition.Identity;
        Write(Path.Combine(identity, ".owner"), owner);
        string? generation = null; string? generationPath = null; string? hash = null; long? bytes = null;
        Dictionary<string, string> generationHashes = [];
        if (ready)
        {
            generation = Guid.NewGuid().ToString("N"); generationPath = Path.Combine(identity, generation);
            Write(Path.Combine(generationPath, ".owner"), owner);
            const string payload = "Synthetic bytes. Never execute this file.";
            Write(Path.Combine(generationPath, "environment", "Scripts", "python.exe"), payload);
            bytes = Encoding.UTF8.GetByteCount(payload);
            var inventory = JsonSerializer.Serialize(new[] { new { Path = "environment/Scripts/python.exe", Length = bytes.Value, Sha256 = Digest(payload) } }, JsonOptions.Value);
            Write(Path.Combine(identity, generation + ".files.json"), inventory);
            hash = Digest(inventory); generationHashes[generation] = hash;
        }
        Write(Path.Combine(identity, "environment.json"), JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, Definition = definition, Generation = generation, InstalledRoot = generationPath,
            ManifestHash = hash, InstalledBytes = bytes, GenerationHashes = generationHashes, PreparingGenerations = Array.Empty<string>()
        }, JsonOptions.Value));
        return new(packages, identity, generationPath, definition);
    }

    private static PythonEnvironmentService Create(Fixture fixture, HttpClient http, IProcessRunner runner) =>
        new(fixture.PackageRoot, "win-x64", http, runner, new Log(), _ => true);
    private static void Write(string path, string contents)
    { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, contents, new UTF8Encoding(false)); }
    private static string Digest(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private sealed class BlockingProcess : IProcessRunner
    {
        public TaskCompletionSource<ProcessRequest> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _drained.TrySetResult();
        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            using var registration = cancellationToken.Register(() => CancellationObserved.TrySetResult());
            Started.TrySetResult(request);
            // Cancellation alone deliberately does not complete: model process-tree cleanup and pipe drain.
            await _drained.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new(0, "中文输出\n", "中文诊断\n", false, false);
        }
    }
    private sealed class NoNetwork : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Interlocked.Increment(ref Calls); throw new HttpRequestException("Python environment fixture network sentinel."); }
    }
    private sealed class BlockingDownload : HttpMessageHandler
    {
        public int Calls;
        public TaskCompletionSource<CancellationToken> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            using var registration = token.Register(() => CancellationObserved.TrySetResult());
            Started.TrySetResult(token);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("The blocked download must finish through cancellation.");
        }
    }
    private sealed class NoProcesses : IProcessRunner
    {
        public int Calls;
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Calls); throw new Exception("Unexpected process execution."); }
    }
    private sealed class Log : IAppLog { public void Write(string source, string message, Exception? error = null) { } }
    private static void Require(bool value) { if (!value) throw new Exception("Python environment assertion failed."); }
    private static async Task Observe(Task task) { try { await task.ConfigureAwait(false); } catch (OperationCanceledException) { } }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
