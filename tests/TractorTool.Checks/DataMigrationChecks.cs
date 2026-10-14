using TractorTool.Core.Plugins;
using TractorTool.Core.Storage;

internal static class DataMigrationChecks
{
    public static void Run(string root, Action<string, Action> check)
    {
        var compatibility = new HostCompatibility("windows", "win-x64", "1.3", "12.1.2");
        AppPaths Paths()
        {
            var folder = Path.Combine(root, "migration-" + Guid.NewGuid().ToString("N"));
            var paths = new AppPaths(Path.Combine(folder, "app"), Path.Combine(folder, "profile"));
            paths.EnsureDataDirectories();
            new JsonFileStore<BootstrapSettings>(paths.BootstrapFile, BootstrapSettings.Validate).Save(new());
            return paths;
        }
        string Target(AppPaths paths) => paths.DataRoot + "-moved";
        check("Missing paths during initialization, reset and migration do not raise first-chance exceptions", () =>
        {
            var missingExceptions = 0;
            var thread = Environment.CurrentManagedThreadId;
            void OnException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
            {
                if (Environment.CurrentManagedThreadId == thread &&
                    args.Exception is FileNotFoundException or DirectoryNotFoundException) missingExceptions++;
            }
            AppDomain.CurrentDomain.FirstChanceException += OnException;
            try
            {
                var profile = Path.Combine(root, "quiet-probe-" + Guid.NewGuid().ToString("N"));
                var initial = AppPaths.Open(profile);
                using var profileLease = new InstanceLease(profile);
                var missing = profile + "-deleted";
                new JsonFileStore<BootstrapSettings>(initial.BootstrapFile).Save(new() { DataRoot = missing });
                var reset = AppPaths.Open(profile);
                reset.CompleteDataReset(_ => { }); reset.EnsureDataDirectories();
                Assert(reset.DataRoot == profile && !Directory.Exists(missing));

                var paths = Paths(); using var manager = new PluginManager(paths, compatibility);
                Write(paths.ConfigRoot, "settings.json", "migration fixture");
                var migration = new DataRootMigration(paths);
                Assert(migration.Queue(Target(paths), manager) is null);
                var result = migration.ApplyAtStartup(manager, null, default); using var targetLease = result.TargetLease;
                Assert(result.Error is null && result.Paths.DataRoot == Target(paths));
            }
            finally { AppDomain.CurrentDomain.FirstChanceException -= OnException; }
            Assert(missingExceptions == 0);
        });
        check("Migration preserves old bootstrap schema and rejects invalid journals", () =>
        {
            var paths = Paths();
            File.WriteAllText(paths.BootstrapFile, "{\"schemaVersion\":1,\"dataRoot\":null}");
            Assert(new DataRootMigration(paths).Snapshot.Migration is null);
            var store = new JsonFileStore<BootstrapSettings>(paths.BootstrapFile, BootstrapSettings.Validate);
            Reject<InvalidDataException>(() => store.Save(new() { Migration = new("bad", paths.DataRoot, Target(paths), paths.AppRoot) }));
            Reject<InvalidDataException>(() => store.Save(new() { DataRoot = "relative" }));
            Assert(store.Load().DataRoot is null);
        });
        check("Migration copies persistent data then cleans owned source folders and preserves root files and plugin code", () =>
        {
            var paths = Paths(); using var sourceLease = new InstanceLease(paths.DataRoot);
            using var manager = new PluginManager(paths, compatibility);
            Write(paths.ConfigRoot, "settings.json", "settings");
            Write(paths.GetPluginDataDirectory("Fixture"), "data.txt", "plugin data");
            Write(paths.PackageRoot, "receipt.json", "package receipt");
            Write(paths.LogRoot, "history.log", "log history");
            Write(Path.Combine(paths.DataRoot, "Cache"), "cache.bin", "rebuild");
            Write(Path.Combine(paths.PackageRoot, ".work"), "partial.zip", "incomplete");
            Write(Path.Combine(paths.PluginRoot, "Fixture", "1.0.0"), "code.dll", "do not move");
            Write(paths.DataRoot, "keep.txt", "unknown root file");
            Write(Path.Combine(paths.DataRoot, "Unknown"), "keep.txt", "unknown root directory");
            var migration = new DataRootMigration(paths);
            Assert(migration.Queue(Target(paths), manager) is null);
            Assert(!Directory.Exists(Target(paths)) && migration.Snapshot.DataRoot is null);
            Write(paths.ConfigRoot, "last-save.json", "saved after queue");
            var copied = new[] { "Config/settings.json", "Config/last-save.json", "Data/Plugins/Fixture/data.txt", "Packages/receipt.json", "Logs/history.log" }
                .ToDictionary(relative => relative, relative => (Text: File.ReadAllText(Path.Combine(paths.DataRoot, relative)),
                    Modified: File.GetLastWriteTimeUtc(Path.Combine(paths.DataRoot, relative))));
            var result = migration.ApplyAtStartup(manager, null, default); using var targetLease = result.TargetLease;
            Assert(result.Error is null && result.Paths.DataRoot == Target(paths));
            foreach (var (relative, original) in copied)
            {
                var after = Path.Combine(result.Paths.DataRoot, relative);
                Assert(original.Text == File.ReadAllText(after));
                Assert(original.Modified == File.GetLastWriteTimeUtc(after));
            }
            foreach (var folder in new[] { "Config", "Data", "Packages", "Logs", "Cache" })
                Assert(!Directory.Exists(Path.Combine(paths.DataRoot, folder)));
            Assert(File.Exists(paths.BootstrapFile) && File.Exists(Path.Combine(paths.DataRoot, "host.lock")));
            Assert(File.ReadAllText(Path.Combine(paths.DataRoot, "keep.txt")) == "unknown root file");
            Assert(File.ReadAllText(Path.Combine(paths.DataRoot, "Unknown", "keep.txt")) == "unknown root directory");
            Assert(!Directory.Exists(Path.Combine(result.Paths.DataRoot, "Cache")));
            Assert(!Directory.Exists(Path.Combine(result.Paths.PackageRoot, ".work")));
            Assert(!File.Exists(Path.Combine(result.Paths.DataRoot, "bootstrap.json")));
            Assert(!Directory.Exists(Path.Combine(result.Paths.DataRoot, "Plugins")));
            Assert(File.ReadAllText(Path.Combine(paths.PluginRoot, "Fixture", "1.0.0", "code.dll")) == "do not move");
            Assert(migration.Snapshot is { Migration: null } state && state.DataRoot == Target(paths) && state.PreviousDataRoot == paths.DataRoot);
            Reject<IOException>(() => { using var forbidden = new InstanceLease(Target(paths)); });
            Assert(migration.Queue(Target(paths) + "-again", manager)?.Contains("已变化") == true);
        });
        check("Migration rejects overlapping and occupied targets without changing their contents", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility);
            var migration = new DataRootMigration(paths);
            foreach (var invalid in new[] { paths.DataRoot, Path.Combine(paths.DataRoot, "child"), Path.GetDirectoryName(paths.DataRoot)!, paths.PluginRoot })
                Assert(migration.Queue(invalid, manager) is not null);
            Write(Target(paths), "keep.txt", "user file");
            Assert(migration.Queue(Target(paths), manager) is not null);
            Assert(File.ReadAllText(Path.Combine(Target(paths), "keep.txt")) == "user file" && migration.Snapshot.Migration is null);
        });
        check("Migration target lease failure retains source and permits explicit retry", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility);
            var migration = new DataRootMigration(paths);
            Assert(migration.Queue(Target(paths), manager) is null);
            using (var busy = new InstanceLease(Target(paths)))
            {
                var failed = migration.ApplyAtStartup(manager, null, default);
                Assert(failed.Error is not null && failed.Paths == paths && migration.Snapshot.DataRoot is null);
            }
            Assert(migration.ApplyAtStartup(manager, null, default).Error is not null); // No implicit retry.
            migration.Retry();
            var result = migration.ApplyAtStartup(manager, null, default); using var lease = result.TargetLease;
            Assert(result.Error is null && migration.Snapshot.DataRoot == Target(paths));
        });
        check("Migration cancellation retains journal and partial target and resumes identical content", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility);
            File.WriteAllBytes(Path.Combine(paths.ConfigRoot, "large.bin"), new byte[3 * 1024 * 1024]);
            var migration = new DataRootMigration(paths); Assert(migration.Queue(Target(paths), manager) is null);
            using var cancel = new CancellationTokenSource();
            Reject<OperationCanceledException>(() => migration.ApplyAtStartup(manager, new CallbackProgress(_ => cancel.Cancel()), cancel.Token));
            Assert(migration.Snapshot is { DataRoot: null, Migration.Error: null } && migration.Snapshot.Migration is not null);
            Assert(File.Exists(Path.Combine(Target(paths), ".tractortool-migration-copy.tmp")));
            var result = migration.ApplyAtStartup(manager, null, default); using var lease = result.TargetLease;
            Assert(result.Error is null && new FileInfo(Path.Combine(result.Paths.ConfigRoot, "large.bin")).Length == 3 * 1024 * 1024);
            Assert(!File.Exists(Path.Combine(Target(paths), ".tractortool-migration-copy.tmp")));
        });
        check("Migration retry refuses changed copied files and cancellation never deletes either copy", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility);
            Write(paths.ConfigRoot, "a.txt", "old settings");
            File.WriteAllBytes(Path.Combine(paths.ConfigRoot, "z.bin"), new byte[3 * 1024 * 1024]);
            var migration = new DataRootMigration(paths); Assert(migration.Queue(Target(paths), manager) is null);
            using var cancel = new CancellationTokenSource(); var reports = 0;
            Reject<OperationCanceledException>(() => migration.ApplyAtStartup(manager, new CallbackProgress(_ => { if (++reports == 2) cancel.Cancel(); }), cancel.Token));
            Write(paths.ConfigRoot, "a.txt", "changed settings");
            var result = migration.ApplyAtStartup(manager, null, default);
            Assert(result.Error is not null && migration.Snapshot.DataRoot is null);
            Assert(File.ReadAllText(Path.Combine(Target(paths), "Config", "a.txt")) == "old settings");
            migration.Cancel();
            Assert(migration.Snapshot.Migration is null && File.ReadAllText(Path.Combine(paths.ConfigRoot, "a.txt")) == "changed settings");
            Assert(Directory.Exists(Target(paths)));
        });
        check("Migration interrupted cleanup resumes the remaining original inventory", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility);
            Write(paths.ConfigRoot, "fixture.txt", "committed");
            Write(Path.Combine(paths.DataRoot, "Cache"), "scratch.bin", "discarded cache");
            var migration = new DataRootMigration(paths); Assert(migration.Queue(Target(paths), manager) is null);
            using var cancel = new CancellationTokenSource();
            Reject<OperationCanceledException>(() => migration.ApplyAtStartup(manager,
                new CallbackProgress(message => { if (message.StartsWith("正在清理旧应用数据… ", StringComparison.Ordinal)) cancel.Cancel(); }), cancel.Token));
            Assert(migration.Snapshot.DataRoot == Target(paths) && migration.Snapshot.Migration is not null);
            Assert(!File.Exists(Path.Combine(paths.ConfigRoot, "fixture.txt")));
            Assert(File.Exists(Path.Combine(paths.DataRoot, "Cache", "scratch.bin")));
            var next = new AppPaths(paths.AppRoot, paths.BootstrapRoot, Target(paths));
            var reopened = new DataRootMigration(next);
            Reject<InvalidOperationException>(reopened.Cancel);
            var resumed = reopened.ApplyAtStartup(manager, null, default);
            Assert(resumed.Error is null && resumed.TargetLease is null && reopened.Snapshot.Migration is null);
            Assert(File.ReadAllText(Path.Combine(next.ConfigRoot, "fixture.txt")) == "committed");
            Assert(!Directory.Exists(paths.ConfigRoot) && !Directory.Exists(Path.Combine(paths.DataRoot, "Cache")));
        });
        foreach (var addFile in new[] { false, true })
            check("Migration cleanup preserves externally " + (addFile ? "added" : "changed") + " source data after commit", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility);
            Write(paths.ConfigRoot, "fixture.txt", "original");
            var migration = new DataRootMigration(paths); Assert(migration.Queue(Target(paths), manager) is null);
            using var cancel = new CancellationTokenSource();
            Reject<OperationCanceledException>(() => migration.ApplyAtStartup(manager,
                new CallbackProgress(message => { if (message == "正在清理旧应用数据…") cancel.Cancel(); }), cancel.Token));
            var changed = addFile ? "new.txt" : "fixture.txt";
            Write(paths.ConfigRoot, changed, "new external data");
            var next = new AppPaths(paths.AppRoot, paths.BootstrapRoot, Target(paths));
            var reopened = new DataRootMigration(next);
            Reject<IOException>(() => reopened.ApplyAtStartup(manager, null, default));
            Assert(reopened.Snapshot.DataRoot == next.DataRoot && reopened.Snapshot.Migration is not null);
            Assert(File.ReadAllText(Path.Combine(paths.ConfigRoot, changed)) == "new external data");
            Assert(File.ReadAllText(Path.Combine(next.ConfigRoot, "fixture.txt")) == "original");
        });
        foreach (var damage in new[] { "deleted", "truncated", "same-length edit", "changed during cleanup" })
            check("Migration cleanup retains source when target is " + damage, () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility);
            Write(paths.ConfigRoot, "fixture.txt", "original");
            Write(paths.DataRoot, Path.Combine("Data", "independent.txt"), "also retain");
            var migration = new DataRootMigration(paths); Assert(migration.Queue(Target(paths), manager) is null);
            using var cancel = new CancellationTokenSource();
            Reject<OperationCanceledException>(() => migration.ApplyAtStartup(manager,
                new CallbackProgress(message => { if (message == "正在清理旧应用数据…") cancel.Cancel(); }), cancel.Token));
            var next = new AppPaths(paths.AppRoot, paths.BootstrapRoot, Target(paths));
            var targetFile = Path.Combine(next.ConfigRoot, "fixture.txt");
            if (damage == "deleted") File.Delete(targetFile);
            else if (damage != "changed during cleanup")
            {
                var modified = File.GetLastWriteTimeUtc(targetFile);
                File.WriteAllText(targetFile, damage == "truncated" ? "" : "modified");
                File.SetLastWriteTimeUtc(targetFile, modified); // Size/time cannot substitute for content verification.
            }
            var reopened = new DataRootMigration(next);
            Reject<IOException>(() => reopened.ApplyAtStartup(manager,
                new CallbackProgress(message =>
                {
                    if (damage == "changed during cleanup" && message == "正在清理旧应用数据…") File.Delete(targetFile);
                }), default));
            Assert(reopened.Snapshot.Migration is not null && reopened.Snapshot.DataRoot == next.DataRoot);
            Assert(File.ReadAllText(Path.Combine(paths.ConfigRoot, "fixture.txt")) == "original");
            if (damage != "changed during cleanup")
                Assert(File.ReadAllText(Path.Combine(paths.DataRoot, "Data", "independent.txt")) == "also retain");
            Write(next.ConfigRoot, "fixture.txt", "original");
            var resumed = reopened.ApplyAtStartup(manager, null, default);
            Assert(resumed.Error is null && reopened.Snapshot.Migration is null && !Directory.Exists(paths.ConfigRoot));
        });
        check("Migration legacy committed marker without cleanup inventory preserves old data", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility);
            Write(paths.ConfigRoot, "fixture.txt", "old data");
            var migration = new DataRootMigration(paths); Assert(migration.Queue(Target(paths), manager) is null);
            using var cancel = new CancellationTokenSource();
            Reject<OperationCanceledException>(() => migration.ApplyAtStartup(manager,
                new CallbackProgress(message => { if (message == "正在清理旧应用数据…") cancel.Cancel(); }), cancel.Token));
            var markerPath = Path.Combine(Target(paths), ".tractortool-migration.json");
            var marker = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(markerPath))!.AsObject();
            marker.Remove("cleanup"); File.WriteAllText(markerPath, marker.ToJsonString());
            var reopened = new DataRootMigration(new AppPaths(paths.AppRoot, paths.BootstrapRoot, Target(paths)));
            var resumed = reopened.ApplyAtStartup(manager, null, default);
            Assert(resumed.Error is null && resumed.Warning?.Contains("旧版本") == true && reopened.Snapshot.Migration is null);
            Assert(File.ReadAllText(Path.Combine(paths.ConfigRoot, "fixture.txt")) == "old data");
        });
        foreach (var deleteSource in new[] { false, true })
            check("Migration committed custom source " + (deleteSource ? "may already be missing without recreation" : "must be locked before cleanup"), () =>
        {
            var original = Paths();
            var paths = new AppPaths(original.AppRoot, original.BootstrapRoot, original.DataRoot + "-custom");
            paths.EnsureDataDirectories();
            new JsonFileStore<BootstrapSettings>(paths.BootstrapFile, BootstrapSettings.Validate).Save(new() { DataRoot = paths.DataRoot });
            using var manager = new PluginManager(paths, compatibility);
            Write(paths.ConfigRoot, "fixture.txt", "copied custom data");
            var migration = new DataRootMigration(paths); Assert(migration.Queue(Target(paths), manager) is null);
            using var cancel = new CancellationTokenSource();
            Reject<OperationCanceledException>(() => migration.ApplyAtStartup(manager,
                new CallbackProgress(message => { if (message == "正在清理旧应用数据…") cancel.Cancel(); }), cancel.Token));
            var next = new AppPaths(paths.AppRoot, paths.BootstrapRoot, Target(paths));
            var reopened = new DataRootMigration(next);
            if (deleteSource) Directory.Delete(paths.DataRoot, recursive: true); // Isolated test data only.
            else
            {
                using var busy = new InstanceLease(paths.DataRoot);
                Reject<IOException>(() => reopened.ApplyAtStartup(manager, null, default));
                Assert(File.ReadAllText(Path.Combine(paths.ConfigRoot, "fixture.txt")) == "copied custom data");
            }
            var resumed = reopened.ApplyAtStartup(manager, null, default);
            Assert(resumed.Error is null && reopened.Snapshot.Migration is null);
            Assert(File.ReadAllText(Path.Combine(next.ConfigRoot, "fixture.txt")) == "copied custom data");
            Assert(deleteSource ? !Directory.Exists(paths.DataRoot) : !Directory.Exists(paths.ConfigRoot));
        });
        if (OperatingSystem.IsWindows()) check("Migration bootstrap write failure never publishes the target as a fallback", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility);
            Write(paths.ConfigRoot, "fixture.txt", "source");
            var migration = new DataRootMigration(paths); Assert(migration.Queue(Target(paths), manager) is null);
            using (var busy = new FileStream(paths.BootstrapFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject<IOException>(() => migration.ApplyAtStartup(manager, null, default));
            Assert(migration.Snapshot.DataRoot is null && migration.Snapshot.Migration is not null);
            Assert(File.ReadAllText(Path.Combine(paths.ConfigRoot, "fixture.txt")) == "source");
            var result = migration.ApplyAtStartup(manager, null, default); using var lease = result.TargetLease;
            Assert(result.Error is null && migration.Snapshot.DataRoot == Target(paths));
        });
        check("Migration unexpected target content preserves both roots and does not commit", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility);
            File.WriteAllBytes(Path.Combine(paths.ConfigRoot, "data.bin"), new byte[2 * 1024 * 1024]);
            var migration = new DataRootMigration(paths); Assert(migration.Queue(Target(paths), manager) is null);
            using var cancel = new CancellationTokenSource();
            Reject<OperationCanceledException>(() => migration.ApplyAtStartup(manager, new CallbackProgress(_ => cancel.Cancel()), cancel.Token));
            Write(Path.Combine(Target(paths), "Packages", ".work"), "foreign.txt", "foreign download");
            Assert(migration.ApplyAtStartup(manager, null, default).Error is not null && migration.Snapshot.DataRoot is null);
            Assert(File.ReadAllText(Path.Combine(Target(paths), "Packages", ".work", "foreign.txt")) == "foreign download");
        });
    }

    private static void Write(string directory, string file, string text)
    { Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, file), text); }
    private static void Assert(bool value) { if (!value) throw new Exception("Assertion failed"); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private sealed class CallbackProgress(Action<string> report) : IProgress<string>
    { public void Report(string value) => report(value); }
}
