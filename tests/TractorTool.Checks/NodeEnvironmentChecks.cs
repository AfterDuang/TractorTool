using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TractorTool.Contracts;
using TractorTool.Contracts.Packages;
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

        foreach (var changed in new[] { false, true })
        Test("locked installed environment retries validation " + (changed ? "and still rejects changed bytes" : "without reinstalling"), async path =>
        {
            var fixture = WriteFixture(path);
            var definition = fixture.Environment.Definition;
            var payload = Path.Combine(fixture.Generation, "node", $"node-v{definition.NodeVersion}-win-x64", "node.exe");
            var recordPath = Path.Combine(fixture.Identity, "environment.json");
            var originalRecord = File.ReadAllText(recordPath);
            using var handler = new NoNetwork(); using var http = new HttpClient(handler);
            var confirmations = 0;
            await using var service = new NodeEnvironmentService(fixture.Packages, "win-x64", http, new NoRunner(), new Log(),
                _ => throw new Exception("Existing generation queried a source."));
            service.ConfirmInstallAsync = (prompt, _) =>
            {
                Require(prompt.IsRepair); confirmations++; return Task.FromResult(false);
            };
            using (var held = new FileStream(payload, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await Reject<IOException>(() => service.TryRequestAsync(definition, "NodeFixture"));
                await Reject<IOException>(() => service.TryRequestAsync(definition, "NodeFixture"));
                var failed = service.GetEnvironments().Single();
                Require(failed.Phase == PackagePhase.Failed && failed.Error.Length != 0 && failed.Users.Count == 0);
                Require(confirmations == 0);
            }
            if (changed)
            {
                var timestamp = File.GetLastWriteTimeUtc(payload);
                Write(payload, "X" + File.ReadAllText(payload)[1..]); File.SetLastWriteTimeUtc(payload, timestamp);
                await Reject<InvalidDataException>(() => service.TryRequestAsync(definition, "NodeFixture"));
                Require(await service.TryRequestAsync(definition, "NodeFixture") is null && confirmations == 1);
                Require(service.GetEnvironments().Single().Phase == PackagePhase.Failed);
            }
            else
            {
                using var lease = await service.TryRequestAsync(definition, "NodeFixture");
                Require(lease?.NodePath == payload && confirmations == 0);
                var recovered = service.GetEnvironments().Single();
                Require(recovered.Phase == PackagePhase.Ready && recovered.Error.Length == 0);
            }
            Require(handler.Calls == 0 && File.ReadAllText(recordPath) == originalRecord);
        });

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

        Test("cache removal never validates or reserves a Node runtime", async path =>
        {
            var fixture = WriteFixture(path);
            var cache = Path.Combine(path, "Cache", "Node");
            Write(Path.Combine(cache, ".node-cache"), "TractorTool.NodeCache.v1");
            Write(Path.Combine(cache, "content", "download.bin"), "cached bytes");
            Write(Path.Combine(fixture.Generation, "node", $"node-v{fixture.Environment.Definition.NodeVersion}-win-x64", "node.exe"), "changed runtime");
            var log = new Log(); var reserved = false;
            using var http = new HttpClient(new NoNetwork());
            await using var service = new NodeEnvironmentService(fixture.Packages, "win-x64", http, new NoRunner(), log, _ => true);
            service.Changed += () => reserved |= service.GetEnvironments().Any(e => e.Users.Count != 0);
            await service.CleanCacheAsync();
            Require(!Directory.Exists(cache) && Directory.Exists(fixture.Generation) && !reserved &&
                !log.Messages.Any(message => message.Contains("工具链无法用于缓存维护", StringComparison.Ordinal)));
        });

        foreach (var dangling in DirectoryLinkFixture.DanglingCases)
        Test("preparation accepts " + (dangling ? "dangling" : "live") + " pnpm project registrations without following them", async path =>
        {
            var fixture = WriteFixture(path);
            var cache = Path.Combine(path, "Cache", "Node");
            Write(Path.Combine(cache, ".node-cache"), "TractorTool.NodeCache.v1");
            var target = Path.Combine(dangling ? Path.Combine(fixture.Identity, Guid.NewGuid().ToString("N")) : fixture.Generation, "environment");
            if (!dangling) Write(Path.Combine(target, "keep.txt"), "project data");
            var link = ProjectRegistration(cache, target);
            DirectoryLinkFixture.Create(link, target);
            var ordinary = Path.Combine(cache, "store", "v10", "projects", "ordinary", "keep.txt");
            Write(ordinary, "ordinary cache content");
            using var handler = new NoNetwork(); using var http = new HttpClient(handler);
            await using var service = new NodeEnvironmentService(fixture.Packages, "win-x64", http, new NoRunner(), new Log(), _ => true);
            await Reject<HttpRequestException>(() => service.InstallAsync(fixture.Environment.Definition.Identity));
            Require(handler.Calls == 1 && new DirectoryInfo(link).LinkTarget is not null);
            Require(File.ReadAllText(ordinary) == "ordinary cache content");
            Require(dangling ? !Directory.Exists(target) : File.ReadAllText(Path.Combine(target, "keep.txt")) == "project data");
            Require(Directory.Exists(fixture.Generation));
        });

        foreach (var dangling in DirectoryLinkFixture.DanglingCases)
        Test("cache cleanup unlinks " + (dangling ? "dangling" : "live") + " pnpm registrations and preserves installed payload", async path =>
        {
            var fixture = WriteFixture(path);
            var cache = Path.Combine(path, "Cache", "Node");
            Write(Path.Combine(cache, ".node-cache"), "TractorTool.NodeCache.v1");
            var target = Path.Combine(dangling ? Path.Combine(fixture.Identity, Guid.NewGuid().ToString("N")) : fixture.Generation, "environment");
            if (!dangling) Write(Path.Combine(target, "keep.txt"), "project data");
            DirectoryLinkFixture.Create(ProjectRegistration(cache, target), target);
            var runtime = Path.Combine(fixture.Generation, "node", $"node-v{fixture.Environment.Definition.NodeVersion}-win-x64", "node.exe");
            var original = File.ReadAllBytes(runtime);
            using var handler = new NoNetwork(); using var http = new HttpClient(handler);
            await using var service = new NodeEnvironmentService(fixture.Packages, "win-x64", http, new NoRunner(), new Log(), _ => true);
            await service.CleanCacheAsync();
            Require(!Directory.Exists(cache) && File.ReadAllBytes(runtime).SequenceEqual(original) && handler.Calls == 0);
            Require(dangling ? !Directory.Exists(target) : File.ReadAllText(Path.Combine(target, "keep.txt")) == "project data");
        });

        foreach (var kind in new[] { "unknown-owner", "wrong-name", "wrong-hash", "wrong-location", "external-target", "invalid-plugin-id", "ancestor", "cache-root" })
        Test("preparation and cleanup preserve unrecognized cache links: " + kind, async path =>
        {
            var fixture = WriteFixture(path);
            var cache = Path.Combine(path, "Cache", "Node");
            var target = kind switch
            {
                "external-target" => Path.Combine(path, "linked-project"),
                "invalid-plugin-id" => Path.Combine(fixture.Packages, "Node", "Environments", "not a plugin id",
                    fixture.Environment.Definition.Identity, Guid.NewGuid().ToString("N"), "environment"),
                _ => Path.Combine(fixture.Generation, "environment")
            };
            // Keep the invalid ID target absent so environment discovery cannot reject it before the cache check.
            if (kind != "invalid-plugin-id") Write(Path.Combine(target, "keep.txt"), "do not follow or delete");
            string link;
            if (kind == "cache-root")
            {
                Write(Path.Combine(target, ".node-cache"), "TractorTool.NodeCache.v1");
                DirectoryLinkFixture.Create(link = cache, target);
            }
            else
            {
                Write(Path.Combine(cache, ".node-cache"), kind == "unknown-owner" ? "foreign cache" : "TractorTool.NodeCache.v1");
                link = kind switch
                {
                    "wrong-name" => Path.Combine(cache, "store", "v10", "projects", "not-a-project-hash"),
                    "wrong-hash" => ProjectRegistration(cache, target + "-different"),
                    "wrong-location" => Path.Combine(cache, "metadata", Digest(target)[..32].ToLowerInvariant()),
                    "ancestor" => Path.Combine(cache, "store"),
                    _ => ProjectRegistration(cache, target)
                };
                DirectoryLinkFixture.Create(link, target);
            }
            using var handler = new NoNetwork(); using var http = new HttpClient(handler);
            await using var service = new NodeEnvironmentService(fixture.Packages, "win-x64", http, new NoRunner(), new Log(), _ => true);
            await Reject<InvalidDataException>(() => service.InstallAsync(fixture.Environment.Definition.Identity));
            await Reject<InvalidDataException>(() => service.CleanCacheAsync());
            Require(handler.Calls == 0 && new DirectoryInfo(link).LinkTarget is not null && Directory.Exists(fixture.Generation));
            Require(kind == "invalid-plugin-id" ? !Directory.Exists(target) : File.ReadAllText(Path.Combine(target, "keep.txt")) == "do not follow or delete");
        });

        Test("an unknown cache link prevents even recognized registrations from being removed", async path =>
        {
            var fixture = WriteFixture(path);
            var cache = Path.Combine(path, "Cache", "Node");
            Write(Path.Combine(cache, ".node-cache"), "TractorTool.NodeCache.v1");
            var target = Path.Combine(fixture.Generation, "environment");
            Write(Path.Combine(target, "keep.txt"), "project data");
            var registered = ProjectRegistration(cache, target);
            var unknown = Path.Combine(cache, "unknown-link");
            DirectoryLinkFixture.Create(registered, target);
            DirectoryLinkFixture.Create(unknown, target);
            using var http = new HttpClient(new NoNetwork());
            await using var service = new NodeEnvironmentService(fixture.Packages, "win-x64", http, new NoRunner(), new Log(), _ => true);
            await Reject<InvalidDataException>(() => service.CleanCacheAsync());
            Require(new DirectoryInfo(registered).LinkTarget is not null && new DirectoryInfo(unknown).LinkTarget is not null &&
                File.ReadAllText(Path.Combine(target, "keep.txt")) == "project data");
        });

        foreach (var interruption in new[] { "before-move", "after-move", "empty-generation", "metadata", "tree-gone", "legacy", "unknown", "changed", "legacy-changed", "unverified" })
            Test("restart resumes only owned removal: " + interruption, async path =>
            {
                var fixture = WriteFixture(path);
                var work = Path.Combine(path, "Cache", "Node-work");
                Write(Path.Combine(work, ".node-work"), "TractorTool.NodeWork.v1");
                var stage = Path.Combine(work, Guid.NewGuid().ToString("N"));
                var removed = Path.Combine(stage, fixture.Environment.Definition.PluginId, fixture.Environment.Definition.Identity);
                var journal = stage + ".removal.json";
                var record = File.ReadAllText(Path.Combine(fixture.Identity, "environment.json"));
                Directory.CreateDirectory(Path.GetDirectoryName(removed)!);
                if (!interruption.StartsWith("legacy", StringComparison.Ordinal)) Write(journal, record);
                if (interruption != "before-move") Directory.Move(fixture.Identity, removed);
                var generation = Path.Combine(removed, Path.GetFileName(fixture.Generation));
                if (interruption == "unverified")
                {
                    var preparing = System.Text.Json.Nodes.JsonNode.Parse(record)!;
                    foreach (var property in new[] { "generation", "installedRoot", "manifestHash", "installedBytes" }) preparing[property] = null;
                    preparing["generationHashes"] = new System.Text.Json.Nodes.JsonObject();
                    preparing["preparingGenerations"] = JsonSerializer.SerializeToNode(new[] { Path.GetFileName(generation) });
                    Write(journal, preparing.ToJsonString(JsonOptions.Value));
                    Write(Path.Combine(removed, "environment.json"), preparing.ToJsonString(JsonOptions.Value));
                }
                if (interruption is "empty-generation" or "metadata" or "tree-gone")
                {
                    RemoveFixtureGeneration(removed, generation, fixture.Environment.Definition.Identity);
                    if (interruption == "empty-generation") Directory.CreateDirectory(generation);
                    else
                    {
                        File.Delete(Path.Combine(removed, Path.GetFileName(generation) + ".files.json"));
                        File.Delete(Path.Combine(removed, "environment.json"));
                        if (interruption == "tree-gone")
                        {
                            File.Delete(Path.Combine(removed, ".owner"));
                            Directory.Delete(removed); Directory.Delete(Path.GetDirectoryName(removed)!); Directory.Delete(stage);
                        }
                    }
                }
                var unknown = Path.Combine(removed, "user.txt");
                if (interruption == "unknown") Write(unknown, "preserve");
                string? changed = null; string? replacement = null;
                if (interruption.EndsWith("changed", StringComparison.Ordinal))
                {
                    changed = Path.Combine(generation, "node", $"node-v{fixture.Environment.Definition.NodeVersion}-win-x64", "node.exe");
                    replacement = "X" + File.ReadAllText(changed)[1..];
                    Write(changed, replacement);
                }
                var log = new Log();
                using var http = new HttpClient(new NoNetwork());
                await using var service = new NodeEnvironmentService(fixture.Packages, "win-x64", http, new NoRunner(), log, _ => true);
                if (interruption == "unknown") Require(File.ReadAllText(unknown) == "preserve" && File.Exists(journal));
                else if (changed is not null)
                    Require(File.ReadAllText(changed) == replacement && Directory.Exists(stage) &&
                        File.Exists(journal) == (interruption == "changed") && log.Messages.Any(message => message.Contains("已保留", StringComparison.Ordinal)));
                else if (interruption == "unverified")
                    Require(Directory.Exists(generation) && File.Exists(journal) && log.Messages.Any(message => message.Contains("已保留", StringComparison.Ordinal)));
                else Require(!Directory.Exists(stage) && !File.Exists(journal));
                Require(service.GetEnvironments().Count == (interruption == "before-move" ? 1 : 0));
                if (interruption == "before-move") Require(Directory.Exists(fixture.Generation));
            });

        Test("explicit removal still accepts damaged bytes at recorded payload paths", async path =>
        {
            var fixture = WriteFixture(path);
            using var http = new HttpClient(new NoNetwork());
            await using var service = new NodeEnvironmentService(fixture.Packages, "win-x64", http, new NoRunner(), new Log(), _ => true);
            service.UpdateReferences([], true, "");
            Write(Path.Combine(fixture.Generation, "node", $"node-v{fixture.Environment.Definition.NodeVersion}-win-x64", "node.exe"), "damaged");
            await service.RemoveAsync(fixture.Environment.Definition.Identity);
            Require(!Directory.Exists(fixture.Identity) && service.GetEnvironments().Count == 0);
        });

        foreach (var interruption in new[] { "partial-before", "partial-legacy", "complete-legacy", "unknown-before", "changed-legacy" })
            Test("unpublished removal intent recovers only verified bytes: " + interruption, async path =>
            {
                var fixture = WriteFixture(path);
                var work = Path.Combine(path, "Cache", "Node-work");
                Write(Path.Combine(work, ".node-work"), "TractorTool.NodeWork.v1");
                var stage = Path.Combine(work, Guid.NewGuid().ToString("N"));
                var removed = Path.Combine(stage, fixture.Environment.Definition.PluginId, fixture.Environment.Definition.Identity);
                var pending = stage + "." + fixture.Environment.Definition.Identity + ".removal.pending";
                var record = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.Identity, "environment.json")))!;
                record["preparingGenerations"] = JsonSerializer.SerializeToNode(Array.Empty<string>());
                var bytes = Encoding.UTF8.GetBytes(record.ToJsonString(JsonOptions.Value));
                if (interruption != "complete-legacy") bytes = bytes[..(bytes.Length / 2)];
                if (interruption == "unknown-before") bytes[0] = (byte)'!';
                Directory.CreateDirectory(Path.GetDirectoryName(removed)!);
                File.WriteAllBytes(pending, bytes);
                var moved = interruption.EndsWith("legacy", StringComparison.Ordinal);
                if (moved) Directory.Move(fixture.Identity, removed);
                string? changed = null; string? replacement = null;
                if (interruption == "changed-legacy")
                {
                    changed = Path.Combine(removed, Path.GetFileName(fixture.Generation), "node",
                        $"node-v{fixture.Environment.Definition.NodeVersion}-win-x64", "node.exe");
                    replacement = "X" + File.ReadAllText(changed)[1..]; Write(changed, replacement);
                }
                var log = new Log(); using var http = new HttpClient(new NoNetwork());
                await using var service = new NodeEnvironmentService(fixture.Packages, "win-x64", http, new NoRunner(), log, _ => true);
                if (interruption is "unknown-before" or "changed-legacy")
                {
                    Require(File.ReadAllBytes(pending).SequenceEqual(bytes) && Directory.Exists(stage) &&
                        log.Messages.Any(message => message.Contains("已保留", StringComparison.Ordinal)));
                    if (changed is not null) Require(File.ReadAllText(changed) == replacement);
                }
                else Require(!File.Exists(pending) && !File.Exists(stage + ".removal.json") && !Directory.Exists(stage));
                Require(service.GetEnvironments().Count == (moved ? 0 : 1));
                if (!moved) Require(Directory.Exists(fixture.Generation));
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
    internal static string ProjectRegistration(string cache, string target) =>
        Path.Combine(cache, "store", "v10", "projects", Digest(target)[..32].ToLowerInvariant());
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
    private sealed class Log : IAppLog
    {
        public List<string> Messages { get; } = [];
        public void Write(string source, string message, Exception? error = null) => Messages.Add(message);
    }
}
