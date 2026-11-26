using System.Security.Cryptography;
using System.Text;
using TractorTool.Contracts;
using TractorTool.Contracts.Processes;
using TractorTool.Core.Packages;
using TractorTool.Core.Packages.Node;
using TractorTool.Core.Plugins;

internal static class NodeContractsChecks
{
    public static void Run(string root, Action<string, Action> check)
    {
        void Test(string name, Func<string, Task> action) => check("Node contracts: " + name, () =>
            action(Path.Combine(root, "node-contracts-" + Guid.NewGuid().ToString("N"))).GetAwaiter().GetResult());

        Test("Node requires new API while old manifests remain compatible", path =>
        {
            var (directory, declaration, manifest) = Fixture(path);
            var compatibility = new HostCompatibility("windows", "win-x64", "1.6", "12.1.6");
            ManifestValidator.Validate(manifest, directory);
            Require(compatibility.Check(manifest) is null);
            foreach (var api in new[] { "1.0", "1.8", "1.9" })
            {
                Require(compatibility.Check(manifest with { ApiVersion = api, NodeEnvironments = [] }) is null);
                Require(compatibility.Check(manifest with { ApiVersion = api }) is not null);
                Throws<InvalidDataException>(() => ManifestValidator.Validate(manifest with { ApiVersion = api }, directory));
            }
            Throws<InvalidDataException>(() => ManifestValidator.Validate(manifest with { NodeEnvironments = [declaration, declaration with { Id = "WORKER" }] }, directory));
            Throws<InvalidDataException>(() => ManifestValidator.Validate(manifest with { NodeEnvironments = null! }, directory));
            return Task.CompletedTask;
        });

        Test("snapshots bind exact original inputs and plugin version", path =>
        {
            var (directory, declaration, manifest) = Fixture(path);
            NodeEnvironmentDefinition Snapshot(NodeEnvironmentDeclaration value, string version = "1.0.0") =>
                NodeEnvironmentDefinitions.Snapshot(value, manifest.Id, version, manifest.Name, directory, "win-x64");
            var original = Snapshot(declaration);
            Require(original.Identity == Snapshot(declaration).Identity && original.Identity != Snapshot(declaration, "1.0.1").Identity);
            var lockPath = Path.Combine(directory, declaration.Lock);
            File.AppendAllText(lockPath, "\n# changed");
            Throws<InvalidDataException>(() => Snapshot(declaration));
            Require(Snapshot(declaration with { LockSha256 = Hash(lockPath) }).Identity != original.Identity);
            Require(!original.LockText.Contains("changed", StringComparison.Ordinal));
            foreach (var invalid in new[]
            {
                declaration with { Project = "../package.json" }, declaration with { Lock = "Elsewhere/pnpm-lock.yaml" },
                declaration with { NodeVersion = "24.0.0" }, declaration with { ProjectSha256 = "short" },
                declaration with { Source = "http://registry.npmjs.org/" }, declaration with { Source = "https://user:secret@registry.npmjs.org/" },
                declaration with { Source = "https://registry.npmjs.org/?token=secret" }
            }) Throws<InvalidDataException>(() => NodeEnvironmentDefinitions.ValidateDeclaration(invalid));
            return Task.CompletedTask;
        });

        Test("npm defaults do not invalidate legacy same-name GitHub sources", async path =>
        {
            Directory.CreateDirectory(path);
            var settings = Path.Combine(path, "sources.json");
            const string original = """{"schemaVersion":1,"sources":[{"name":"npm","url":"https://git.example.com/api/v3/"}]}""";
            File.WriteAllText(settings, original);
            using var http = new HttpClient(new NoNetwork());
            await using (var service = Create(path, http, settings))
            {
                Require(service.SourceError.Length == 0 && File.ReadAllText(settings) == original);
                var sources = service.GetSources();
                Require(sources.Count(s => s.IsDefault) == 3);
                Require(sources.Count(s => s.Name == "npm") == 2);
                var npm = sources.Single(s => s.Kind == DependencySourceKind.NpmRegistry);
                Throws<InvalidOperationException>(() => service.RemoveSource(npm));
                service.AddSource("Mirror", "https://packages.example.com/npm", DependencySourceKind.NpmRegistry);
                Throws<InvalidDataException>(() => service.AddSource("Duplicate", "https://packages.example.com/npm/", DependencySourceKind.NpmRegistry));
            }
            await using var restored = Create(path, http, settings);
            Require(restored.GetSources().Single(s => s.Name == "Mirror").Url == "https://packages.example.com/npm/");
        });

        Test("startup source policy normalizes URLs without rewriting legacy configuration", path =>
        {
            Directory.CreateDirectory(path);
            var settings = Path.Combine(path, "sources.json");
            var original = System.Text.Json.JsonSerializer.Serialize(new { schemaVersion = 1, sources = new[]
            {
                new { name = "Node mirror", url = "https://packages.example.com/npm", kind = "npmRegistry" },
                new { name = "Python mirror", url = "https://packages.example.com/simple", kind = "pythonIndex" },
                new { name = "Legacy long index", url = "https://packages.example.com/" + new string('a', 2050) + "/simple", kind = "pythonIndex" }
            } });
            File.WriteAllText(settings, original);
            var sources = ExternalPackageService.ReadConfiguredSources(settings);
            Require(sources.Single(s => s.Name == "Node mirror").Url == "https://packages.example.com/npm/");
            Require(sources.Single(s => s.Name == "Python mirror").Url == "https://packages.example.com/simple/");
            Require(File.ReadAllText(settings) == original);
            return Task.CompletedTask;
        });

        Test("undeclared scopes cannot request Node or use another plugin environment", async path =>
        {
            var (directory, declaration, manifest) = Fixture(path);
            var definition = NodeEnvironmentDefinitions.Snapshot(declaration, manifest.Id, manifest.Version, manifest.Name, directory, "win-x64");
            using var http = new HttpClient(new NoNetwork());
            await using var service = Create(path, http);
            service.UpdateReferences(new([], true) { NodeEnvironments = [definition] });
            using var legacy = service.CreateScope("Legacy.Plugin", [], CancellationToken.None);
            await Throws<InvalidOperationException>(() => legacy.TryRequestNodeAsync("worker"));
        });
    }

