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

internal static class NativeArchiveChecks
{
    private static readonly PackageReference Key = new("Native.Archives", "1.0.0");
    private const string NativeEntry = "runtime.data/purelib/runtime/bin/runtime.dll";
    private const string ResourceEntry = "components.data/purelib/runtime/data/runtime.dat";

    public static void Run(string root, Action<string, Action> check)
    {
        void Test(string name, Func<string, Task> action) => check("Native archives: " + name, () =>
            action(Path.Combine(root, "native-archives-" + Guid.NewGuid().ToString("N"))).GetAwaiter().GetResult());

        Test("maps two upstream archives and reloads the complete runtime offline", async path =>
        {
            var fixture = new Fixture(); using var http = new HttpClient(fixture);
            string directory;
            await using (var service = Create(path, http, fixture.Recipe))
            {
                using var scope = Scope(service);
                using var lease = await scope.TryRequestAsync(Key);
                Require(lease is not null, "The approved runtime must be installed.");
                directory = lease!.Directory;
                Require(lease.Libraries["runtime"] == Path.Combine(directory, "bin", "runtime.dll"), "The DLL entrypoint must use the mapped path.");
                Require(File.ReadAllText(Resource(directory)) == "original", "The second archive's resource must be included.");
                Require(!File.Exists(Path.Combine(directory, "ignored.txt")), "Unmapped archive content must not be installed.");
                using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "receipt.json")));
                Require(receipt.RootElement.GetProperty("Schema").GetInt32() == 4, "Combined archives need a schema 4 receipt.");
                Require(receipt.RootElement.GetProperty("Definition").GetProperty("Archives").GetArrayLength() == 2,
                    "The receipt must retain every archive and its mapping.");
                Require(receipt.RootElement.GetProperty("ArchiveHash").GetString() != fixture.Recipe.Archives[0].Sha256,
                    "The installed identity must cover the combination, not only the first archive.");
                Require(fixture.Requests.Count == 2, "Each archive must be fetched once, with no GitHub lookup.");
            }
            fixture.Offline = true;
            await using var reloaded = Create(path, http);
            using (var scope = Scope(reloaded))
            using (var lease = await scope.TryRequestAsync(Key))
                Require(lease?.Directory == directory, "A saved recipe must remain usable without plugin declarations or network access.");
            Require(fixture.Requests.Count == 2, "Offline reuse must not resolve or download again.");
            await reloaded.RemoveAsync(Key);
            Require(!Directory.Exists(directory), "An unreferenced and unused combined runtime must be removable.");
        });

        foreach (var failure in new[] { "http", "hash", "cancel" })
            Test("a second-archive " + failure + " failure never publishes a partial runtime", async path =>
            {
                var fixture = new Fixture(); using var http = new HttpClient(fixture);
                await using var service = Create(path, http, fixture.Recipe);
                fixture.OnRequest = (url, token) =>
                {
                    if (url != fixture.Recipe.Archives[1].Url) return;
                    if (failure == "http") throw new HttpRequestException("Fixture second archive unavailable.", null, HttpStatusCode.ServiceUnavailable);
                    if (failure == "hash") fixture.Content[url] = Encoding.UTF8.GetBytes("wrong archive");
                    if (failure == "cancel") { service.CancelInstall(Key); token.ThrowIfCancellationRequested(); }
                };
                using var scope = Scope(service);
                if (failure == "http") await Throws<HttpRequestException>(() => scope.TryRequestAsync(Key));
                else if (failure == "hash") await Throws<InvalidDataException>(() => scope.TryRequestAsync(Key));
                else await Throws<OperationCanceledException>(() => scope.TryRequestAsync(Key));
                Require(!Directory.Exists(service.GetStatus(Key).Directory), "Failure must leave no published package directory.");
                Require(service.GetStatus(Key).Users.Count == 0, "Failure must release the lease reservation.");
            });

        Test("a download timeout identifies its archive without being reported as caller cancellation", async path =>
        {
            var fixture = new Fixture(); using var http = new HttpClient(fixture);
            var timedOutUrl = fixture.Recipe.Archives[1].Url;
            fixture.OnRequest = (url, token) =>
            {
                if (url != timedOutUrl) return;
                Require(!token.IsCancellationRequested, "The fixture must simulate an HTTP timeout, not caller cancellation.");
                throw new OperationCanceledException("Simulated HTTP client timeout.");
            };
            await using var service = Create(path, http, fixture.Recipe);
            using var scope = Scope(service);
            try
            {
                using var lease = await scope.TryRequestAsync(Key);
                throw new Exception("Expected TimeoutException.");
            }
            catch (TimeoutException ex)
            {
                Require(ex.Message.Contains(timedOutUrl.AbsoluteUri, StringComparison.Ordinal),
                    "A download timeout must identify the failing upstream archive.");
            }
            var status = service.GetStatus(Key);
            Require(status.Phase == PackagePhase.Failed && !Directory.Exists(status.Directory) && status.Users.Count == 0,
                "A timeout must fail the request, release its reservation and leave no partial installation.");
        });

        Test("repair after plugin removal retains the prior runtime on download failure and can retry", async path =>
        {
            var fixture = new Fixture(); using var http = new HttpClient(fixture);
            string directory;
            await using (var installed = Create(path, http, fixture.Recipe))
            {
                using var scope = Scope(installed); using var lease = await scope.TryRequestAsync(Key);
                directory = lease!.Directory;
            }
            var receiptPath = Path.Combine(directory, "receipt.json");
            var receipt = File.ReadAllText(receiptPath);
            var dllHash = Hash(File.ReadAllBytes(Path.Combine(directory, "bin", "runtime.dll")));
            await using var reloaded = Create(path, http);
            var ready = reloaded.GetPackages().Single(p => p.Package == Key);
            Require(ready.Status.Phase == PackagePhase.Ready && ready.CanInstall && ready.RequiredBy.Count == 0,
                "An unused runtime must expose an integrity-check action even after its plugin is removed.");
            var requestsBeforeCheck = fixture.Requests.Count;
            using (var checkedLease = await reloaded.TryRequestAsync(Key, "Settings"))
            {
                Require(checkedLease is not null && !reloaded.GetPackages().Single(p => p.Package == Key).CanInstall,
                    "The integrity check must hold a lease and block maintenance while it is in use.");
            }
            Require(fixture.Requests.Count == requestsBeforeCheck, "Checking an intact runtime must not download it again.");
            File.WriteAllText(Resource(directory), "changed!");
            await Throws<InvalidDataException>(() => reloaded.TryRequestAsync(Key, "Settings"));
            var damaged = reloaded.GetPackages().Single(p => p.Package == Key);
            Require(damaged.Status.Phase == PackagePhase.Failed && damaged.CanInstall,
                "A failed integrity check must leave a reachable repair action without a plugin declaration.");
            fixture.OnRequest = (url, _) => { if (url == fixture.Recipe.Archives[1].Url) throw new HttpRequestException("Fixture retryable failure."); };
            await Throws<HttpRequestException>(() => reloaded.TryRequestAsync(Key, "Settings"));
            Require(File.ReadAllText(receiptPath) == receipt && File.ReadAllText(Resource(directory)) == "changed!" &&
                Hash(File.ReadAllBytes(Path.Combine(directory, "bin", "runtime.dll"))) == dllHash,
                "Failed repair must preserve the previous receipt and all prior payload files.");
            fixture.OnRequest = null;
            using var leaseAfterRetry = await reloaded.TryRequestAsync(Key, "Settings");
            Require(leaseAfterRetry is not null && File.ReadAllText(Resource(directory)) == "original",
                "Repair must work from the saved recipe after a retry, without the original plugin declaration.");
        });

        Test("a missing mapped entry fails before the assembled runtime becomes available", async path =>
        {
            var fixture = new Fixture();
            fixture.ReplaceArchive(1, NativePackageChecks.Zip([("unrelated.dat", Encoding.UTF8.GetBytes("unrelated"))]));
            using var http = new HttpClient(fixture);
            await using var service = Create(path, http, fixture.Recipe);
            using var scope = Scope(service);
            await Throws<InvalidDataException>(() => scope.TryRequestAsync(Key));
            Require(!Directory.Exists(service.GetStatus(Key).Directory), "A missing mapped resource must not produce a partial package.");
        });

        foreach (var unsafeEntry in new[] { "traversal", "link", "case", "file-directory" })
            Test("rejects an unsafe unmapped archive entry: " + unsafeEntry, async path =>
            {
                var fixture = new Fixture();
                var files = new List<(string, byte[])> { (ResourceEntry, Encoding.UTF8.GetBytes("original")) };
                if (unsafeEntry == "traversal") files.Add(("../../escape.txt", [1]));
                else if (unsafeEntry == "link") files.Add(("ignored-link", [1]));
                else if (unsafeEntry == "case") { files.Add(("ignored/a.txt", [1])); files.Add(("Ignored/b.txt", [1])); }
                else { files.Add(("ignored", [1])); files.Add(("ignored/child.txt", [1])); }
                var bytes = NativePackageChecks.Zip(files.ToArray());
                if (unsafeEntry == "link")
                {
                    using var stream = new MemoryStream(); stream.Write(bytes); stream.Position = 0;
                    using (var archive = new ZipArchive(stream, ZipArchiveMode.Update, true))
                        archive.GetEntry("ignored-link")!.ExternalAttributes = unchecked((int)0xA1FF0000);
                    bytes = stream.ToArray();
                }
                fixture.ReplaceArchive(1, bytes);
                using var http = new HttpClient(fixture);
                await using var service = Create(path, http, fixture.Recipe);
                using var scope = Scope(service);
                await Throws<InvalidDataException>(() => scope.TryRequestAsync(Key));
                Require(!Directory.Exists(service.GetStatus(Key).Directory), "Unselected entries must still obey archive safety rules.");
                Require(!File.Exists(Path.Combine(path, "escape.txt")), "Unsafe entries must never write outside staging.");
            });

        Test("expanded-size limits apply across archives including unmapped entries", async path =>
        {
            var fixture = new Fixture();
            fixture.ReplaceArchive(0, WithDeclaredExpandedSize(NativePackageChecks.Zip(
                [(NativeEntry, NativePackageChecks.NativePe()), ("ignored.bin", [])]), "ignored.bin", 600 * 1024 * 1024));
            fixture.ReplaceArchive(1, WithDeclaredExpandedSize(NativePackageChecks.Zip(
                [(ResourceEntry, Encoding.UTF8.GetBytes("original")), ("ignored.bin", [])]), "ignored.bin", 600 * 1024 * 1024));
            using var http = new HttpClient(fixture);
            await using var service = Create(path, http, fixture.Recipe);
            using var scope = Scope(service);
            await Throws<InvalidDataException>(() => scope.TryRequestAsync(Key));
            Require(!Directory.Exists(service.GetStatus(Key).Directory), "The total expanded-size budget cannot reset for each archive.");
        });

        Test("entry-count limits apply to the entire archive combination", async path =>
        {
            var fixture = new Fixture();
            var ignored = Enumerable.Range(0, 5000).Select(i => ($"ignored/{i}.txt", Array.Empty<byte>())).ToArray();
            fixture.ReplaceArchive(0, NativePackageChecks.Zip([(NativeEntry, NativePackageChecks.NativePe()), .. ignored]));
            fixture.ReplaceArchive(1, NativePackageChecks.Zip([(ResourceEntry, Encoding.UTF8.GetBytes("original")), .. ignored]));
            using var http = new HttpClient(fixture);
            await using var service = Create(path, http, fixture.Recipe);
            using var scope = Scope(service);
            await Throws<InvalidDataException>(() => scope.TryRequestAsync(Key));
            Require(!Directory.Exists(service.GetStatus(Key).Directory), "The 10000-entry limit cannot reset for each archive.");
        });

        Test("invalid mappings and source addresses are rejected before network access", async path =>
        {
            var fixture = new Fixture(); using var http = new HttpClient(fixture);
            await using var service = Create(path, http);
            foreach (var target in new[] { "bin/runtime.dll", "BIN/resource.dat", "bin/runtime.dll/child", "receipt.json", ".package-files.json/child", "../escape", "data\\runtime.dat" })
            {
                var recipe = fixture.Recipe with { Archives = [fixture.Recipe.Archives[0], fixture.Recipe.Archives[1] with
                    { Files = new Dictionary<string, string> { [ResourceEntry] = target } }] };
                Throws<InvalidDataException>(() => Declare(service, recipe));
            }
            foreach (var address in new[] { "http://files.pythonhosted.org/runtime.whl", "https://other.example/runtime.whl",
                "https://files.pythonhosted.org/runtime.whl?token=secret", "https://user@files.pythonhosted.org/runtime.whl" })
            {
                var recipe = fixture.Recipe with { Archives = [fixture.Recipe.Archives[0] with { Url = new Uri(address) }, fixture.Recipe.Archives[1]] };
                Throws<InvalidDataException>(() => Declare(service, recipe));
            }
            Require(fixture.Requests.Count == 0, "Invalid declarations must fail locally.");
        });

        Test("a saved runtime remains usable offline but a removed source blocks repair before consent", async path =>
        {
            var fixture = new Fixture(https: true); using var http = new HttpClient(fixture);
            string directory;
            await using (var service = Create(path, http, fixture.Recipe))
            {
                using var scope = Scope(service);
                await Throws<InvalidOperationException>(() => scope.TryRequestAsync(Key));
                Require(fixture.Requests.Count == 0, "Unconfigured HTTPS origins must not be fetched.");
                service.AddSource("Fixture upstream", fixture.Recipe.Source, DependencySourceKind.Https);
                using (var lease = await scope.TryRequestAsync(Key)) directory = lease!.Directory;
                service.RemoveSource(service.GetSources().Single(s => s.Kind == DependencySourceKind.Https));
            }
            fixture.Offline = true;
            await using var reloaded = Create(path, http);
            Require(reloaded.GetPackages().Single(p => p.Package == Key).RequiredBy.Count == 0,
                "This request must use only the saved receipt after the plugin has been removed.");
            using (var lease = await reloaded.TryRequestAsync(Key, "Settings"))
                Require(lease is not null, "Removing a source must not disable an intact installation.");
            File.WriteAllText(Resource(directory), "changed!");
            await Throws<InvalidDataException>(() => reloaded.TryRequestAsync(Key, "Settings"));
            Require(reloaded.GetStatus(Key).Phase == PackagePhase.Failed, "Damage must mark the saved runtime as failed before repair.");
            var calls = fixture.Requests.Count;
            var prompts = 0;
            reloaded.ConfirmInstallAsync = (_, _) => { prompts++; return Task.FromResult(true); };
            await Throws<InvalidOperationException>(() => reloaded.TryRequestAsync(Key, "Settings"));
            Require(prompts == 0 && fixture.Requests.Count == calls, "A removed source must be rejected before confirmation or networking.");
            reloaded.AddSource("Fixture upstream", fixture.Recipe.Source, DependencySourceKind.Https);
            fixture.Offline = false;
            using var repaired = await reloaded.TryRequestAsync(Key, "Settings");
            Require(prompts == 1 && repaired is not null && File.ReadAllText(Resource(directory)) == "original",
                "Re-adding the exact origin must allow a confirmed repair from the saved recipe.");
        });

        Test("source policy is checked again after the installation prompt", async path =>
        {
            var fixture = new Fixture(https: true); using var http = new HttpClient(fixture);
            await using var service = Create(path, http, fixture.Recipe);
            service.AddSource("Fixture upstream", fixture.Recipe.Source, DependencySourceKind.Https);
            service.ConfirmInstallAsync = (_, _) =>
            {
                service.RemoveSource(service.GetSources().Single(s => s.Kind == DependencySourceKind.Https));
                return Task.FromResult(true);
            };
            using var scope = Scope(service);
            await Throws<InvalidOperationException>(() => scope.TryRequestAsync(Key));
            Require(fixture.Requests.Count == 0, "Consent must not bypass a source removed while the dialog was open.");
        });

        Test("changing a secondary archive or mapping conflicts with an installed identity", async path =>
        {
            var fixture = new Fixture(); using var http = new HttpClient(fixture);
            await using var service = Create(path, http, fixture.Recipe);
            using var scope = Scope(service);
            string directory;
            using (var lease = await scope.TryRequestAsync(Key)) directory = lease!.Directory;
            var calls = fixture.Requests.Count;
            foreach (var secondary in new[]
            {
                fixture.Recipe.Archives[1] with { Sha256 = new string('f', 64) },
                fixture.Recipe.Archives[1] with { Files = new Dictionary<string, string> { [ResourceEntry] = "data/renamed.dat" } }
            })
            {
                Declare(service, fixture.Recipe with { Archives = [fixture.Recipe.Archives[0], secondary] });
                await Throws<InvalidDataException>(() => scope.TryRequestAsync(Key));
            }
            Require(fixture.Requests.Count == calls && File.ReadAllText(Resource(directory)) == "original",
                "A changed secondary artifact must require a new package identity and leave installed files untouched.");
        });

        Test("reference snapshots own archive lists and mapping dictionaries", async path =>
        {
            var fixture = new Fixture(); using var http = new HttpClient(fixture);
            var mappings = new Dictionary<string, string>(fixture.Recipe.Archives[0].Files);
            var archives = fixture.Recipe.Archives.ToArray(); archives[0] = archives[0] with { Files = mappings };
            await using var service = Create(path, http, fixture.Recipe with { Archives = archives });
            mappings[NativeEntry] = "bin/mutated.dll";
            archives[1] = archives[1] with { Sha256 = new string('f', 64) };
            using var scope = Scope(service); using var lease = await scope.TryRequestAsync(Key);
            Require(lease is not null && File.Exists(lease.Libraries["runtime"]) && File.ReadAllText(Resource(lease.Directory)) == "original",
                "Mutating the publisher's collections must not alter the accepted installation recipe.");
        });

        Test("archive manifests require API 1.11 and retain the old single-ZIP API floor", path =>
        {
            var fixture = new Fixture();
            var manifest = new PluginManifest { SchemaVersion = 1, Id = "Test.NativeArchives", Version = "1.0.0", Name = "Native archives",
                EntryAssembly = "Test.dll", EntryType = "Test.Plugin", ApiVersion = "1.11", UiApiVersion = "1.0", AvaloniaVersion = "12.1.6",
                SupportedPlatforms = ["windows"], Dependencies = [new(Key.Id, Key.Version) { Downloads = [fixture.Recipe] }] };
            var directory = Path.Combine(path, manifest.Id, manifest.Version);
            var host = new HostCompatibility("windows", "win-x64", "1.0", "12.1.6");
            ManifestValidator.Validate(manifest, directory);
            Require(host.Check(manifest) is null, "API 1.11 must accept archive recipes.");
            var json = JsonSerializer.SerializeToNode(fixture.Recipe, JsonOptions.Value)!.AsObject();
            foreach (var field in new[] { "repository", "tag", "asset", "sha256", "tools" }) json.Remove(field);
            var parsed = JsonSerializer.Deserialize<PackageDownload>(json.ToJsonString(), JsonOptions.Value)!;
            ManifestValidator.Validate(manifest with { Dependencies = [new(Key.Id, Key.Version) { Downloads = [parsed] }] }, directory);
            foreach (var api in new[] { "1.8", "1.9", "1.10" })
            {
                Require(host.Check(manifest with { ApiVersion = api }) is not null, "Older APIs must reject archive recipes.");
                Throws<InvalidDataException>(() => ManifestValidator.Validate(manifest with { ApiVersion = api }, directory));
            }
            var github = fixture.Recipe with { Archives = [], SourceKind = DependencySourceKind.GitHub, Source = DependencySource.GitHubUrl,
                Repository = "fixture/runtime", Tag = "v1.0.0", Asset = "runtime.zip", Sha256 = new string('a', 64) };
            var legacy = manifest with { ApiVersion = "1.8", Dependencies = [new(Key.Id, Key.Version) { Downloads = [github] }] };
            ManifestValidator.Validate(legacy, directory);
            Require(host.Check(legacy) is null, "Existing single-ZIP native plugins must keep API 1.8 compatibility.");
            return Task.CompletedTask;
        });

        Test("tampering with a stored combination cannot authorize reuse or repair", async path =>
        {
            var fixture = new Fixture(); using var http = new HttpClient(fixture);
            string directory;
            await using (var service = Create(path, http, fixture.Recipe))
            {
                using var scope = Scope(service); using var lease = await scope.TryRequestAsync(Key); directory = lease!.Directory;
            }
            var receiptPath = Path.Combine(directory, "receipt.json");
            var receipt = JsonNode.Parse(File.ReadAllText(receiptPath))!;
            receipt["Definition"]!["Archives"]![1]!["Files"]![ResourceEntry] = "data/changed.dat";
            File.WriteAllText(receiptPath, receipt.ToJsonString());
            fixture.Offline = true;
            await using var reloaded = Create(path, http, fixture.Recipe);
            using var restoredScope = Scope(reloaded);
            var calls = fixture.Requests.Count;
            await Throws<InvalidDataException>(() => restoredScope.TryRequestAsync(Key));
            Require(fixture.Requests.Count == calls && File.ReadAllText(Resource(directory)) == "original",
                "A mismatched saved recipe must preserve the directory and fail before downloading.");
        });

        Test("duplicate JSON mapping keys are rejected during discovery and ZIP installation", path =>
        {
            var fixture = new Fixture();
            var manifest = new PluginManifest { SchemaVersion = 1, Id = "Test.NativeArchives", Version = "1.0.0", Name = "Native archives",
                EntryAssembly = "Test.dll", EntryType = "Test.Plugin", ApiVersion = "1.11", UiApiVersion = "1.0", AvaloniaVersion = "12.1.6",
                SupportedPlatforms = ["windows"], Dependencies = [new(Key.Id, Key.Version) { Downloads = [fixture.Recipe] }] };
            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonOptions.Value) { WriteIndented = false });
            var mapping = JsonSerializer.Serialize(NativeEntry) + ":" + JsonSerializer.Serialize("bin/runtime.dll");
            Require(json.Contains(mapping, StringComparison.Ordinal), "The fixture must contain the mapping to duplicate.");
            json = json.Replace(mapping, mapping + "," + mapping, StringComparison.Ordinal);
            var directory = Path.Combine(path, "unpacked", manifest.Id, manifest.Version);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "plugin.json"), json);
            Throws<JsonException>(() => ManifestValidator.Read(directory));

            var archivePath = Path.Combine(path, "duplicate.zip");
            File.WriteAllBytes(archivePath, NativePackageChecks.Zip([("plugin.json", Encoding.UTF8.GetBytes(json)),
                ("Test.dll", File.ReadAllBytes(typeof(IPlugin).Assembly.Location))]));
            var paths = new AppPaths(Path.Combine(path, "app"), Path.Combine(path, "profile"));
            paths.EnsureDataDirectories();
            using var manager = new PluginManager(paths, new HostCompatibility("windows", "win-x64", "1.0", "12.1.6"));
            Throws<JsonException>(() => manager.QueueInstall(archivePath, []));
            Require(manager.Snapshot.Count == 0, "An ambiguous manifest must not queue a plugin installation.");
            return Task.CompletedTask;
        });
    }

    private static ExternalPackageService Create(string path, HttpClient http, PackageDownload? recipe = null)
    {
        var service = new ExternalPackageService(path, "win-x64", http, new NoProcesses(), new Log());
        service.UpdateReferences(new([], true));
        if (recipe is not null) Declare(service, recipe);
        service.ConfirmInstallAsync = (_, _) => Task.FromResult(true);
        return service;
    }
    private static void Declare(ExternalPackageService service, PackageDownload recipe) =>
        service.UpdateReferences(new([new(Key, "Native.Plugin", "Native", [recipe])], true));
    private static PackageScope Scope(ExternalPackageService service) =>
        service.CreateScope("Native.Plugin", [Key], CancellationToken.None, supportsNativeLibraries: true);
    private static string Resource(string directory) => Path.Combine(directory, "data", "runtime.dat");
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class Fixture : HttpMessageHandler
    {
        public PackageDownload Recipe { get; private set; }
        public Dictionary<Uri, byte[]> Content { get; } = [];
        public List<Uri> Requests { get; } = [];
        public bool Offline { get; set; }
        public Action<Uri, CancellationToken>? OnRequest { get; set; }

        public Fixture(bool https = false)
        {
            var origin = https ? "https://fixture.example/native/" : "https://files.pythonhosted.org/packages/";
            var first = new Uri(origin + "runtime.whl"); var second = new Uri(origin + "components.zip");
            Content[first] = NativePackageChecks.Zip([(NativeEntry, NativePackageChecks.NativePe()), ("ignored.txt", Encoding.UTF8.GetBytes("not installed"))]);
            Content[second] = NativePackageChecks.Zip([(ResourceEntry, Encoding.UTF8.GetBytes("original"))]);
            Recipe = new("win-x64", "", "", "", "", "Native archive fixture", "Never executed", "Fixture", new Dictionary<string, PackageTool>())
            {
                Kind = PackageKind.NativeLibrary, SourceKind = https ? DependencySourceKind.Https : DependencySourceKind.PythonIndex,
                Source = https ? origin : DependencySource.PyPiUrl,
                Libraries = new Dictionary<string, string> { ["runtime"] = "bin/runtime.dll" },
                Archives = [new(first, Hash(Content[first]), new Dictionary<string, string> { [NativeEntry] = "bin/runtime.dll" }),
                    new(second, Hash(Content[second]), new Dictionary<string, string> { [ResourceEntry] = "data/runtime.dat" })]
            };
        }
        public void ReplaceArchive(int index, byte[] bytes)
        {
            var archives = Recipe.Archives.ToArray();
            Content[archives[index].Url] = bytes;
            archives[index] = archives[index] with { Sha256 = Hash(bytes) };
            Recipe = Recipe with { Archives = archives };
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var url = request.RequestUri!; Requests.Add(url);
            if (Offline) throw new HttpRequestException("This operation must not access the network.");
            OnRequest?.Invoke(url, token); token.ThrowIfCancellationRequested();
            if (!Content.TryGetValue(url, out var bytes)) throw new HttpRequestException("Unexpected fixture URL: " + url);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) });
        }
    }

    private static byte[] WithDeclaredExpandedSize(byte[] bytes, string name, uint length)
    {
        // Only central-directory metadata changes; no large allocation or decompression is needed to exercise the aggregate limit.
        for (var offset = 0; offset <= bytes.Length - 46; offset++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)) != 0x02014B50) continue;
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 28));
            if (offset + 46 + nameLength > bytes.Length || Encoding.UTF8.GetString(bytes, offset + 46, nameLength) != name) continue;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 24), length);
            return bytes;
        }
        throw new InvalidOperationException("Fixture entry not found: " + name);
    }
    private sealed class NoProcesses : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Native archive installation must never execute a process.");
    }
    private sealed class Log : IAppLog { public void Write(string source, string message, Exception? error = null) { } }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
