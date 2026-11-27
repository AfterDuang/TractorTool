using System.Text.Json;
using TractorTool.Contracts;
using TractorTool.Core.Logging;
using TractorTool.Core.Plugins;
using TractorTool.Core.Storage;

internal static class PluginLifecycleChecks
{
    public static void Run(Action<string, Action> check, Action<Task> pump, string output,
        string fixtureDirectory, HostCompatibility compatibility)
    {
        Check("Plugin registration can request concurrent shutdown without holding the lifecycle gate", (host, log) =>
        {
            var callerThread = Environment.CurrentManagedThreadId;
            var registeredOnCaller = false;
            var shutdownReturned = false;
            var disposals = 0;
            Task<Task>? shutdown = null;
            var loading = host.LoadWithResourcesAsync((_, _) =>
            {
                registeredOnCaller = Environment.CurrentManagedThreadId == callerThread;
                shutdown = BeginConcurrentShutdown(host);
                // Wait only for DisposeAsync to return its task, never for shutdown to finish.
                shutdownReturned = shutdown.Wait(TimeSpan.FromSeconds(2));
                return new Registration(() => Interlocked.Increment(ref disposals));
            });
            pump(loading);
            Require(shutdown is not null, "Registration did not run.");
            pump(shutdown!.Unwrap());
            Require(registeredOnCaller && shutdownReturned, "Registration held the lifecycle gate or changed UI context.");
            Require(disposals == 1, "The registration returned during shutdown was not released exactly once.");
            Require(host.Records.Single().State != PluginState.Loaded, "Shutdown allowed a late Loaded publication.");
            Require(!log.Snapshot().Entries.Any(e => e.Message == "shutdown-fixture:background-started"),
                "Background work started after shutdown won registration.");
            AssertCleanedOnce(log);
        });
        Check("Registration cleanup can reenter concurrent disposal and shares the original shutdown task", (host, log) =>
        {
            var callerThread = Environment.CurrentManagedThreadId;
            var cleanedOnCaller = false;
            var shutdownReturned = false;
            var disposals = 0;
            Task<Task>? repeated = null;
            pump(host.LoadWithResourcesAsync((_, _) => new Registration(() =>
            {
                Interlocked.Increment(ref disposals);
                cleanedOnCaller = Environment.CurrentManagedThreadId == callerThread;
                repeated = BeginConcurrentShutdown(host);
                shutdownReturned = repeated.Wait(TimeSpan.FromSeconds(2));
            })));
            Require(host.Records.Single().State == PluginState.Loaded, "Fixture failed to load.");
            var first = host.DisposeAsync().AsTask();
            pump(first);
            Require(repeated is not null, "Registration cleanup did not run.");
            pump(repeated!.Unwrap());
            Require(cleanedOnCaller && shutdownReturned, "Cleanup held the lifecycle gate or changed its calling context.");
            Require(disposals == 1 && ReferenceEquals(first, repeated!.Result) &&
                ReferenceEquals(first, host.DisposeAsync().AsTask()), "Concurrent disposal did not share one cleanup.");
            AssertCleanedOnce(log);
        });

        void Check(string name, Action<PluginHost, AppLog> action) => check(name, () =>
        {
            var root = Path.Combine(output, "lifecycle-gate-" + Guid.NewGuid().ToString("N"));
            var paths = new AppPaths(Path.Combine(root, "app"), Path.Combine(root, "data"));
            paths.EnsureDataDirectories();
            var manifest = JsonSerializer.Deserialize<PluginManifest>(
                File.ReadAllText(Path.Combine(fixtureDirectory, "plugin.json")), JsonOptions.Value)!
                with { EntryType = "IndependentPlugin.ShutdownProbePlugin" };
            var destination = Path.Combine(paths.PluginRoot, manifest.Id, manifest.Version);
            foreach (var file in Directory.EnumerateFiles(fixtureDirectory, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination, Path.GetRelativePath(fixtureDirectory, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            File.WriteAllText(Path.Combine(destination, "plugin.json"), JsonSerializer.Serialize(manifest, JsonOptions.Value));
            var log = new AppLog(paths.LogRoot);
            var host = new PluginHost(paths, compatibility, log);
            try { host.Discover([]); action(host, log); }
            finally { pump(host.DisposeAsync().AsTask()); }
        });
    }

    // A dedicated thread avoids thread-pool availability influencing this lock-order check.
    private static Task<Task> BeginConcurrentShutdown(PluginHost host) => Task.Factory.StartNew(
        () => host.DisposeAsync().AsTask(), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static void AssertCleanedOnce(AppLog log)
    {
        var entries = log.Snapshot().Entries;
        foreach (var stage in new[] { "initialize-started", "initialize-completed", "stop-started", "stop-completed", "disposed" })
            Require(entries.Count(e => e.Message == "shutdown-fixture:" + stage) == 1, "Lifecycle stage repeated or missing: " + stage);
        Require(!entries.Any(e => e.Message is "shutdown-fixture:disposed-too-early" or "shutdown-fixture:stop-before-initialize-completed"),
            "Plugin resources were released before lifecycle work finished.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Registration(Action dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { dispose(); return ValueTask.CompletedTask; }
    }
}
