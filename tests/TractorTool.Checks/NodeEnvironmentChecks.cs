using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TractorTool.Contracts;
using TractorTool.Contracts.Processes;
using TractorTool.Core.Packages.Node;
using TractorTool.Core.Storage;

// Synthetic installation bytes exercise the public lifecycle; no downloaded code runs.
internal static class NodeEnvironmentChecks
{
    public static void Run(string root, Action<string, Action> check)
    {
        void Test(string name, Func<string, Task> body) => check("Node environments: " + name, () =>
            body(Path.Combine(root, "node-" + Guid.NewGuid().ToString("N"))).GetAwaiter().GetResult());

        Test("offline reuse and process drain retain the immutable environment", async path =>
        {
            var fixture = WriteFixture(path);
            using var http = new HttpClient(new NoNetwork());
            var runner = new BlockingRunner();
            await using var service = new NodeEnvironmentService(fixture.Packages, "win-x64", http, runner, new Log(),
                _ => throw new Exception("Offline reuse queried a source."));
            service.UpdateReferences([], true, "");
            var lease = await service.TryRequestAsync(fixture.Environment.Definition, "NodeFixture") ?? throw new Exception("Missing lease.");
            var request = new ProcessRequest(lease.NodePath, ["--eval", "console.log('fixture')"])
            {
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    ["NODE_OPTIONS"] = "--require outside.js", ["NODE_PATH"] = "outside",
                    ["npm_config_registry"] = "https://outside.example/", ["PLUGIN_VALUE"] = "keep"
                }
            };
            var running = lease.RunAsync(request);
            try
            {
                var actual = await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Require(actual.Arguments.SequenceEqual(new[] { "--no-global-search-paths" }.Concat(request.Arguments)));
                Require(actual.EnvironmentVariables!["NODE_OPTIONS"] is null && actual.EnvironmentVariables["NODE_PATH"] is null &&
                    actual.EnvironmentVariables["npm_config_registry"] is null && actual.EnvironmentVariables["PLUGIN_VALUE"] == "keep");
                Require(request.EnvironmentVariables!["NODE_OPTIONS"] == "--require outside.js");
                lease.Dispose();
                Require(!service.GetEnvironments().Single().CanRemove);
                await Reject<InvalidOperationException>(() => service.RemoveAsync(fixture.Environment.Definition.Identity));
                runner.Release.TrySetResult(); await running;
                Require(service.GetEnvironments().Single().CanRemove);
                await service.RemoveAsync(fixture.Environment.Definition.Identity);
                Require(service.GetEnvironments().Count == 0 && !Directory.Exists(fixture.Identity));
            }
            finally { runner.Release.TrySetResult(); lease.Dispose(); await running; }
        });

        Test("migration validation compares reloaded definitions by value", async path =>
        {
            var fixture = WriteFixture(path);
            var loaded = NodeEnvironmentService.ReadMigrationEnvironments(fixture.Packages, validatePayload: true).Single();
            Require(loaded == fixture.Environment && !ReferenceEquals(loaded.Definition, fixture.Environment.Definition));
            await NodeEnvironmentService.ValidateMigratedEnvironmentAsync(fixture.Packages, fixture.Environment, default);
            using var http = new HttpClient(new NoNetwork());
            await NodeEnvironmentService.RebuildCopiedEnvironmentAsync(fixture.Packages, loaded, http, new NoRunner(), new Log(),
                _ => throw new Exception("Validated target attempted rebuilding."), default);
        });

        Test("unknown payload blocks removal and cache cleanup preserves installed files", async path =>
        {
            var fixture = WriteFixture(path);
            var cache = Path.Combine(path, "Cache", "Node");
            Write(Path.Combine(cache, ".node-cache"), "TractorTool.NodeCache.v1");
            Write(Path.Combine(cache, "content", "download.bin"), "cached bytes");
            using var http = new HttpClient(new NoNetwork());
            await using var service = new NodeEnvironmentService(fixture.Packages, "win-x64", http, new NoRunner(), new Log(), _ => true);
            service.UpdateReferences([], true, "");
            await service.CleanCacheAsync();
            Require(!Directory.Exists(cache) && Directory.Exists(fixture.Generation));
            var unknown = Path.Combine(fixture.Generation, "user-added.txt"); Write(unknown, "retain");
            await Reject<InvalidDataException>(() => service.RemoveAsync(fixture.Environment.Definition.Identity));
            Require(File.ReadAllText(unknown) == "retain");
        });

