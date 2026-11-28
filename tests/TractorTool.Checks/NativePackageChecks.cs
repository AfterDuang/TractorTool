using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TractorTool.Contracts;
using TractorTool.Contracts.Packages;
using TractorTool.Contracts.Processes;
using TractorTool.Core.Packages;
using TractorTool.Core.Plugins;
using TractorTool.Core.Storage;

internal static class NativePackageChecks
{
    private static readonly PackageReference Key = new("Native.Fixture", "1.0.0");

    public static void Run(string root, Action<string, Action> check)
    {
        void Test(string name, Func<string, Task> action) => check("Native packages: " + name, () =>
            action(Path.Combine(root, "native-" + Guid.NewGuid().ToString("N"))).GetAwaiter().GetResult());

        Test("installs and reloads a complete runtime without executing it", async path =>
        {
            var bytes = Archive(); var definition = Definition(bytes); var runner = new Probe();
            using var http = Client(bytes);
            await using (var service = Create(path, http, runner, definition))
            {
                await service.InstallAsync(Key);
                var status = service.GetStatus(Key);
                Require(status.Kind == PackageKind.NativeLibrary && status.Phase == PackagePhase.Ready);
                Require(status.Directory == Path.Combine(path, "DLL", Key.Id, Key.Version, "win-x64"));
                using var scope = service.CreateScope("Native.Plugin", [Key], CancellationToken.None, supportsNativeLibraries: true);
                using var lease = await scope.AcquireAsync(Key);
                Require(lease.Tools.Count == 0 && lease.Libraries.Count == 1);
                Require(lease.Libraries["runtime"] == Path.Combine(status.Directory, "bin", "runtime.dll"));
                Require(File.Exists(Path.Combine(status.Directory, "data", "runtime.dat")));
                using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(status.Directory, "receipt.json")));
                Require(receipt.RootElement.GetProperty("Schema").GetInt32() == 3);
                Require(receipt.RootElement.GetProperty("ManifestHash").GetString()?.Length == 64);
            }
            // No plugin recipes or constructor definitions are needed to rediscover a native receipt.
            await using var reloaded = Create(path, http, runner);
            Require(reloaded.GetStatus(Key).Phase == PackagePhase.Ready && runner.Calls == 0);
            using (var scope = reloaded.CreateScope("Native.Plugin", [Key], CancellationToken.None, supportsNativeLibraries: true))
            using (var lease = await scope.AcquireAsync(Key)) Require(lease.Libraries.Count == 1);
            await reloaded.RemoveAsync(Key);
            Require(reloaded.GetStatus(Key).Phase == PackagePhase.Missing);
        });

        foreach (var mutation in new[] { "replace", "remove", "add", "manifest" })
            Test("rejects changed runtime payload: " + mutation, async path =>
            {
                var bytes = Archive(); using var http = Client(bytes);
                await using var service = Create(path, http, new Probe(), Definition(bytes));
                await service.InstallAsync(Key);
                var directory = service.GetStatus(Key).Directory;
                var resource = Path.Combine(directory, "data", "runtime.dat");
                switch (mutation)
                {
                    case "replace": File.WriteAllText(resource, "changed!"); break; // Same length; hash must catch it.
                    case "remove": File.Delete(resource); break;
                    case "add": File.WriteAllText(Path.Combine(directory, "bin", "unexpected.dll"), "unexpected"); break;
                    case "manifest": File.AppendAllText(Path.Combine(directory, ".package-files.json"), " "); break;
                }
                using var scope = service.CreateScope("Native.Plugin", [Key], CancellationToken.None, supportsNativeLibraries: true);
                await Throws<InvalidDataException>(() => scope.AcquireAsync(Key));
                Require(service.GetStatus(Key).Phase == PackagePhase.Failed && service.GetStatus(Key).Users.Count == 0);
                // Unpinned damaged payload can be replaced through the same consent path.
                var prompts = 0;
                service.ConfirmInstallAsync = (prompt, _) => { prompts++; Require(prompt.IsRepair); return Task.FromResult(true); };
                using var repaired = await scope.TryRequestAsync(Key);
                Require(repaired is not null && prompts == 1 && File.ReadAllText(resource) == "original");
            });

        Test("retention survives lease, scope and service disposal and prevents repair", async path =>
        {
            var bytes = Archive(); var handler = new Handler(bytes); using var http = new HttpClient(handler);
            var definition = Definition(bytes);
            string directory;
            await using (var service = Create(path, http, new Probe(), definition))
            {
                await service.InstallAsync(Key);
                var scope = service.CreateScope("Native.Plugin", [Key], CancellationToken.None, supportsNativeLibraries: true);
                var lease = await scope.AcquireAsync(Key);
                directory = lease.Directory;
                lease.RetainUntilProcessExit(); lease.RetainUntilProcessExit();
                scope.Dispose(); lease.Dispose();
                Require(service.GetStatus(Key).Users.Count == 0);
                Require(service.GetStatus(Key).RetainedBy.SequenceEqual(["Native.Plugin"]));
                await Throws<InvalidOperationException>(() => service.RemoveAsync(Key));
            }
            // This simulates a partial native load: there was a retention call but no successful load or remaining lease.
            File.WriteAllText(Path.Combine(directory, "data", "runtime.dat"), "changed!");
            await using var reloaded = Create(path, http, new Probe(), definition);
            reloaded.UpdateReferences(new([new(Key, "Native.Plugin", "Native", [NativeRecipe(bytes)])], true));
            Require(reloaded.GetStatus(Key).RetainedBy.SequenceEqual(["Native.Plugin"]));
            using var nextScope = reloaded.CreateScope("Native.Plugin", [Key], CancellationToken.None, supportsNativeLibraries: true);
            await Throws<InvalidDataException>(() => nextScope.AcquireAsync(Key));
            var prompts = 0;
            reloaded.ConfirmInstallAsync = (_, _) => { prompts++; return Task.FromResult(true); };
            var downloads = handler.Calls; handler.RejectRequests = true;
            await Throws<InvalidOperationException>(() => nextScope.TryRequestAsync(Key));
            await Throws<InvalidOperationException>(() => reloaded.InstallAsync(Key));
            await Throws<InvalidOperationException>(() => reloaded.RemoveAsync(Key));
            Require(prompts == 0 && handler.Calls == downloads && File.ReadAllText(Path.Combine(directory, "data", "runtime.dat")) == "changed!");
        });

        Test("legacy scopes cannot acquire or prepare a native-only package", async path =>
        {
            foreach (var installed in new[] { false, true })
            {
                var bytes = Archive(); var handler = new Handler(bytes); using var http = new HttpClient(handler);
                await using var service = Create(Path.Combine(path, installed ? "installed" : "missing"), http, new Probe(), Definition(bytes));
                if (installed) await service.InstallAsync(Key);
                var requests = handler.Calls; handler.RejectRequests = true;
                var prompts = 0;
                service.ConfirmInstallAsync = (_, _) => { prompts++; return Task.FromResult(true); };
                using var scope = service.CreateScope("Legacy.Plugin", [Key], CancellationToken.None);
                await Throws<NotSupportedException>(() => scope.AcquireAsync(Key));
                await Throws<NotSupportedException>(() => scope.TryRequestAsync(Key));
                Require(handler.Calls == requests && prompts == 0 && service.GetStatus(Key).Users.Count == 0);
                Require(service.GetStatus(Key).Phase == (installed ? PackagePhase.Ready : PackagePhase.Missing));
            }
        });

        Test("legacy scopes reject native recipes before GitHub resolution", async path =>
        {
            var bytes = Archive(); var handler = new Handler(bytes) { RejectRequests = true };
            using var http = new HttpClient(handler);
            await using var service = Create(path, http, new Probe());
            service.UpdateReferences(new([new(Key, "Native.Plugin", "Native", [NativeRecipe(bytes)])], true));
            var prompts = 0;
            service.ConfirmInstallAsync = (_, _) => { prompts++; return Task.FromResult(true); };
            using var scope = service.CreateScope("Legacy.Plugin", [Key], CancellationToken.None);
            await Throws<NotSupportedException>(() => scope.TryRequestAsync(Key));
            Require(handler.Calls == 0 && prompts == 0 && !Directory.Exists(Path.Combine(path, "DLL")));
        });

        Test("unresolved package status uses declarations without network and reports conflicting recipes", async path =>
        {
            var bytes = Archive(); var handler = new Handler(bytes) { RejectRequests = true };
            using var http = new HttpClient(handler);
            await using var service = Create(path, http, new Probe());
            var native = NativeRecipe(bytes);
            service.UpdateReferences(new([new(Key, "Native.Plugin", "Native", [native])], true));
            using var scope = service.CreateScope("Native.Plugin", [Key], CancellationToken.None, supportsNativeLibraries: true);
            var status = scope.GetStatus(Key);
            Require(status.Phase == PackagePhase.Missing && status.Kind == PackageKind.NativeLibrary && handler.Calls == 0);
            Require(service.GetStatus(new("Legacy.Unknown", "1.0.0")).Kind == PackageKind.Tool);
            var tool = native with { Kind = PackageKind.Tool, Libraries = new Dictionary<string, string>(),
                Tools = new Dictionary<string, PackageTool> { ["tool"] = new("tool.exe", "fixture") } };
            service.UpdateReferences(new([new(Key, "Native.Plugin", "Native", [native]), new(Key, "Tool.Plugin", "Tool", [tool])], true));
            status = scope.GetStatus(Key);
            Require(status.Phase == PackagePhase.Failed && status.Error.Length != 0 && handler.Calls == 0);
        });

        Test("requesting a ready native package with a lost receipt reports failure without downloading", async path =>
        {
            var bytes = Archive(); var handler = new Handler(bytes); using var http = new HttpClient(handler);
            await using var service = Create(path, http, new Probe(), Definition(bytes));
            await service.InstallAsync(Key);
            var directory = service.GetStatus(Key).Directory;
            File.Delete(Path.Combine(directory, "receipt.json"));
            var requests = handler.Calls; handler.RejectRequests = true;
            using var scope = service.CreateScope("Native.Plugin", [Key], CancellationToken.None, supportsNativeLibraries: true);
            await Throws<InvalidDataException>(() => scope.TryRequestAsync(Key));
            var failed = service.GetStatus(Key);
            Require(failed.Phase == PackagePhase.Failed && failed.Error.Length != 0 && failed.Users.Count == 0);
            Require(handler.Calls == requests && File.Exists(Path.Combine(directory, "bin", "runtime.dll")));
        });

        Test("same-process receipt loss revokes permission to repair or remove a native directory", async path =>
        {
            foreach (var mutation in new[] { "missing", "corrupt", "foreign" })
            {
                var bytes = Archive(); var handler = new Handler(bytes); using var http = new HttpClient(handler);
                await using var service = Create(Path.Combine(path, mutation), http, new Probe(), Definition(bytes));
                await service.InstallAsync(Key);
                var directory = service.GetStatus(Key).Directory;
                var receiptPath = Path.Combine(directory, "receipt.json");
                if (mutation == "missing") File.Delete(receiptPath);
                else if (mutation == "corrupt") File.WriteAllText(receiptPath, "{broken");
                else
                {
                    var receipt = JsonNode.Parse(File.ReadAllText(receiptPath))!;
                    receipt["ArchiveHash"] = new string('f', 64);
                    File.WriteAllText(receiptPath, receipt.ToJsonString());
                }
                var keep = Path.Combine(directory, "keep.dat"); File.WriteAllText(keep, "external content");
                var downloads = handler.Calls; handler.RejectRequests = true;
                // No failed acquisition or restart first: removal itself must refresh ownership.
                await Throws<InvalidOperationException>(() => service.RemoveAsync(Key));
                var item = service.GetPackages().Single();
                Require(!item.CanRemove && !item.CanInstall && File.ReadAllText(keep) == "external content");
                await Throws<InvalidOperationException>(() => service.InstallAsync(Key));
                Require(handler.Calls == downloads && File.Exists(receiptPath) == (mutation != "missing"));
            }
        });

        Test("repair rechecks the receipt after downloading before replacing existing files", async path =>
        {
            var bytes = Archive(); var handler = new Handler(bytes); using var http = new HttpClient(handler);
            await using var service = Create(path, http, new Probe(), Definition(bytes));
            await service.InstallAsync(Key);
            var directory = service.GetStatus(Key).Directory;
            File.WriteAllText(Path.Combine(directory, "data", "runtime.dat"), "changed!");
            using var scope = service.CreateScope("Native.Plugin", [Key], CancellationToken.None, supportsNativeLibraries: true);
            await Throws<InvalidDataException>(() => scope.AcquireAsync(Key));
            handler.OnRequest = () =>
            {
                File.Delete(Path.Combine(directory, "receipt.json"));
                File.WriteAllText(Path.Combine(directory, "keep.dat"), "external content");
            };
            service.ConfirmInstallAsync = (_, _) => Task.FromResult(true);
            await Throws<InvalidOperationException>(() => scope.TryRequestAsync(Key));
            Require(File.ReadAllText(Path.Combine(directory, "keep.dat")) == "external content" &&
                File.ReadAllText(Path.Combine(directory, "data", "runtime.dat")) == "changed!");
            Require(!service.GetPackages().Single().CanRemove);
        });

        Test("disposed leases cannot establish process retention", async path =>
        {
            var bytes = Archive(); using var http = Client(bytes);
            await using var service = Create(path, http, new Probe(), Definition(bytes));
            await service.InstallAsync(Key);
            using var scope = service.CreateScope("Native.Plugin", [Key], CancellationToken.None, supportsNativeLibraries: true);
            var lease = await scope.AcquireAsync(Key); lease.Dispose();
            Throws<ObjectDisposedException>(lease.RetainUntilProcessExit);
            Require(service.GetStatus(Key).RetainedBy.Count == 0);
            await service.RemoveAsync(Key);
        });

        Test("rejects wrong architecture, managed assemblies and invalid PE files", async path =>
        {
            var cases = new[] { NativePe(0xAA64), File.ReadAllBytes(typeof(PackageReference).Assembly.Location), Encoding.UTF8.GetBytes("not a PE image") };
            for (var i = 0; i < cases.Length; i++)
            {
                var bytes = Archive(cases[i]); var runner = new Probe(); using var http = Client(bytes);
                await using var service = Create(Path.Combine(path, i.ToString()), http, runner, Definition(bytes));
                await Throws<InvalidDataException>(() => service.InstallAsync(Key));
                Require(service.GetStatus(Key).Phase == PackagePhase.Failed && runner.Calls == 0);
                Require(!Directory.Exists(service.GetStatus(Key).Directory));
            }
        });

        foreach (var reserved in new[] { "RECEIPT.JSON", ".PACKAGE-FILES.JSON" })
            Test("rejects archive metadata collision: " + reserved, async path =>
            {
                var bytes = Archive(extra: [(reserved, Encoding.UTF8.GetBytes("untrusted record"))]);
                using var http = Client(bytes);
                await using var service = Create(path, http, new Probe(), Definition(bytes));
                await Throws<InvalidDataException>(() => service.InstallAsync(Key));
                Require(!Directory.Exists(service.GetStatus(Key).Directory));
            });

        Test("large file inventories remain separate from bounded receipts", async path =>
        {
            var extras = Enumerable.Range(0, 500).Select(i => ($"data/{i:D4}-{new string('a', 80)}.dat", new byte[] { 1 })).ToArray();
            var bytes = Archive(extra: extras); using var http = Client(bytes);
            await using var service = Create(path, http, new Probe(), Definition(bytes));
            await service.InstallAsync(Key);
            using var scope = service.CreateScope("Native.Plugin", [Key], CancellationToken.None, supportsNativeLibraries: true);
            using var lease = await scope.AcquireAsync(Key);
            Require(new FileInfo(Path.Combine(lease.Directory, ".package-files.json")).Length > 64 * 1024);
            Require(new FileInfo(Path.Combine(lease.Directory, "receipt.json")).Length <= 64 * 1024);
        });

        Test("legacy package named DLL is preserved and blocks claiming the native category", async path =>
        {
            var key = new PackageReference("DLL", "1.0.0");
            var toolBytes = Zip([("bin/tool.exe", Encoding.UTF8.GetBytes("fixture only"))]);
            var tool = ToolDefinition(key, toolBytes); using var http = Client(toolBytes);
            await using (var legacy = Create(path, http, new Probe(), tool)) await legacy.InstallAsync(key);
            var original = Path.Combine(path, "DLL", "1.0.0", "win-x64", "bin", "tool.exe");
            var nativeBytes = Archive(); using var nativeHttp = Client(nativeBytes);
            Throws<InvalidDataException>(() => Create(path, nativeHttp, new Probe(), Definition(nativeBytes)));
            Require(File.ReadAllText(original) == "fixture only");
            Require(!File.Exists(Path.Combine(path, "DLL", ".native-packages")));
            await using var legacyReloaded = Create(path, http, new Probe());
            using var scope = legacyReloaded.CreateScope("Legacy.Plugin", [key], CancellationToken.None);
            using var lease = await scope.AcquireAsync(key);
            Require(legacyReloaded.GetStatus(key).Kind == PackageKind.Tool && lease.Libraries.Count == 0);
            Throws<InvalidOperationException>(lease.RetainUntilProcessExit);
        });

        Test("legacy tools can still contain a file named like the native inventory", async path =>
        {
            var key = new PackageReference("Legacy.Tool", "1.0.0");
            var bytes = Zip([("bin/tool.exe", Encoding.UTF8.GetBytes("fixture only")),
                (".package-files.json", Encoding.UTF8.GetBytes("tool-owned data"))]);
            using var http = Client(bytes);
            await using var service = Create(path, http, new Probe(), ToolDefinition(key, bytes));
            await service.InstallAsync(key);
            using var scope = service.CreateScope("Legacy.Plugin", [key], CancellationToken.None);
            using var lease = await scope.AcquireAsync(key);
            Require(File.ReadAllText(Path.Combine(lease.Directory, ".package-files.json")) == "tool-owned data");
            Require(lease.Tools.Count == 1 && lease.Libraries.Count == 0);
        });

        Test("legacy tool definitions at the original size limit still load from schema 2", async path =>
        {
            var key = new PackageReference("Legacy.LargeDefinition", "1.0.0");
            var bytes = Zip([("bin/tool.exe", Encoding.UTF8.GetBytes("fixture only"))]);
            var legacy = JsonSerializer.SerializeToNode(ToolDefinition(key, bytes) with { Description = "" })!.AsObject();
            legacy.Remove("Kind"); legacy.Remove("Libraries");
            legacy["Description"] = new string('a', 48 * 1024 - JsonSerializer.SerializeToUtf8Bytes(legacy).Length);
            Require(JsonSerializer.SerializeToUtf8Bytes(legacy).Length == 48 * 1024);
            var definition = JsonSerializer.Deserialize<PackageDefinition>(legacy.ToJsonString())!;
            Require(definition.Kind == PackageKind.Tool && JsonSerializer.SerializeToUtf8Bytes(definition).Length > 48 * 1024);
            using var http = Client(bytes);
            await using (var service = Create(path, http, new Probe(), definition)) await service.InstallAsync(key);
            var receiptPath = Path.Combine(path, key.Id, key.Version, "win-x64", "receipt.json");
            var receipt = JsonNode.Parse(File.ReadAllText(receiptPath))!.AsObject();
            Require(receipt["Schema"]!.GetValue<int>() == 2);
            receipt["Definition"] = legacy.DeepClone(); // Recreate an old receipt with only the original nine definition fields.
            receipt.Remove("ManifestHash");
            File.WriteAllText(receiptPath, receipt.ToJsonString());
            Require(new FileInfo(receiptPath).Length <= 64 * 1024);
            await using var reloaded = Create(path, http, new Probe());
            Require(reloaded.GetStatus(key).Phase == PackagePhase.Ready);
            using var scope = reloaded.CreateScope("Legacy.Plugin", [key], CancellationToken.None);
            using var lease = await scope.AcquireAsync(key);
            Require(File.ReadAllText(lease.Tools["tool"]) == "fixture only");
        });

        Test("native maintenance requires a trustworthy receipt, not merely a category marker", async path =>
        {
            var bytes = Archive();
            foreach (var corruptReceipt in new[] { false, true })
            {
                var packageRoot = Path.Combine(path, corruptReceipt ? "corrupt" : "missing");
                // Establish an owned category through installation; the second directory is unrelated existing content.
                using (var seedHttp = Client(bytes))
                {
                    var seed = Definition(bytes) with { Package = new("Native.Seed", "1.0.0") };
                    await using var seedService = Create(packageRoot, seedHttp, new Probe(), seed);
                    await seedService.InstallAsync(seed.Package);
                }
                var directory = Path.Combine(packageRoot, "DLL", Key.Id, Key.Version, "win-x64");
                Directory.CreateDirectory(directory);
                var original = Path.Combine(directory, "keep.dat"); File.WriteAllText(original, "unknown payload");
                var receipt = Path.Combine(directory, "receipt.json");
                if (corruptReceipt) File.WriteAllText(receipt, "{broken");
                var handler = new Handler(bytes) { RejectRequests = true }; using var http = new HttpClient(handler);
                await using var service = Create(packageRoot, http, new Probe());
                service.UpdateReferences(new([new(Key, "Native.Plugin", "Native", [NativeRecipe(bytes)])], true));
                var item = service.GetPackages().Single(p => p.Package == Key);
                Require(item.IsInstalled && !item.CanInstall && !item.CanRemove);
                var prompts = 0;
                service.ConfirmInstallAsync = (_, _) => { prompts++; return Task.FromResult(true); };
                using var scope = service.CreateScope("Native.Plugin", [Key], CancellationToken.None, supportsNativeLibraries: true);
                await Throws<InvalidOperationException>(() => scope.TryRequestAsync(Key));
                await Throws<InvalidOperationException>(() => service.InstallAsync(Key));
                service.UpdateReferences(new([], true)); // No declared reference may be the reason cleanup is blocked.
                Require(!service.GetPackages().Single(p => p.Package == Key).CanRemove);
                await Throws<InvalidOperationException>(() => service.RemoveAsync(Key));
                Require(prompts == 0 && handler.Calls == 0 && File.ReadAllText(original) == "unknown payload");
                Require(corruptReceipt ? File.ReadAllText(receipt) == "{broken" : !File.Exists(receipt));
            }

            // Conversely, a validated receipt still establishes ownership when only the payload inventory is missing.
            var ownedRoot = Path.Combine(path, "repairable"); var definition = Definition(bytes);
            using var repairHttp = Client(bytes);
            await using (var installed = Create(ownedRoot, repairHttp, new Probe(), definition)) await installed.InstallAsync(Key);
            var ownedDirectory = Path.Combine(ownedRoot, "DLL", Key.Id, Key.Version, "win-x64");
            File.Delete(Path.Combine(ownedDirectory, ".package-files.json"));
            await using var reloaded = Create(ownedRoot, repairHttp, new Probe(), definition);
            var ownedItem = reloaded.GetPackages().Single(p => p.Package == Key);
            Require(ownedItem.IsInstalled && ownedItem.CanInstall && ownedItem.CanRemove && ownedItem.Status.Phase == PackagePhase.Failed);
            var repairPrompts = 0;
            reloaded.ConfirmInstallAsync = (prompt, _) => { Require(prompt.IsRepair); repairPrompts++; return Task.FromResult(true); };
            using var repairScope = reloaded.CreateScope("Native.Plugin", [Key], CancellationToken.None, supportsNativeLibraries: true);
            using var repaired = await repairScope.TryRequestAsync(Key);
            Require(repaired is not null && repairPrompts == 1 && File.Exists(Path.Combine(ownedDirectory, ".package-files.json")));
        });

        Test("missing or corrupt legacy receipts cannot redirect the same identity into the native category", async path =>
        {
            foreach (var corruptReceipt in new[] { false, true })
            {
                var packageRoot = Path.Combine(path, corruptReceipt ? "corrupt" : "missing");
                var legacyDirectory = Path.Combine(packageRoot, Key.Id, Key.Version, "win-x64");
                Directory.CreateDirectory(legacyDirectory);
                var original = Path.Combine(legacyDirectory, "keep.dat"); File.WriteAllText(original, "original legacy payload");
                var receipt = Path.Combine(legacyDirectory, "receipt.json");
                if (corruptReceipt) File.WriteAllText(receipt, "{broken");
                var bytes = Archive(); var handler = new Handler(bytes); using var http = new HttpClient(handler);
                // Discover the damaged legacy location before the new plugin introduces its native recipe.
                await using var service = Create(packageRoot, http, new Probe());
                service.UpdateReferences(new([new(Key, "Native.Plugin", "Native", [NativeRecipe(bytes)])], true));
                var prompts = 0;
                service.ConfirmInstallAsync = (_, _) => { prompts++; return Task.FromResult(true); };
                using var scope = service.CreateScope("Native.Plugin", [Key], CancellationToken.None, supportsNativeLibraries: true);
                await Throws<InvalidDataException>(() => scope.TryRequestAsync(Key));
                Require(handler.Calls == 0 && prompts == 0);
                Require(File.ReadAllText(original) == "original legacy payload");
                Require(corruptReceipt ? File.ReadAllText(receipt) == "{broken" : !File.Exists(receipt));
                Require(!Directory.Exists(Path.Combine(packageRoot, "DLL", Key.Id)));
            }
        });

        Test("a corrupt native receipt cannot redirect the same identity into the tool layout", async path =>
        {
            var bytes = Archive(); using (var http = Client(bytes))
            {
                await using var service = Create(path, http, new Probe(), Definition(bytes));
                await service.InstallAsync(Key);
            }
            var directory = Path.Combine(path, "DLL", Key.Id, Key.Version, "win-x64");
            var receipt = Path.Combine(directory, "receipt.json"); File.WriteAllText(receipt, "{broken");
            var handler = new Handler(bytes); using var reloadHttp = new HttpClient(handler);
            await using var reloaded = Create(path, reloadHttp, new Probe());
            var toolRecipe = NativeRecipe(bytes) with
            {
                Kind = PackageKind.Tool,
                Libraries = new Dictionary<string, string>(),
                Tools = new Dictionary<string, PackageTool> { ["tool"] = new("bin/tool.exe", "fixture 1.0.0") }
            };
            reloaded.UpdateReferences(new([new(Key, "Tool.Plugin", "Tool", [toolRecipe])], true));
            using var scope = reloaded.CreateScope("Tool.Plugin", [Key], CancellationToken.None);
            await Throws<InvalidDataException>(() => scope.TryRequestAsync(Key));
            Require(handler.Calls == 0 && File.ReadAllText(receipt) == "{broken");
            Require(File.ReadAllText(Path.Combine(directory, "data", "runtime.dat")) == "original");
            Require(!Directory.Exists(Path.Combine(path, Key.Id)));
        });

        Test("a damaged native category marker does not hide independent legacy installations", async path =>
        {
            // Create DLL first; the damaged category must not terminate the top-level scan.
            Directory.CreateDirectory(Path.Combine(path, "DLL"));
            var marker = Path.Combine(path, "DLL", ".native-packages"); File.WriteAllText(marker, "incomplete");
            var key = new PackageReference("Z.LegacyTool", "1.0.0");
            var bytes = Zip([("bin/tool.exe", Encoding.UTF8.GetBytes("fixture only"))]);
            using var http = Client(bytes);
            await using (var installed = Create(path, http, new Probe(), ToolDefinition(key, bytes)))
                await installed.InstallAsync(key);
            await using var reloaded = Create(path, http, new Probe()); // No recipe/definition may rescue the scan.
            var item = reloaded.GetPackages().Single(p => p.Package == key);
            Require(item.IsInstalled && item.Status.Phase == PackagePhase.Ready && item.Status.Kind == PackageKind.Tool);
            Require(reloaded.SourceError.Length != 0 && File.ReadAllText(marker) == "incomplete");
            using var scope = reloaded.CreateScope("Legacy.Plugin", [key], CancellationToken.None);
            using var lease = await scope.AcquireAsync(key);
            Require(File.ReadAllText(lease.Tools["tool"]) == "fixture only");
        });

        Test("unknown DLL directory is never adopted or overwritten", path =>
        {
            Directory.CreateDirectory(Path.Combine(path, "DLL"));
            var original = Path.Combine(path, "DLL", "unknown.txt"); File.WriteAllText(original, "keep");
            var bytes = Archive(); using var http = Client(bytes);
            Throws<InvalidDataException>(() => Create(path, http, new Probe(), Definition(bytes)));
            Require(File.ReadAllText(original) == "keep" && !File.Exists(Path.Combine(path, "DLL", ".native-packages")));
            return Task.CompletedTask;
        });

        Test("native declarations require API 1.8 and strict package kinds", path =>
        {
            var recipe = new PackageDownload("win-x64", "fixture/runtime", "v1.0.0", "runtime.zip",
                new string('a', 64), "Runtime", "Fixture", "MIT", new Dictionary<string, PackageTool>())
            { Kind = PackageKind.NativeLibrary, Libraries = new Dictionary<string, string> { ["runtime"] = "bin/runtime.dll" } };
            var manifest = new PluginManifest { SchemaVersion = 1, Id = "Test.Native", Version = "1.0.0", Name = "Native",
                EntryAssembly = "Test.dll", EntryType = "Test.Plugin", ApiVersion = "1.8", UiApiVersion = "1.0",
                AvaloniaVersion = "12.1.6", SupportedPlatforms = ["windows"],
                Dependencies = [new(Key.Id, Key.Version) { Downloads = [recipe] }] };
            var directory = Path.Combine(path, manifest.Id, manifest.Version);
            ManifestValidator.Validate(manifest, directory);
            var host = new HostCompatibility("windows", "win-x64", "1.0", "12.1.6");
            Require(host.Check(manifest) is null && host.Check(manifest with { ApiVersion = "1.7" }) is not null);
            Throws<InvalidDataException>(() => ManifestValidator.Validate(manifest with { ApiVersion = "1.7" }, directory));
            var json = JsonSerializer.SerializeToNode(recipe, JsonOptions.Value)!.AsObject();
            json.Remove("tools"); // Native authors need not declare an empty executable map.
            var roundtrip = JsonSerializer.Deserialize<PackageDownload>(json.ToJsonString(), JsonOptions.Value)!;
            ManifestValidator.Validate(manifest with { Dependencies = [new(Key.Id, Key.Version) { Downloads = [roundtrip] }] }, directory);
            foreach (JsonNode? badKind in new JsonNode?[] { JsonValue.Create(1), JsonValue.Create("unknown"), JsonValue.Create("1") })
            {
                json["kind"] = badKind;
                Throws<JsonException>(() => JsonSerializer.Deserialize<PackageDownload>(json.ToJsonString(), JsonOptions.Value));
            }
            return Task.CompletedTask;
        });
    }

    private static PackageDefinition Definition(byte[] bytes) => new(Key, "win-x64", "Native fixture", "Never executed",
        new("https://example.invalid/runtime.zip"), Convert.ToHexString(SHA256.HashData(bytes)), "Fixture",
        new("https://example.invalid/source"), new Dictionary<string, PackageTool>())
    { Kind = PackageKind.NativeLibrary, Libraries = new Dictionary<string, string> { ["runtime"] = "bin/runtime.dll" } };

    private static PackageDefinition ToolDefinition(PackageReference key, byte[] bytes) => new(key, "win-x64", "Legacy tool", "Fixture",
        new("https://example.invalid/tool.zip"), Convert.ToHexString(SHA256.HashData(bytes)), "Fixture",
        new("https://example.invalid/source"), new Dictionary<string, PackageTool> { ["tool"] = new("bin/tool.exe", "fixture 1.0.0") });

    private static PackageDownload NativeRecipe(byte[] bytes) => new("win-x64", "fixture/runtime", "v1.0.0", "runtime.zip",
        Convert.ToHexString(SHA256.HashData(bytes)), "Runtime", "Fixture", "MIT", new Dictionary<string, PackageTool>())
    { Kind = PackageKind.NativeLibrary, Libraries = new Dictionary<string, string> { ["runtime"] = "bin/runtime.dll" } };

    private static ExternalPackageService Create(string path, HttpClient http, Probe runner, params PackageDefinition[] definitions)
    {
        var service = new ExternalPackageService(path, "win-x64", http, runner, new Log(), definitions);
        service.UpdateReferences(new([], true));
        return service;
    }

    private static byte[] Archive(byte[]? dll = null, (string Name, byte[] Bytes)[]? extra = null) => Zip(
        [("bin/runtime.dll", dll ?? NativePe()), ("data/runtime.dat", Encoding.UTF8.GetBytes("original")), .. extra ?? []]);

    internal static byte[] Zip((string Name, byte[] Bytes)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            foreach (var (name, bytes) in files)
            {
                var entry = zip.CreateEntry(name);
                entry.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var output = entry.Open(); output.Write(bytes);
            }
        return buffer.ToArray();
    }

    // Structurally valid PE32+ DLL headers and one section, only parsed as bytes; never loaded or executed.
    internal static byte[] NativePe(ushort machine = 0x8664)
    {
        var bytes = new byte[1024];
        void U16(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
        void U32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        U16(0, 0x5A4D); U32(0x3C, 0x80); U32(0x80, 0x00004550);
        U16(0x84, machine); U16(0x86, 1); U16(0x94, 0xF0); U16(0x96, 0x2022);
        const int optional = 0x98;
        U16(optional, 0x20B); U32(optional + 16, 0x1000); U32(optional + 20, 0x1000);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(optional + 24), 0x180000000);
        U32(optional + 32, 0x1000); U32(optional + 36, 0x200);
        U32(optional + 56, 0x2000); U32(optional + 60, 0x200); U16(optional + 68, 3);
        U32(optional + 108, 16);
        const int section = optional + 0xF0;
        Encoding.ASCII.GetBytes(".text").CopyTo(bytes, section);
        U32(section + 8, 1); U32(section + 12, 0x1000); U32(section + 16, 0x200);
        U32(section + 20, 0x200); U32(section + 36, 0x60000020); bytes[0x200] = 0xC3;
        return bytes;
    }

    private static HttpClient Client(byte[] bytes) => new(new Handler(bytes));
    private sealed class Handler(byte[] bytes) : HttpMessageHandler
    {
        public int Calls;
        public bool RejectRequests;
        public Action? OnRequest;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Calls);
            if (RejectRequests) throw new HttpRequestException("This check must reject the operation before network access.");
            OnRequest?.Invoke();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) });
        }
    }
    private sealed class Probe : IProcessRunner
    {
        public int Calls;
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Calls); return Task.FromResult(new ProcessResult(0, "fixture 1.0.0", "", false, false)); }
    }
    private sealed class Log : IAppLog { public void Write(string source, string message, Exception? error = null) { } }
    private static void Require(bool value) { if (!value) throw new Exception("Native package assertion failed."); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