    private static (string, NodeEnvironmentDeclaration, PluginManifest) Fixture(string path)
    {
        var directory = Path.Combine(path, "Fixture.Node", "1.0.0");
        var inputs = Path.Combine(directory, "Node"); Directory.CreateDirectory(inputs);
        File.WriteAllText(Path.Combine(inputs, "package.json"), "{\"name\":\"fixture\",\"version\":\"1.0.0\"}\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(inputs, "pnpm-lock.yaml"), "lockfileVersion: '9.0'\n", new UTF8Encoding(false));
        var declaration = new NodeEnvironmentDeclaration { Id = "worker", NodeVersion = NodeEnvironmentDefinitions.SupportedNodeVersion,
            ProjectSha256 = Hash(Path.Combine(inputs, "package.json")), LockSha256 = Hash(Path.Combine(inputs, "pnpm-lock.yaml")) };
        var manifest = new PluginManifest { SchemaVersion = 1, Id = "Fixture.Node", Name = "Fixture", Version = "1.0.0",
            EntryAssembly = "Fixture.dll", EntryType = "Fixture.Plugin", ApiVersion = "1.10", UiApiVersion = "1.6", AvaloniaVersion = "12.1.6",
            SupportedPlatforms = ["win-x64"], NodeEnvironments = [declaration] };
        return (directory, declaration, manifest);
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static ExternalPackageService Create(string path, HttpClient http, string? sources = null) =>
        new(Path.Combine(path, "Packages"), "win-x64", http, new NoProcesses(), new Log(), sourcesFile: sources);
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Contract checks must not access network.");
    }
    private sealed class NoProcesses : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Contract checks must not execute processes.");
    }
    private sealed class Log : IAppLog { public void Write(string source, string message, Exception? error = null) { } }
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("Node contract assertion failed."); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