        Test("legacy tool named Node never becomes a managed environment", async path =>
        {
            var packages = Path.Combine(path, "Packages");
            var legacy = Path.Combine(packages, "Node", "1.0.0", "win-x64", "keep.txt"); Write(legacy, "old tool");
            Require(NodeEnvironmentService.ReadMigrationEnvironments(packages).Count == 0);
            using var http = new HttpClient(new NoNetwork());
            await using var service = new NodeEnvironmentService(packages, "win-x64", http, new NoRunner(), new Log(), _ => true);
            Require(service.GetEnvironments().Count == 0 && File.ReadAllText(legacy) == "old tool");
        });

        Test("a trailing separator keeps installation and management on the same cache", async path =>
        {
            var fixture = WriteFixture(path);
            using var handler = new NoNetwork(); using var http = new HttpClient(handler);
            await using var service = new NodeEnvironmentService(fixture.Packages + Path.DirectorySeparatorChar,
                "win-x64", http, new NoRunner(), new Log(), _ => true);
            await Reject<HttpRequestException>(() => service.InstallAsync(fixture.Environment.Definition.Identity));
            Require(File.Exists(Path.Combine(path, "Cache", "Node", ".node-cache")) && service.CanCleanCache);
            Require(handler.Calls == 1 && !Directory.Exists(Path.Combine(fixture.Packages, "Cache")));
        });

