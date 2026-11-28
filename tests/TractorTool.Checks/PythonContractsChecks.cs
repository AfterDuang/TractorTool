using System.Security.Cryptography;
using System.Text;
using TractorTool.Contracts;
using TractorTool.Contracts.Packages;
using TractorTool.Contracts.Processes;
using TractorTool.Core.Packages;
using TractorTool.Core.Packages.Python;
using TractorTool.Core.Plugins;

internal static class PythonContractsChecks
{
    public static void Run(string root, Action<string, Action> check)
    {
        void Test(string name, Func<string, Task> action) => check("Python contracts: " + name, () =>
            action(Path.Combine(root, "python-contracts-" + Guid.NewGuid().ToString("N"))).GetAwaiter().GetResult());

        Test("new API retains native and legacy manifests", path =>
        {
            var (directory, declaration, manifest) = Fixture(path);
            var native = new PackageDownload("win-x64", "example/runtime", "v1.0.0", "runtime.zip", new string('a', 64),
                "Runtime", "Fixture", "MIT", new Dictionary<string, PackageTool>())
            { Kind = PackageKind.NativeLibrary, Libraries = new Dictionary<string, string> { ["runtime"] = "runtime.dll" } };
            var compatible = new HostCompatibility("windows", "win-x64", "1.6", "12.1.6");
            foreach (var api in new[] { "1.8", "1.9", "1.10" })
            {
                var withNative = manifest with { ApiVersion = api, PythonEnvironments = [], Dependencies = [new("Fixture.Runtime", "1.0.0") { Downloads = [native] }] };
                ManifestValidator.Validate(withNative, directory);
                Require(compatible.Check(withNative) is null);
            }
            Require(compatible.Check(manifest) is null);
            Require(compatible.Check(manifest with { ApiVersion = "1.8" }) is not null);
            Throws<InvalidDataException>(() => ManifestValidator.Validate(manifest with { ApiVersion = "1.8" }, directory));
            Throws<InvalidDataException>(() => ManifestValidator.Validate(manifest with { PythonEnvironments = [declaration, declaration with { Id = "WORKER" }] }, directory));
            Throws<InvalidDataException>(() => ManifestValidator.Validate(manifest with { PythonEnvironments = null! }, directory));
            return Task.CompletedTask;
        });

        Test("project and lock snapshots bind identity and preserve original inputs", path =>
        {
            var (directory, declaration, manifest) = Fixture(path);
            PythonEnvironmentDefinition Read(PythonEnvironmentDeclaration item, string version = "1.0.0") =>
                PythonEnvironmentDefinitions.Snapshot(item, manifest.Id, version, manifest.Name, directory, "win-x64");
            var snapshot = Read(declaration);
            Require(snapshot.ProjectText == File.ReadAllText(Path.Combine(directory, declaration.Project)));
            Require(snapshot.LockText == File.ReadAllText(Path.Combine(directory, declaration.Lock)));
            Require(Read(declaration).Identity == snapshot.Identity);
            Require(Read(declaration, "1.0.1").Identity != snapshot.Identity);
            File.AppendAllText(Path.Combine(directory, declaration.Lock), "\n# changed");
            Throws<InvalidDataException>(() => Read(declaration));
            var changed = declaration with { LockSha256 = HashFile(Path.Combine(directory, declaration.Lock)) };
            Require(Read(changed).Identity != snapshot.Identity && !snapshot.LockText.Contains("changed", StringComparison.Ordinal));
            return Task.CompletedTask;
        });

        Test("unsafe paths, credentials and unsupported versions fail before execution", path =>
        {
            var (_, declaration, _) = Fixture(path);
            foreach (var candidate in new[]
            {
                declaration with { Id = "../escape" },
                declaration with { Project = "../pyproject.toml" },
                declaration with { Lock = "Elsewhere/uv.lock" },
                declaration with { Lock = "Python/UV.LOCK" },
                declaration with { ProjectSha256 = "short" },
                declaration with { PythonVersion = "3.13.17" },
                declaration with { Source = "http://pypi.org/simple/" },
                declaration with { Source = "https://user:secret@pypi.org/simple/" },
                declaration with { Source = "https://pypi.org/simple/?token=secret" },
                declaration with { Source = "https://packages.example.com/" + new string('a', 2050) + "/simple/" }
            }) Throws<InvalidDataException>(() => PythonEnvironmentDefinitions.ValidateDeclaration(candidate));
            return Task.CompletedTask;
        });

        Test("missing and oversized inputs are rejected without fallback", path =>
        {
            var (directory, declaration, manifest) = Fixture(path);
            PythonEnvironmentDefinition Read() => PythonEnvironmentDefinitions.Snapshot(declaration, manifest.Id, manifest.Version,
                manifest.Name, directory, "win-x64");
            File.Delete(Path.Combine(directory, declaration.Lock));
            Throws<FileNotFoundException>(() => Read());
            File.WriteAllBytes(Path.Combine(directory, declaration.Lock), new byte[2 * 1024 * 1024 + 1]);
            Throws<InvalidDataException>(() => Read());
            return Task.CompletedTask;
        });

        Test("oversized plugin name fails during input snapshot", path =>
        {
            var (directory, declaration, manifest) = Fixture(path);
            Throws<InvalidDataException>(() => PythonEnvironmentDefinitions.Snapshot(declaration, manifest.Id, manifest.Version,
                new string('n', 257), directory, "win-x64"));
            return Task.CompletedTask;
        });

        Test("old GitHub sources retain type and gain a separate default PyPI source", async path =>
        {
            Directory.CreateDirectory(path);
            var settings = Path.Combine(path, "sources.json");
            File.WriteAllText(settings, """{"schemaVersion":1,"sources":[{"name":"Enterprise","url":"https://git.example.com/api/v3/"}]}""");
            using var http = new HttpClient(new NoNetwork());
            await using (var service = Create(path, http, settings))
            {
                var sources = service.GetSources();
                Require(sources.Count == 4 && sources.Count(s => s.IsDefault) == 3);
                Require(sources.Single(s => s.Name == "Enterprise").Kind == DependencySourceKind.GitHub);
                Require(sources.Single(s => s.Url == DependencySource.PyPiUrl).Kind == DependencySourceKind.PythonIndex);
                service.AddSource("Mirror", "https://packages.example.com/simple/", DependencySourceKind.PythonIndex);
                Throws<InvalidDataException>(() => service.AddSource("WrongKind", "https://pypi.org/simple/"));
                Throws<InvalidDataException>(() => service.AddSource("WrongKind", "https://api.github.com/", DependencySourceKind.PythonIndex));
                Throws<InvalidOperationException>(() => service.RemoveSource(sources.Single(s => s.Url == DependencySource.PyPiUrl)));
            }
            await using var restored = Create(path, http, settings);
            Require(restored.GetSources().Single(s => s.Name == "Mirror").Kind == DependencySourceKind.PythonIndex);
        });

        Test("invalid source settings stay intact and cannot be overwritten", async path =>
        {
            Directory.CreateDirectory(path);
            var settings = Path.Combine(path, "sources.json");
            const string original = """{"schemaVersion":1,"sources":[{"name":"Broken","url":"https://packages.example.com/simple/","kind":999}]}""";
            File.WriteAllText(settings, original);
            using var http = new HttpClient(new NoNetwork());
            await using var service = Create(path, http, settings);
            Require(service.SourceError.Length != 0);
            Throws<InvalidOperationException>(() => service.AddSource("Valid", "https://packages.example.com/simple/", DependencySourceKind.PythonIndex));
            Require(File.ReadAllText(settings) == original);
        });

        Test("legacy GitHub source named PyPI remains readable and editable", async path =>
        {
            Directory.CreateDirectory(path);
            var settings = Path.Combine(path, "sources.json");
            const string original = """{"schemaVersion":1,"sources":[{"name":"PyPI","url":"https://git.example.com/api/v3/"}]}""";
            File.WriteAllText(settings, original);
            using var http = new HttpClient(new NoNetwork());
            await using var service = Create(path, http, settings);
            Require(service.SourceError.Length == 0 && File.ReadAllText(settings) == original);
            var named = service.GetSources().Where(s => s.Name == "PyPI").ToArray();
            Require(named.Length == 2 && named.Single(s => s.Kind == DependencySourceKind.GitHub).Url == "https://git.example.com/api/v3/");
            service.AddSource("New mirror", "https://packages.example.com/simple/", DependencySourceKind.PythonIndex);
            Require(service.GetSources().Any(s => s.Kind == DependencySourceKind.GitHub && s.Name == "PyPI"));
        });

        Test("undeclared Python cannot use another plugin's environment", async path =>
        {
            var (directory, declaration, manifest) = Fixture(path);
            var definition = PythonEnvironmentDefinitions.Snapshot(declaration, manifest.Id, manifest.Version, manifest.Name, directory, "win-x64");
            using var http = new HttpClient(new NoNetwork());
            await using var service = Create(path, http);
            service.UpdateReferences(new([], true) { PythonEnvironments = [definition] });
            using var legacy = service.CreateScope("Legacy.Plugin", [], CancellationToken.None);
            await Throws<InvalidOperationException>(() => legacy.TryRequestPythonAsync("worker"));
            using var other = service.CreateScope("Other.Plugin", [], CancellationToken.None, supportsNativeLibraries: true);
            await Throws<InvalidOperationException>(() => other.TryRequestPythonAsync("worker"));
        });
    }

