using System.Text.Json;
using TractorTool.Core.Logging;
using TractorTool.Core.Plugins;
using TractorTool.Core.Storage;

if (args.FirstOrDefault() == "--process-fixture") return await ProcessChecks.RunFixtureAsync(args[1..]);

var root = Path.Combine(Path.GetTempPath(), "TractorTool-Checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
var failed = 0;
void Check(string name, Action test)
{
    try { test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
void Assert(bool ok) { if (!ok) throw new Exception("Assertion failed"); }
void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
var paths = new AppPaths(Path.Combine(root, "app"), Path.Combine(root, "fixed"), Path.Combine(root, "data"));
Check("AppRoot independent of current directory", () => Assert(paths.PluginRoot == Path.Combine(root, "app", "Plugins")));
Check("Custom data does not relocate plugin code", () => Assert(paths.PackageRoot == Path.Combine(root, "data", "Packages") && paths.BootstrapFile == Path.Combine(root, "fixed", "bootstrap.json")));
Check("Reject relative data path", () => Throws<InvalidDataException>(() => new AppPaths(paths.AppRoot, paths.BootstrapRoot, "relative")));
Check("Reject data/plugin overlap", () => Throws<InvalidDataException>(() => new AppPaths(paths.AppRoot, paths.BootstrapRoot, paths.PluginRoot)));
Check("Directory containment accepts descendants and the directory itself", () =>
{
    Assert(AppPaths.Contains(paths.DataRoot, Path.Combine(paths.DataRoot, "Data", "Plugins")));
    Assert(AppPaths.Contains(paths.DataRoot + Path.DirectorySeparatorChar, paths.DataRoot));
    Assert(!AppPaths.Contains(Path.Combine(paths.DataRoot, "Data"), paths.DataRoot));
});
Check("Directory containment rejects adjacent names with the same prefix", () =>
{
    Assert(!AppPaths.Contains(paths.DataRoot, paths.DataRoot + "-other"));
    Assert(!AppPaths.Contains(paths.DataRoot, Path.Combine(paths.DataRoot + "-other", "Config")));
});
Check("Volume root contains its child directories", () =>
{
    var volumeRoot = Path.GetPathRoot(paths.AppRoot)!;
    Assert(AppPaths.Contains(volumeRoot, paths.PluginRoot));
    Assert(AppPaths.Contains(volumeRoot, volumeRoot));
});
Check("Reject volume root as a data directory", () =>
    Throws<InvalidDataException>(() => new AppPaths(paths.AppRoot, paths.BootstrapRoot, Path.GetPathRoot(paths.AppRoot)!)));
Check("Reject traversal and reserved plugin IDs", () => { foreach (var id in new[] { "../x", "a/b", "CON", "LPT1.x", "trailing." }) Throws<InvalidDataException>(() => paths.GetPluginDataDirectory(id)); });
Check("Create isolated data layout", () => { paths.EnsureDataDirectories(); Assert(Directory.Exists(paths.LogRoot) && Directory.Exists(paths.PackageRoot) && !Directory.Exists(paths.PluginRoot)); });
Check("Bootstrap default and fixed pointer", () =>
{
    var profile = Path.Combine(root, "bootstrap-test");
    var opened = AppPaths.Open(profile);
    Assert(opened.DataRoot == profile && opened.AppRoot == Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
    var custom = Path.Combine(root, "custom"); Directory.CreateDirectory(custom);
    new JsonFileStore<BootstrapSettings>(opened.BootstrapFile).Save(new() { DataRoot = custom });
    var next = AppPaths.Open(profile);
    Assert(next.DataRoot == custom && next.BootstrapFile == opened.BootstrapFile && next.PluginRoot == opened.PluginRoot);
});
Check("Unavailable custom root never creates empty replacement", () =>
{
    var profile = Path.Combine(root, "missing-root"); Directory.CreateDirectory(profile);
    var missing = Path.Combine(root, "must-not-create");
    new JsonFileStore<BootstrapSettings>(Path.Combine(profile, "bootstrap.json")).Save(new() { DataRoot = missing });
    Throws<DirectoryNotFoundException>(() => AppPaths.Open(profile)); Assert(!Directory.Exists(missing));
});
var file = Path.Combine(paths.ConfigRoot, "settings.json");
var store = new JsonFileStore<HostSettings>(file, HostSettings.Validate);
Check("Settings defaults", () => Assert(store.Load().Theme == "System"));
Check("Settings roundtrip", () => { store.Save(new() { Theme = "Dark", Accent = "#0F766E" }); Assert(store.Load().Theme == "Dark"); });
Check("Atomic replacement keeps previous backup", () => { store.Save(new() { Theme = "Light" }); Assert(new JsonFileStore<HostSettings>(file + ".bak").Load().Theme == "Dark"); Assert(!Directory.GetFiles(paths.ConfigRoot, "*.tmp").Any()); });
Check("Invalid settings rejected before overwrite", () => { var before = File.ReadAllText(file); Throws<InvalidDataException>(() => store.Save(new() { Theme = "no" })); Assert(before == File.ReadAllText(file)); });
Check("Concurrent settings writes remain valid JSON", () => { Parallel.For(0, 25, i => new JsonFileStore<HostSettings>(file).Save(new() { Theme = i % 2 == 0 ? "Dark" : "Light" })); Assert(store.Load().SchemaVersion == 1); });
Check("Corrupt settings preserved", () => { File.WriteAllText(file, "{bad"); Throws<JsonException>(() => store.Load()); Assert(File.ReadAllText(file) == "{bad"); });
Check("Unknown settings schema rejected", () => { File.WriteAllText(file, "{\"schemaVersion\":99}"); Throws<InvalidDataException>(() => store.Load()); });
Check("Write failure preserves original settings and cleans temporary file", () =>
{
    store.Save(new() { Theme = "Dark" });
    using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        Throws<IOException>(() => store.Save(new() { Theme = "Light" }));
    Assert(store.Load().Theme == "Dark" && !Directory.GetFiles(paths.ConfigRoot, "*.tmp").Any());
});
Check("Corrupt bootstrap never resets the data pointer", () =>
{
    var profile = Path.Combine(root, "corrupt-bootstrap"); Directory.CreateDirectory(profile);
    var pointer = Path.Combine(profile, "bootstrap.json"); File.WriteAllText(pointer, "{bad");
    Throws<JsonException>(() => AppPaths.Open(profile)); Assert(File.ReadAllText(pointer) == "{bad" && !Directory.Exists(Path.Combine(profile, "Config")));
});
Check("Plugin settings remain ID scoped", () => { var a = new PluginSettingsStore(paths.GetPluginDataDirectory("One")); var b = new PluginSettingsStore(paths.GetPluginDataDirectory("Two")); a.Save("prefs", new HostSettings { Theme = "Dark" }); Assert(b.Load<HostSettings>("prefs").Theme == "System"); Throws<InvalidDataException>(() => a.Load<HostSettings>("../escape")); });
Check("Same profile lock prevents concurrent writers", () => { using var lease = new InstanceLease(paths.BootstrapRoot); Throws<IOException>(() => new InstanceLease(paths.BootstrapRoot)); });
Check("Log memory bounded; file records exceptions", () => { var log = new AppLog(paths.LogRoot); for (int i = 0; i < 350; i++) log.Write("Test", "line:" + i); log.Write("Test", "failure", new Exception("detail")); Assert(!log.ReadRecent().Contains("line:0\r")); Assert(File.ReadAllText(log.FilePath).Contains("detail")); });
var dir = Path.Combine(paths.PluginRoot, "Test.One", "1.0.0"); Directory.CreateDirectory(dir);
var manifest = new PluginManifest { SchemaVersion = 1, Id = "Test.One", Name = "Test", Version = "1.0.0", EntryAssembly = "Test.dll", EntryType = "Test.Plugin", ApiVersion = "1.0", UiApiVersion = "1.0", AvaloniaVersion = "12.1.2", SupportedPlatforms = ["windows"] };
var compatibility = new HostCompatibility("windows", "win-x64", "1.0", "12.1.2");
void WriteManifest(PluginManifest value) => File.WriteAllText(Path.Combine(dir, "plugin.json"), JsonSerializer.Serialize(value, JsonOptions.Value));
Check("Manifest validates real directory layout", () => { WriteManifest(manifest); Assert(ManifestValidator.Read(dir).Id == manifest.Id); });
Check("Manifest ID and folder must match", () => Throws<InvalidDataException>(() => ManifestValidator.Validate(manifest with { Id = "Other" }, dir)));
Check("Reject manifest traversal and absolute DLL paths", () => { foreach (var entry in new[] { "../bad.dll", "sub/../../bad.dll", "C:\\bad.dll", "Test.dll:ads", "/bad.dll" }) Throws<InvalidDataException>(() => ManifestValidator.Validate(manifest with { EntryAssembly = entry }, dir)); });
Check("Package paths reject normalized traversal and adjacent package directories", () =>
{
    Throws<InvalidDataException>(() => ManifestValidator.PackagePath(dir, Path.Combine(dir, "..", "outside.dll")));
    Throws<InvalidDataException>(() => ManifestValidator.PackagePath(dir, Path.Combine(dir + "-other", "private.dll")));
});
Check("Package paths accept ordinary nested dependencies", () =>
{
    var nested = Path.Combine(dir, "runtimes", "win-x64", "native");
    Directory.CreateDirectory(nested);
    var dependency = Path.Combine(nested, "dependency.dll");
    File.WriteAllText(dependency, "Path validation fixture; not an executable DLL.");
    Assert(ManifestValidator.PackagePath(dir, dependency) == Path.GetFullPath(dependency));
    Assert(ManifestValidator.PackagePath(dir, Path.Combine(nested, ".", "dependency.dll")) == Path.GetFullPath(dependency));
});
Check("Reject unknown manifest schema", () => Throws<InvalidDataException>(() => ManifestValidator.Validate(manifest with { SchemaVersion = 2 }, dir)));
Check("Reject null platform/dependency fields", () => { Throws<InvalidDataException>(() => ManifestValidator.Validate(manifest with { SupportedPlatforms = null! }, dir)); Throws<InvalidDataException>(() => ManifestValidator.Validate(manifest with { Dependencies = null! }, dir)); });
Check("Exact SDK baseline compatibility", () => { Assert(compatibility.Check(manifest) is null); Assert(compatibility.Check(manifest with { ApiVersion = "2.0" }) is not null); Assert(compatibility.Check(manifest with { UiApiVersion = "2.0" }) is not null); Assert(compatibility.Check(manifest with { AvaloniaVersion = "11.3.0" }) is not null); });
Check("API 1.4 accepts existing process and legacy plugins", () =>
{
    Assert(compatibility.Check(manifest with { ApiVersion = "1.1" }) is null);
    Assert(compatibility.Check(manifest with { ApiVersion = "1.0" }) is null);
    Assert(compatibility.Check(manifest with { ApiVersion = "1.2" }) is null);
    Assert(compatibility.Check(manifest with { ApiVersion = "1.3" }) is null);
    Assert(compatibility.Check(manifest with { ApiVersion = "1.4" }) is null);
    Assert(compatibility.Check(manifest with { ApiVersion = "1.5" }) is null);
    Assert(compatibility.Check(manifest with { ApiVersion = "1.6" }) is not null);
});
Check("Package dependencies validate shape and API without requiring an available source", () =>
{
    var packageManifest = manifest with { ApiVersion = "1.2", Dependencies = [new("FFmpeg", "9.0.2")] };
    Assert(compatibility.Check(packageManifest) is null);
    Assert(compatibility.Check(packageManifest with { ApiVersion = "1.4" }) is null);
    Assert(compatibility.Check(packageManifest with { ApiVersion = "1.5" }) is null);
    Assert(compatibility.Check(packageManifest with { ApiVersion = "1.1" }) is not null);
    Assert(compatibility.Check(packageManifest with { Dependencies = [new("Unknown", "1.0.0")] }) is null);
    Throws<InvalidDataException>(() => ManifestValidator.Validate(packageManifest with { Dependencies = [new("../escape", "1.0.0")] }, dir));
    Throws<InvalidDataException>(() => ManifestValidator.Validate(packageManifest with { Dependencies = [new("FFmpeg", "9.0.2"), new("FFmpeg", "9.0.2")] }, dir));
});
Check("UI 1.1 preserves legacy pages but old hosts reject new SDK", () => {
    var current = new HostCompatibility("windows", "win-x64", "1.1", "12.1.2");
    Assert(current.Check(manifest) is null);
    Assert(current.Check(manifest with { UiApiVersion = "1.1" }) is null);
    Assert(current.Check(manifest with { UiApiVersion = "1.2" }) is not null);
    Assert(compatibility.Check(manifest with { UiApiVersion = "1.1" }) is not null);
});
Check("UI 1.2 accepts existing SDKs and rejects unknown icon contracts", () => {
    var current = new HostCompatibility("windows", "win-x64", "1.2", "12.1.2");
    foreach (var ui in new[] { "1.0", "1.1", "1.2" }) Assert(current.Check(manifest with { UiApiVersion = ui }) is null);
    Assert(current.Check(manifest with { UiApiVersion = "1.3" }) is not null);
    Assert(new HostCompatibility("windows", "win-x64", "1.1", "12.1.2").Check(manifest with { UiApiVersion = "1.2" }) is not null);
});
Check("Platform compatibility", () => { Assert(compatibility.Check(manifest with { SupportedPlatforms = ["linux"] }) is not null); Assert(compatibility.Check(manifest with { SupportedPlatforms = ["win-x64"] }) is null); });
Check("Missing DLL becomes load failure", () => { WriteManifest(manifest); var host = new PluginHost(paths, compatibility, new AppLog(paths.LogRoot)); host.Discover([]); host.LoadAsync((_, _) => throw new Exception("must not register")).GetAwaiter().GetResult(); Assert(host.Records.Single().State == PluginState.Failed); host.DisposeAsync().AsTask().GetAwaiter().GetResult(); });
Check("Disabled plugin never opens bad DLL", () => { File.WriteAllText(Path.Combine(dir, "Test.dll"), "bad"); var host = new PluginHost(paths, compatibility, new AppLog(paths.LogRoot)); host.Discover([manifest.Id]); host.LoadAsync((_, _) => throw new Exception()).GetAwaiter().GetResult(); Assert(host.Records.Single().State == PluginState.Disabled); });
Check("Incompatible plugin never opens bad DLL", () => { WriteManifest(manifest with { ApiVersion = "2.0" }); var host = new PluginHost(paths, compatibility, new AppLog(paths.LogRoot)); host.Discover([]); host.LoadAsync((_, _) => throw new Exception()).GetAwaiter().GetResult(); Assert(host.Records.Single().State == PluginState.Incompatible); });
Check("Invalid DLL is contained as failed", () => { WriteManifest(manifest); var host = new PluginHost(paths, compatibility, new AppLog(paths.LogRoot)); host.Discover([]); host.LoadAsync((_, _) => throw new Exception()).GetAwaiter().GetResult(); Assert(host.Records.Single().State == PluginState.Failed); });
Check("Duplicate IDs block all candidates", () => { var second = Path.Combine(paths.PluginRoot, manifest.Id, "2.0.0"); Directory.CreateDirectory(second); File.WriteAllText(Path.Combine(second, "plugin.json"), JsonSerializer.Serialize(manifest with { Version = "2.0.0" }, JsonOptions.Value)); var host = new PluginHost(paths, compatibility, new AppLog(paths.LogRoot)); host.Discover([]); Assert(host.Records.All(r => r.State == PluginState.Duplicate)); });
Check("Blocked software plugin directory reports error without relocation", () =>
{
    var blocked = new AppPaths(Path.Combine(root, "blocked-app"), Path.Combine(root, "blocked-data"));
    Directory.CreateDirectory(blocked.AppRoot); blocked.EnsureDataDirectories(); File.WriteAllText(blocked.PluginRoot, "existing file");
    var host = new PluginHost(blocked, compatibility, new AppLog(blocked.LogRoot)); host.Discover([]);
    Assert(host.DirectoryError is not null && host.DirectoryError.Contains(blocked.PluginRoot));
    Assert(File.ReadAllText(blocked.PluginRoot) == "existing file" && !Directory.Exists(Path.Combine(blocked.DataRoot, "Plugins")));
    // Only rename the synthetic obstruction created by this check.
    File.Move(blocked.PluginRoot, blocked.PluginRoot + ".fixture");
    host.Discover([]);
    Assert(host.DirectoryError is null && Directory.Exists(blocked.PluginRoot));
});
PluginManagementChecks.Run(root, Check);
ProcessChecks.Run(root, Check);
PackageChecks.Run(root, Check);
Console.WriteLine($"RESULT passed={passed} failed={failed}; isolated evidence={root}");
return failed == 0 ? 0 : 1;