        foreach (var changeExisting in new[] { false, true })
        Test("migration rejects retired " + (changeExisting ? "rewritten bytes" : "unrecorded files"), async path =>
        {
            var fixture = WriteFixture(path);
            WriteInstalled(fixture.Packages, fixture.Environment);
            var unknown = changeExisting ? Path.Combine(fixture.Generation, "node", $"node-v{fixture.Environment.Definition.NodeVersion}-win-x64", "node.exe")
                : Path.Combine(fixture.Generation, "unrecorded.txt");
            var content = changeExisting ? "X" + File.ReadAllText(unknown)[1..] : "retain";
            Write(unknown, content);
            var recordPath = Path.Combine(fixture.Identity, "environment.json");
            var record = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(recordPath))!;
            record["preparingGenerations"] = JsonSerializer.SerializeToNode(new[] { Path.GetFileName(fixture.Generation) });
            Write(recordPath, record.ToJsonString(JsonOptions.Value));
            using var handler = new NoNetwork(); using var http = new HttpClient(handler);
            await Reject<InvalidDataException>(() => NodeEnvironmentService.RebuildCopiedEnvironmentAsync(fixture.Packages,
                fixture.Environment, http, new NoRunner(), new Log(), _ => true, default));
            await Reject<InvalidDataException>(() => NodeEnvironmentService.ValidateMigratedEnvironmentAsync(fixture.Packages,
                fixture.Environment, default));
            Require(handler.Calls == 0 && File.ReadAllText(unknown) == content);
        });

        Test("migration preflight checks copied bytes before an installer can clean them", async path =>
        {
            var fixture = WriteFixture(path);
            var copiedPackages = Path.Combine(path, "target", "Packages");
            foreach (var source in Directory.EnumerateFiles(fixture.Packages, "*", SearchOption.AllDirectories))
                Write(Path.Combine(copiedPackages, Path.GetRelativePath(fixture.Packages, source)), File.ReadAllText(source));
            var payload = Path.Combine(copiedPackages, fixture.Environment.RelativeDirectory, Path.GetFileName(fixture.Generation),
                "node", $"node-v{fixture.Environment.Definition.NodeVersion}-win-x64", "node.exe");
            var modified = "X" + File.ReadAllText(payload)[1..]; Write(payload, modified);
            using var handler = new NoNetwork(); using var http = new HttpClient(handler);
            await Reject<InvalidDataException>(() => NodeEnvironmentService.RebuildCopiedEnvironmentAsync(copiedPackages,
                fixture.Environment, http, new NoRunner(), new Log(), _ => true, default));
            Require(handler.Calls == 0 && File.ReadAllText(payload) == modified);
        });

        Test("migration preflight rejects a wholly missing active generation", async path =>
        {
            var fixture = WriteFixture(path);
            var copiedPackages = Path.Combine(path, "target", "Packages");
            foreach (var source in Directory.EnumerateFiles(fixture.Packages, "*", SearchOption.AllDirectories))
                Write(Path.Combine(copiedPackages, Path.GetRelativePath(fixture.Packages, source)), File.ReadAllText(source));
            var identity = Path.Combine(copiedPackages, fixture.Environment.RelativeDirectory);
            var generation = Path.Combine(identity, Path.GetFileName(fixture.Generation));
            RemoveFixtureGeneration(identity, generation, fixture.Environment.Definition.Identity);
            using var handler = new NoNetwork(); using var http = new HttpClient(handler);
            await Reject<InvalidDataException>(() => NodeEnvironmentService.RebuildCopiedEnvironmentAsync(copiedPackages,
                fixture.Environment, http, new NoRunner(), new Log(), _ => true, default));
            Require(handler.Calls == 0 && File.Exists(Path.Combine(identity, "environment.json")) && !Directory.Exists(generation));
        });

        Test("migration validates retained manifests after their retired payload is gone", async path =>
        {
            var fixture = WriteFixture(path);
            var current = WriteInstalled(fixture.Packages, fixture.Environment);
            RemoveFixtureGeneration(fixture.Identity, fixture.Generation, fixture.Environment.Definition.Identity);
            var manifest = Path.Combine(fixture.Identity, Path.GetFileName(fixture.Generation) + ".files.json");
            Write(manifest, "changed orphan manifest");
            var recordPath = Path.Combine(fixture.Identity, "environment.json");
            var record = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(recordPath))!;
            record["preparingGenerations"] = JsonSerializer.SerializeToNode(new[] { Path.GetFileName(fixture.Generation) });
            Write(recordPath, record.ToJsonString(JsonOptions.Value));
            using var handler = new NoNetwork(); using var http = new HttpClient(handler);
            await Reject<InvalidDataException>(() => NodeEnvironmentService.ValidateMigratedEnvironmentAsync(fixture.Packages,
                fixture.Environment, default));
            await Reject<InvalidDataException>(() => NodeEnvironmentService.RebuildCopiedEnvironmentAsync(fixture.Packages,
                fixture.Environment, http, new NoRunner(), new Log(), _ => true, default));
            // A copied record has the old absolute path; exercise the pre-install path as well.
            record["installedRoot"] = current.Generation + "-old-location";
            Write(recordPath, record.ToJsonString(JsonOptions.Value));
            await Reject<InvalidDataException>(() => NodeEnvironmentService.RebuildCopiedEnvironmentAsync(fixture.Packages,
                fixture.Environment, http, new NoRunner(), new Log(), _ => true, default));
            Require(handler.Calls == 0 && File.ReadAllText(manifest) == "changed orphan manifest" && Directory.Exists(current.Generation));
        });

        foreach (var remainder in new[] { "payload", "manifest", "manifest.tmp" })
        Test("migration resumes registered preparation cleanup for " + remainder, async path =>
        {
            var fixture = WriteFixture(path);
            var preparing = Guid.NewGuid().ToString("N");
            var directory = Path.Combine(fixture.Identity, preparing);
            Write(Path.Combine(directory, ".owner"), "TractorTool.NodeEnvironment.v1\n" + fixture.Environment.Definition.Identity);
            Write(Path.Combine(directory, "partial.bin"), "unverified output");
            if (remainder != "payload")
            {
                Write(Path.Combine(fixture.Identity, preparing + ".files.json" + (remainder == "manifest.tmp" ? ".tmp" : "")), "unfinished manifest");
                RemoveFixtureGeneration(fixture.Identity, directory, fixture.Environment.Definition.Identity);
            }
            var recordPath = Path.Combine(fixture.Identity, "environment.json");
            var record = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(recordPath))!;
            record["preparingGenerations"] = JsonSerializer.SerializeToNode(new[] { preparing });
            Write(recordPath, record.ToJsonString(JsonOptions.Value));
            using var handler = new NoNetwork(); using var http = new HttpClient(handler);
            await Reject<InvalidDataException>(() => NodeEnvironmentService.ValidateMigratedEnvironmentAsync(fixture.Packages,
                fixture.Environment, default));
            Require(remainder == "payload" ? File.Exists(Path.Combine(directory, "partial.bin"))
                : File.Exists(Path.Combine(fixture.Identity, preparing + ".files.json" + (remainder == "manifest.tmp" ? ".tmp" : ""))));
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await NodeEnvironmentService.RebuildCopiedEnvironmentAsync(fixture.Packages,
                    fixture.Environment, http, new NoRunner(), new Log(), _ => true, default);
                await NodeEnvironmentService.ValidateMigratedEnvironmentAsync(fixture.Packages, fixture.Environment, default);
            }
            record = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(recordPath))!;
            Require(record["preparingGenerations"]!.AsArray().Count == 0 && handler.Calls == 0 &&
                !Directory.Exists(directory) && !File.Exists(Path.Combine(fixture.Identity, preparing + ".files.json")) &&
                !File.Exists(Path.Combine(fixture.Identity, preparing + ".files.json.tmp")) && Directory.Exists(fixture.Generation));
        });

        Test("migration permits already removed retired payload", async path =>
        {
            var fixture = WriteFixture(path);
            WriteInstalled(fixture.Packages, fixture.Environment);
            File.Delete(Path.Combine(fixture.Generation, "node", $"node-v{fixture.Environment.Definition.NodeVersion}-win-x64", "node.exe"));
            await NodeEnvironmentService.ValidateMigratedEnvironmentAsync(fixture.Packages, fixture.Environment, default);
        });
    }

    internal sealed record Fixture(string Packages, string Identity, string Generation, NodeMigrationEnvironment Environment);

    internal static Fixture WriteFixture(string root)
    {
        var input = Path.Combine(root, "node-input");
        const string project = "{\"name\":\"node-fixture\",\"version\":\"1.0.0\",\"private\":true}";
        const string locked = "lockfileVersion: '9.0'\nsettings:\n  autoInstallPeers: true\n  excludeLinksFromLockfile: false\nimporters:\n  .: {}\n";
        Write(Path.Combine(input, "package.json"), project); Write(Path.Combine(input, "pnpm-lock.yaml"), locked);
        var definition = NodeEnvironmentDefinitions.Snapshot(new()
        {
            Id = "worker", NodeVersion = NodeEnvironmentDefinitions.SupportedNodeVersion,
            Project = "package.json", Lock = "pnpm-lock.yaml", ProjectSha256 = Digest(project), LockSha256 = Digest(locked)
        }, "NodeFixture", "1.0.0", "Node fixture", input, "win-x64");
        var environment = new NodeMigrationEnvironment(Path.Combine("Node", "Environments", definition.PluginId, definition.Identity), definition);
        return WriteInstalled(Path.Combine(root, "Packages"), environment);
    }

    internal static Fixture WriteInstalled(string packages, NodeMigrationEnvironment environment)
    {
        var definition = environment.Definition;
        Write(Path.Combine(packages, "Node", ".node-packages"), "TractorTool.NodePackages.v1");
        var identity = Path.Combine(packages, environment.RelativeDirectory);
        var owner = "TractorTool.NodeEnvironment.v1\n" + definition.Identity;
        Write(Path.Combine(identity, ".owner"), owner);
        var generation = Guid.NewGuid().ToString("N"); var directory = Path.Combine(identity, generation);
        Write(Path.Combine(directory, ".owner"), owner);
        var relative = $"node/node-v{definition.NodeVersion}-win-x64/node.exe";
        var payload = "Synthetic, never executed: " + directory;
        Write(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)), payload);
        Directory.CreateDirectory(Path.Combine(directory, "environment"));
        var inventory = JsonSerializer.Serialize(new[] { new { Path = relative, Length = Encoding.UTF8.GetByteCount(payload), Sha256 = Digest(payload) } }, JsonOptions.Value);
        var hash = Digest(inventory);
        Write(Path.Combine(identity, generation + ".files.json"), inventory);
        var hashes = new Dictionary<string, string>();
        var recordPath = Path.Combine(identity, "environment.json");
        if (File.Exists(recordPath))
        {
            using var old = JsonDocument.Parse(File.ReadAllText(recordPath));
            foreach (var item in old.RootElement.GetProperty("generationHashes").EnumerateObject()) hashes[item.Name] = item.Value.GetString()!;
        }
        hashes[generation] = hash;
        Write(recordPath, JsonSerializer.Serialize(new { SchemaVersion = 1, Definition = definition, Generation = generation,
            InstalledRoot = directory, ManifestHash = hash, InstalledBytes = Encoding.UTF8.GetByteCount(payload), GenerationHashes = hashes }, JsonOptions.Value));
        return new(packages, identity, directory, environment);
    }

    private static void Write(string path, string value)
    { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value, new UTF8Encoding(false)); }
    private static void RemoveFixtureGeneration(string identity, string generation, string owner)
    {
        Require(Path.GetDirectoryName(Path.GetFullPath(generation)) == Path.GetFullPath(identity) &&
            Guid.TryParseExact(Path.GetFileName(generation), "N", out _) &&
            File.ReadAllText(Path.Combine(generation, ".owner")) == "TractorTool.NodeEnvironment.v1\n" + owner);
        Directory.Delete(generation, recursive: true);
    }
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void Require(bool value) { if (!value) throw new Exception("Node environment assertion failed."); }
    private static async Task Reject<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private sealed class NoNetwork : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Interlocked.Increment(ref Calls); throw new HttpRequestException("Node environment fixture network sentinel."); }
    }
    private sealed class NoRunner : IProcessRunner
    { public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default) => throw new Exception("Unexpected process start."); }
    private sealed class BlockingRunner : IProcessRunner
    {
        internal TaskCompletionSource<ProcessRequest> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        { Started.TrySetResult(request); await Release.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancellationToken.ThrowIfCancellationRequested(); return new(0, "", "", false, false); }
    }
    private sealed class Log : IAppLog { public void Write(string source, string message, Exception? error = null) { } }
}