    private static (string Directory, PythonEnvironmentDeclaration Declaration, PluginManifest Manifest) Fixture(string path)
    {
        var directory = Path.Combine(path, "Fixture.Python", "1.0.0");
        var inputs = Path.Combine(directory, "Python"); Directory.CreateDirectory(inputs);
        File.WriteAllText(Path.Combine(inputs, "pyproject.toml"), "[project]\nname = \"fixture\"\nversion = \"1.0.0\"\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(inputs, "uv.lock"), "version = 1\nrevision = 5\n", new UTF8Encoding(false));
        var declaration = new PythonEnvironmentDeclaration { Id = "worker", PythonVersion = "3.13.18",
            ProjectSha256 = HashFile(Path.Combine(inputs, "pyproject.toml")), LockSha256 = HashFile(Path.Combine(inputs, "uv.lock")) };
        var manifest = new PluginManifest { SchemaVersion = 1, Id = "Fixture.Python", Name = "Fixture", Version = "1.0.0",
            EntryAssembly = "Fixture.dll", EntryType = "Fixture.Plugin", ApiVersion = "1.9", UiApiVersion = "1.6", AvaloniaVersion = "12.1.6",
            SupportedPlatforms = ["win-x64"], PythonEnvironments = [declaration] };
        return (directory, declaration, manifest);
    }

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static ExternalPackageService Create(string path, HttpClient http, string? sources = null) =>
        new(Path.Combine(path, "Packages"), "win-x64", http, new NoProcesses(), new Log(), sourcesFile: sources);
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new Exception("Contract validation must not access the network.");
    }
    private sealed class NoProcesses : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default) =>
            throw new Exception("Contract validation must not execute processes.");
    }
    private sealed class Log : IAppLog { public void Write(string source, string message, Exception? error = null) { } }
    private static void Require(bool value) { if (!value) throw new Exception("Python contract assertion failed."); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
