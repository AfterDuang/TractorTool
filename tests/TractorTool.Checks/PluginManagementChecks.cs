using System.IO.Compression;
using System.Text.Json;
using TractorTool.Contracts;
using TractorTool.Core.Logging;
using TractorTool.Core.Plugins;
using TractorTool.Core.Storage;

internal static class PluginManagementChecks
{
    public static void Run(string root, Action<string, Action> check)
    {
        var compatibility = new HostCompatibility("windows", "win-x64", "1.2", "12.1.2");
        AppPaths Paths()
        {
            var test = Path.Combine(root, "management-" + Guid.NewGuid().ToString("N"));
            var paths = new AppPaths(Path.Combine(test, "app"), Path.Combine(test, "profile"));
            paths.EnsureDataDirectories();
            return paths;
        }
        PluginManifest Manifest(string version = "1.0.0", string id = "Checks.Management") => new()
        {
            SchemaVersion = 1, Id = id, Name = "Management fixture", Version = version,
            EntryAssembly = "Fixture.dll", EntryType = "NotExecuted",
            ApiVersion = "1.0", UiApiVersion = "1.2", AvaloniaVersion = "12.1.2", SupportedPlatforms = ["windows"]
        };
        // A real managed PE is used for package inspection only. These checks never claim to load a plugin.
        string Zip(AppPaths paths, PluginManifest? manifest = null, Action<ZipArchive>? extra = null, bool includeDll = true)
        {
            var zip = Path.Combine(paths.DataRoot, Guid.NewGuid().ToString("N") + ".zip");
            using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
            using (var file = archive.CreateEntry("plugin.json").Open()) JsonSerializer.Serialize(file, manifest ?? Manifest(), JsonOptions.Value);
            if (includeDll) archive.CreateEntryFromFile(typeof(IPlugin).Assembly.Location, "Fixture.dll");
            extra?.Invoke(archive);
            return zip;
        }
        void Install(PluginManager manager, AppPaths paths)
        {
            Assert(manager.QueueInstall(Zip(paths), []) is null); manager.ApplyPending();
            // State-machine fixture outcome, not a DLL-load integration test.
            manager.RecordLoadResults([new PluginRecord(Path.Combine(paths.PluginRoot, "Checks.Management", "1.0.0"), Manifest(), PluginState.Loaded, "State fixture")]);
        }
        check("Management stages package until next startup", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility);
            Assert(manager.QueueInstall(Zip(paths), []) is null); var entry = manager.Snapshot.Single();
            Assert(entry.ActiveVersion is null && entry.Pending?.Version == "1.0.0");
            Assert(!Directory.Exists(Path.Combine(paths.PluginRoot, entry.Id)));
            manager.ApplyPending();
            Assert(manager.Snapshot.Single().ActiveVersion == "1.0.0");
            Assert(File.Exists(Path.Combine(paths.PluginRoot, entry.Id, "1.0.0", "Fixture.dll")));
        });
        check("Management pending conflicts return a reason without changing the queued request or staging", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility);
            Assert(manager.QueueInstall(Zip(paths), []) is null);
            var before = manager.Snapshot.Single();
            Assert(manager.QueueInstall(Zip(paths, Manifest("2.0.0")), [])?.Contains("待重启") == true);
            Assert(manager.QueueChange(before.Id, PluginChangeKind.Uninstall, [])?.Contains("待重启") == true);
            Assert(manager.Snapshot.Single() == before);
            var staging = Path.Combine(paths.PluginRoot, ".management", "staging");
            Assert(Directory.GetDirectories(staging).Select(Path.GetFileName).SequenceEqual([before.Pending!.Token]));
        });
        check("Management installation lock spans profiles", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility);
            var other = new AppPaths(paths.AppRoot, Path.Combine(paths.DataRoot, "other"));
            Reject<IOException>(() => new PluginManager(other, compatibility));
        });
        check("Management cancellation removes only its staging", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility);
            Assert(manager.QueueInstall(Zip(paths), []) is null); var entry = manager.Snapshot.Single();
            var stage = Path.Combine(paths.PluginRoot, ".management", "staging", entry.Pending!.Token);
            manager.CancelPending(entry.Id);
            Assert(manager.Snapshot.Single().Pending is null && !Directory.Exists(stage));
            manager.ApplyPending(); Assert(!Directory.Exists(Path.Combine(paths.PluginRoot, entry.Id)));
        });
        check("Management rejects ZIP traversal and case duplicate names", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility);
            Reject<InvalidDataException>(() => manager.QueueInstall(Zip(paths, extra: a => a.CreateEntry("../escape.txt")), []));
            Reject<InvalidDataException>(() => manager.QueueInstall(Zip(paths, extra: a => a.CreateEntry("PLUGIN.JSON")), []));
            Assert(manager.Snapshot.Count == 0 && !File.Exists(Path.Combine(paths.PluginRoot, "escape.txt")));
        });
        check("Management rejects link and Windows ambiguous ZIP paths", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility);
            Reject<InvalidDataException>(() => manager.QueueInstall(Zip(paths, extra: a => a.CreateEntry("link").ExternalAttributes = unchecked((int)0xA0000000)), []));
            foreach (var name in new[] { "file:stream", "CON.txt", "trailing. ", "/absolute", "sub\\..\\x" })
                Reject<InvalidDataException>(() => manager.QueueInstall(Zip(paths, extra: a => a.CreateEntry(name)), []));
        });
        check("Management rejects missing DLL and incompatible SDK", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility);
            Reject<FileNotFoundException>(() => manager.QueueInstall(Zip(paths, includeDll: false), []));
            Reject<InvalidDataException>(() => manager.QueueInstall(Zip(paths, Manifest() with { ApiVersion = "2.0" }), []));
            Assert(manager.Snapshot.Count == 0);
        });
        check("Management never overwrites same version or changed ID casing", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility); Install(manager, paths);
            var original = File.ReadAllBytes(Path.Combine(paths.PluginRoot, "Checks.Management", "1.0.0", "Fixture.dll"));
            Assert(manager.QueueInstall(Zip(paths), [])?.Contains("此版本已存在") == true);
            Reject<InvalidDataException>(() => manager.QueueInstall(Zip(paths, Manifest("2.0.0", "checks.management")), []));
            Assert(original.SequenceEqual(File.ReadAllBytes(Path.Combine(paths.PluginRoot, "Checks.Management", "1.0.0", "Fixture.dll"))));
        });
        check("Management activation detects tampered staged package", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility);
            Assert(manager.QueueInstall(Zip(paths), []) is null); var entry = manager.Snapshot.Single();
            File.WriteAllText(Path.Combine(paths.PluginRoot, ".management", "staging", entry.Pending!.Token, entry.Id, "1.0.0", "extra.txt"), "tampered");
            manager.ApplyPending();
            Assert(manager.Snapshot.Single().ActiveVersion is null && manager.Snapshot.Single().Error.Length != 0);
            manager.CancelPending(entry.Id);
            Assert(manager.Snapshot.Single().Pending is null && manager.Snapshot.Single().Error.Length == 0);
            Assert(manager.QueueInstall(Zip(paths), []) is null); // Bad install can be replaced through the supported flow.
        });
        check("Management first adoption preserves original manifest ID casing", () =>
        {
            var paths = Paths();
            var directory = Path.Combine(paths.PluginRoot, "Checks.Management", "1.0.0"); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "plugin.json"), JsonSerializer.Serialize(Manifest(), JsonOptions.Value));
            File.Copy(typeof(IPlugin).Assembly.Location, Path.Combine(directory, "Fixture.dll"));
            using var manager = new PluginManager(paths, compatibility);
            var host = new PluginHost(paths, compatibility, new AppLog(paths.LogRoot)); host.Discover([]);
            Reject<InvalidDataException>(() => manager.QueueInstall(Zip(paths, Manifest("2.0.0", "checks.management")), host.Records));
            Assert(manager.Snapshot.Count == 0);
        });
        check("Management selects one version and overrides legacy disabled list", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility); Install(manager, paths);
            Assert(manager.QueueInstall(Zip(paths, Manifest("2.0.0")), []) is null); manager.ApplyPending();
            var host = new PluginHost(paths, compatibility, new AppLog(paths.LogRoot));
            host.Discover(["Checks.Management"], manager.Snapshot);
            Assert(host.Records.Count == 1 && host.Records[0].Manifest?.Version == "2.0.0" && host.Records[0].State == PluginState.Discovered);
            Assert(manager.QueueChange("Checks.Management", PluginChangeKind.Disable, host.Records) is null); manager.ApplyPending();
            host.Discover([], manager.Snapshot); Assert(host.Records.Single().State == PluginState.Disabled);
        });
        check("Management update snapshots last data and rollback retains failed data", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility); Install(manager, paths);
            var data = paths.GetPluginDataDirectory("Checks.Management"); Directory.CreateDirectory(data);
            var config = Path.Combine(data, "preferences.json"); File.WriteAllText(config, "before request");
            Assert(manager.QueueInstall(Zip(paths, Manifest("2.0.0")), []) is null);
            File.WriteAllText(config, "last old-version save");
            manager.ApplyPending(); File.WriteAllText(config, "new-version format");
            var recovery = manager.Snapshot.Single().Recovery!;
            manager.RecordLoadResults([new PluginRecord("fixture", Manifest("2.0.0"), PluginState.Failed, "Injected outcome")]);
            Assert(manager.Snapshot.Single().Error.Length != 0);
            Assert(manager.QueueChange("Checks.Management", PluginChangeKind.Rollback, []) is null); manager.ApplyPending();
            Assert(manager.Snapshot.Single().ActiveVersion == "1.0.0" && File.ReadAllText(config) == "last old-version save");
            Assert(File.ReadAllText(Path.Combine(paths.DataRoot, "Data", ".plugin-backups", recovery.Token, "before-rollback", "preferences.json")) == "new-version format");
        });
        check("Management pending changes block migration before copying", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility);
            Install(manager, paths);
            var migration = new DataRootMigration(paths);
            var target = paths.DataRoot + "-moved";
            Assert(manager.QueueChange("Checks.Management", PluginChangeKind.Disable, []) is null);
            Assert(migration.Queue(target, manager) is not null && !Directory.Exists(target));
            manager.CancelPending("Checks.Management");
            Assert(migration.Queue(target, manager) is null);
            Assert(manager.QueueChange("Checks.Management", PluginChangeKind.Disable, []) is null);
            Assert(migration.ApplyAtStartup(manager, null, default).Error is not null);
            Assert(!Directory.Exists(target) && migration.Snapshot.DataRoot is null && manager.Snapshot.Single().Pending is not null);
        });
        check("Management rollback uses migrated snapshots and leaves original data unchanged", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility); Install(manager, paths);
            var data = paths.GetPluginDataDirectory("Checks.Management"); Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "preferences.json"), "old format");
            File.WriteAllText(Path.Combine(data, "empty.txt"), "");
            Directory.CreateDirectory(Path.Combine(data, "nested"));
            File.WriteAllBytes(Path.Combine(data, "nested", "data.bin"), Enumerable.Repeat((byte)0xA5, 70000).ToArray());
            Assert(manager.QueueInstall(Zip(paths, Manifest("2.0.0")), []) is null); manager.ApplyPending();
            File.WriteAllText(Path.Combine(data, "preferences.json"), "new format");
            manager.RecordLoadResults([new PluginRecord("fixture", Manifest("2.0.0"), PluginState.Loaded, "State fixture")]);
            var recovery = manager.Snapshot.Single().Recovery!;
            // Frozen legacy digest: ordered relative UTF-8 paths + NUL + each file's SHA256.
            // Empty/nested files and data spanning the hash buffer must remain compatible.
            var originalSnapshot = Path.Combine(paths.DataRoot, "Data", ".plugin-backups", recovery.Token, "snapshot.json");
            Assert(new JsonFileStore<PluginManager.RecoveryMetadata>(originalSnapshot).Load().Digest ==
                "E191F92B422840CD6A634D4174346F49274A3ABD2768D5A8868C2A782A25A678");
            var migration = new DataRootMigration(paths);
            Assert(migration.Queue(paths.DataRoot + "-moved", manager) is null);
            var result = migration.ApplyAtStartup(manager, null, default); using var lease = result.TargetLease;
            Assert(result.Error is null && manager.Snapshot.Single().Recovery!.DataRoot == result.Paths.DataRoot);
            var savedMetadata = File.ReadAllText(originalSnapshot);
            Assert(savedMetadata.Contains(JsonSerializer.Serialize(paths.DataRoot, JsonOptions.Value)));
            Assert(manager.QueueChange("Checks.Management", PluginChangeKind.Rollback, []) is null);
            Assert(manager.Snapshot.Single().Pending!.DataRoot == result.Paths.DataRoot);
            manager.ApplyPending();
            Assert(manager.Snapshot.Single() is { ActiveVersion: "1.0.0", Pending: null });
            Assert(File.ReadAllText(Path.Combine(result.Paths.GetPluginDataDirectory("Checks.Management"), "preferences.json")) == "old format");
            Assert(File.ReadAllText(Path.Combine(data, "preferences.json")) == "new format");
            Assert(File.ReadAllText(originalSnapshot) == savedMetadata);
        });
        foreach (var afterCommit in new[] { false, true })
            check("Migration snapshot cancellation resumes " + (afterCommit ? "after" : "before") + " pointer commit", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility); Install(manager, paths);
            var data = paths.GetPluginDataDirectory("Checks.Management"); Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "fixture.txt"), "retained source");
            Assert(manager.QueueInstall(Zip(paths, Manifest("2.0.0")), []) is null); manager.ApplyPending();
            manager.RecordLoadResults([new PluginRecord("fixture", Manifest("2.0.0"), PluginState.Loaded, "State fixture")]);
            var migration = new DataRootMigration(paths); var target = paths.DataRoot + "-moved";
            Assert(migration.Queue(target, manager) is null);
            using var cancel = new CancellationTokenSource();
            var phase = afterCommit ? "正在完成插件回退记录…" : "正在校验插件回退快照…";
            Reject<OperationCanceledException>(() => migration.ApplyAtStartup(manager,
                new CallbackProgress(message => { if (message == phase) cancel.Cancel(); }), cancel.Token));
            Assert(migration.Snapshot.DataRoot == (afterCommit ? target : null));
            Assert(migration.Snapshot.Migration is { Error: null });
            Assert(manager.Snapshot.Single().Recovery!.DataRoot == paths.DataRoot);
            manager.Dispose(); // Reopen as the next startup would, using the persisted pointer.
            var next = afterCommit ? new AppPaths(paths.AppRoot, paths.BootstrapRoot, target) : paths;
            using var sourceLease = new InstanceLease(next.DataRoot);
            using var resumedManager = new PluginManager(next, compatibility);
            var scratch = Path.Combine(target, ".tractortool-migration-copy.tmp");
            // All final files already exist; retry must still reclaim abandoned scratch space.
            if (!afterCommit) File.WriteAllText(scratch, "incomplete scratch");
            var resumed = new DataRootMigration(next).ApplyAtStartup(resumedManager, null, default);
            using var targetLease = resumed.TargetLease;
            Assert(resumed.Error is null && migration.Snapshot.Migration is null);
            Assert(resumedManager.Snapshot.Single().Recovery!.DataRoot == target);
            Assert(!File.Exists(scratch));
            Assert(File.ReadAllText(Path.Combine(data, "fixture.txt")) == "retained source");
        });
        check("Migration detects damaged recovery snapshots before committing the pointer", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility); Install(manager, paths);
            Assert(manager.QueueInstall(Zip(paths, Manifest("2.0.0")), []) is null); manager.ApplyPending();
            manager.RecordLoadResults([new PluginRecord("fixture", Manifest("2.0.0"), PluginState.Loaded, "State fixture")]);
            var recovery = manager.Snapshot.Single().Recovery!;
            var backup = Path.Combine(paths.DataRoot, "Data", ".plugin-backups", recovery.Token, "saved", "unexpected.txt");
            File.WriteAllText(backup, "damaged snapshot fixture");
            var migration = new DataRootMigration(paths);
            Assert(migration.Queue(paths.DataRoot + "-moved", manager) is null);
            var result = migration.ApplyAtStartup(manager, null, default);
            Assert(result.Error?.Contains("回退快照无效") == true && migration.Snapshot.DataRoot is null);
            Assert(manager.Snapshot.Single().Recovery!.DataRoot == paths.DataRoot && File.ReadAllText(backup) == "damaged snapshot fixture");
        });
        if (OperatingSystem.IsWindows()) check("Migration resumes after pointer commit and a locked plugin management state", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility); Install(manager, paths);
            var data = paths.GetPluginDataDirectory("Checks.Management"); Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "fixture.txt"), "retained source");
            Assert(manager.QueueInstall(Zip(paths, Manifest("2.0.0")), []) is null); manager.ApplyPending();
            manager.RecordLoadResults([new PluginRecord("fixture", Manifest("2.0.0"), PluginState.Loaded, "State fixture")]);
            var migration = new DataRootMigration(paths);
            var target = paths.DataRoot + "-moved";
            Assert(migration.Queue(target, manager) is null);
            using (var busy = new FileStream(Path.Combine(paths.PluginRoot, ".management", "state.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject<IOException>(() => migration.ApplyAtStartup(manager, null, default));
            Assert(migration.Snapshot.DataRoot == target && migration.Snapshot.Migration is not null);
            Assert(manager.Snapshot.Single().Recovery!.DataRoot == paths.DataRoot);
            using var targetLease = new InstanceLease(target);
            var next = new AppPaths(paths.AppRoot, paths.BootstrapRoot, target);
            var resumed = new DataRootMigration(next).ApplyAtStartup(manager, null, default);
            Assert(resumed.Error is null && migration.Snapshot.Migration is null);
            Assert(manager.Snapshot.Single().Recovery!.DataRoot == target);
            Assert(File.ReadAllText(Path.Combine(data, "fixture.txt")) == "retained source");
        });
        check("Management pending change rejects different data profile", () =>
        {
            var paths = Paths();
            using (var manager = new PluginManager(paths, compatibility)) Assert(manager.QueueInstall(Zip(paths), []) is null);
            var other = new AppPaths(paths.AppRoot, paths.DataRoot + "-other"); other.EnsureDataDirectories();
            using var next = new PluginManager(other, compatibility); next.ApplyPending();
            Assert(next.Snapshot.Single().Pending is not null && next.Snapshot.Single().ActiveVersion is null && next.Snapshot.Single().Error.Length != 0);
        });
        check("Management resumes activation after directory move before state save", () =>
        {
            var paths = Paths();
            using (var manager = new PluginManager(paths, compatibility))
            {
                Assert(manager.QueueInstall(Zip(paths), []) is null); var item = manager.Snapshot.Single();
                var target = Path.Combine(paths.PluginRoot, item.Id, "1.0.0"); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Directory.Move(Path.Combine(paths.PluginRoot, ".management", "staging", item.Pending!.Token, item.Id, "1.0.0"), target);
            }
            using var reopened = new PluginManager(paths, compatibility); reopened.ApplyPending();
            Assert(reopened.Snapshot.Single().ActiveVersion == "1.0.0" && reopened.Snapshot.Single().Pending is null);
        });
        check("Management uninstall removes code and preserves user data", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility); Install(manager, paths);
            var data = paths.GetPluginDataDirectory("Checks.Management"); Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "keep.txt"), "user fixture");
            Assert(manager.QueueChange("Checks.Management", PluginChangeKind.Uninstall, []) is null);
            Assert(Directory.Exists(Path.Combine(paths.PluginRoot, "Checks.Management")));
            manager.ApplyPending();
            Assert(manager.Snapshot.Single().ActiveVersion is null && !Directory.Exists(Path.Combine(paths.PluginRoot, "Checks.Management")));
            Assert(File.ReadAllText(Path.Combine(data, "keep.txt")) == "user fixture");
        });
        check("Management state write failure does not queue or redirect", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility); Install(manager, paths);
            using var locked = new FileStream(Path.Combine(paths.PluginRoot, ".management", "state.json"), FileMode.Open, FileAccess.Read, FileShare.None);
            Reject<IOException>(() => manager.QueueChange("Checks.Management", PluginChangeKind.Disable, []));
            Assert(manager.Snapshot.Single().Pending is null && !Directory.Exists(Path.Combine(paths.DataRoot, "Plugins")));
        });
    }
    private static void Assert(bool condition) { if (!condition) throw new Exception("Management assertion failed"); }
    private sealed class CallbackProgress(Action<string> report) : IProgress<string>
    { public void Report(string value) => report(value); }
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
