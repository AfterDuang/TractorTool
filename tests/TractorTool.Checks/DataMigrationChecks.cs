using TractorTool.Core.Plugins;
using TractorTool.Core.Storage;
using TractorTool.Core.Packages.Python;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
        check("A missing bootstrap with a surviving backup stops before creating default data", () =>
        {
            var profile = Path.Combine(root, "orphaned-bootstrap");
            var pointer = Path.Combine(profile, "bootstrap.json");
            var backup = pointer + ".bak";
            var custom = profile + "-custom";
            Write(custom, "retained.txt", "data");
            new JsonFileStore<BootstrapSettings>(backup).Save(new() { DataRoot = custom });
            var original = File.ReadAllText(backup);
            Reject<InvalidDataException>(() => AppPaths.Open(profile));
            Assert(!File.Exists(pointer) && !Directory.Exists(Path.Combine(profile, "Config")));
            Assert(File.ReadAllText(backup) == original && File.ReadAllText(Path.Combine(custom, "retained.txt")) == "data");
            File.Copy(backup, pointer);
            Assert(AppPaths.Open(profile).DataRoot == custom);
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
        if (OperatingSystem.IsWindows())
        {
            var mapped = DriveInfo.GetDrives().FirstOrDefault(drive => drive.DriveType == DriveType.Network);
            if (mapped is null) Console.WriteLine("SKIP Mapped migration target: no mapped Windows network drive.");
            else check("Queue and startup reject a mapped network drive", () =>
            {
                var paths = Paths(); using var manager = new PluginManager(paths, compatibility);
                var target = Path.Combine(mapped.Name, "migration-check-" + Guid.NewGuid().ToString("N"));
                var migration = new DataRootMigration(paths);
                Assert(migration.Queue(target, manager)?.Contains("本机磁盘") == true && migration.Snapshot.Migration is null);
                new JsonFileStore<BootstrapSettings>(paths.BootstrapFile).Save(new()
                {
                    Migration = new(Guid.NewGuid().ToString("N"), paths.DataRoot, target, paths.AppRoot)
                });
                var result = migration.ApplyAtStartup(manager, null, default);
                Assert(result.Error?.Contains("本机磁盘") == true && migration.Snapshot.DataRoot is null);
            });
        }
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
            var targetFile = Path.Combine(next.ConfigRoot, "fixture.txt");
            var modified = File.GetLastWriteTimeUtc(targetFile);
            File.WriteAllText(targetFile, "corrupted"); // Same length and timestamp, source copy is already gone.
            File.SetLastWriteTimeUtc(targetFile, modified);
            Reject<IOException>(() => reopened.ApplyAtStartup(manager, null, default));
            Assert(reopened.Snapshot.Migration is not null && File.Exists(Path.Combine(paths.DataRoot, "Cache", "scratch.bin")));
            File.Delete(targetFile);
            Reject<IOException>(() => reopened.ApplyAtStartup(manager, null, default));
            Assert(reopened.Snapshot.Migration is not null && File.Exists(Path.Combine(paths.DataRoot, "Cache", "scratch.bin")));
            File.WriteAllText(targetFile, "committed");
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
            marker.Remove("cleanup"); marker.Remove("targetHashes"); File.WriteAllText(markerPath, marker.ToJsonString());
            var reopened = new DataRootMigration(new AppPaths(paths.AppRoot, paths.BootstrapRoot, Target(paths)));
            var resumed = reopened.ApplyAtStartup(manager, null, default);
            Assert(resumed.Error is null && resumed.Warning?.Contains("旧版本") == true && reopened.Snapshot.Migration is null);
            Assert(File.ReadAllText(Path.Combine(paths.ConfigRoot, "fixture.txt")) == "old data");
        });
        check("Legacy cleanup inventory upgrades only with evidence from every copied file", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility);
            Write(paths.ConfigRoot, "fixture.txt", "legacy data");
            Write(paths.LogRoot, "keep.log", "retain until verified");
            var originalFile = Path.Combine(paths.ConfigRoot, "fixture.txt");
            var originalTime = File.GetLastWriteTimeUtc(originalFile);
            var migration = new DataRootMigration(paths); Assert(migration.Queue(Target(paths), manager) is null);
            using var cancel = new CancellationTokenSource();
            Reject<OperationCanceledException>(() => migration.ApplyAtStartup(manager,
                new CallbackProgress(message => { if (message == "正在清理旧应用数据…") cancel.Cancel(); }), cancel.Token));
            var next = new AppPaths(paths.AppRoot, paths.BootstrapRoot, Target(paths));
            var markerPath = Path.Combine(next.DataRoot, ".tractortool-migration.json");
            var marker = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(markerPath))!.AsObject();
            marker.Remove("targetHashes"); File.WriteAllText(markerPath, marker.ToJsonString());
            File.Delete(originalFile);
            var reopened = new DataRootMigration(next);
            Reject<IOException>(() => reopened.ApplyAtStartup(manager, null, default));
            Assert(reopened.Snapshot.Migration is not null && File.Exists(Path.Combine(paths.LogRoot, "keep.log")));
            File.WriteAllText(originalFile, "legacy data"); File.SetLastWriteTimeUtc(originalFile, originalTime);
            using var interrupt = new CancellationTokenSource();
            Reject<OperationCanceledException>(() => reopened.ApplyAtStartup(manager,
                new CallbackProgress(message => { if (message == "正在清理旧应用数据…") interrupt.Cancel(); }), interrupt.Token));
            Assert(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(markerPath))!["targetHashes"] is not null);
            Assert(reopened.ApplyAtStartup(manager, null, default).Error is null && reopened.Snapshot.Migration is null);
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
            if (deleteSource)
            {
                Directory.Delete(paths.DataRoot, recursive: true); // Isolated test data only.
                var copied = Path.Combine(next.ConfigRoot, "fixture.txt");
                File.WriteAllText(copied, "edited custom data");
                Reject<IOException>(() => reopened.ApplyAtStartup(manager, null, default));
                Assert(reopened.Snapshot.Migration is not null && !Directory.Exists(paths.DataRoot));
                File.WriteAllText(copied, "copied custom data");
            }
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
        foreach (var existingMarker in new[] { false, true })
            check(existingMarker ? "Migration resumes an existing marker with an interrupted pending update" : "Migration recreates its initial marker after an interrupted write", () =>
            {
                var paths = Paths(); using var manager = new PluginManager(paths, compatibility);
                File.WriteAllBytes(Path.Combine(paths.ConfigRoot, "marker-fixture.bin"), new byte[1024 * 1024]);
                var migration = new DataRootMigration(paths);
                Assert(migration.Queue(Target(paths), manager) is null);
                var request = migration.Snapshot.Migration!;
                if (existingMarker)
                {
                    using var cancel = new CancellationTokenSource();
                    Reject<OperationCanceledException>(() => migration.ApplyAtStartup(manager,
                        new CallbackProgress(_ => cancel.Cancel()), cancel.Token));
                    Assert(File.Exists(Path.Combine(Target(paths), ".tractortool-migration.json")));
                }
                var pending = ".tractortool-migration.json." + request.Id + ".tmp";
                Write(Target(paths), pending, "{ interrupted metadata");
                var result = migration.ApplyAtStartup(manager, null, default); using var lease = result.TargetLease;
                Assert(result.Error is null && migration.Snapshot.Migration is null && migration.Snapshot.DataRoot == Target(paths));
                Assert(!File.Exists(Path.Combine(Target(paths), pending)));
                Assert(new FileInfo(Path.Combine(result.Paths.ConfigRoot, "marker-fixture.bin")).Length == 1024 * 1024);
            });
        check("Python migration rebuilds at the final path and records independent generated-file evidence", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility);
            var environment = PythonFixture(paths);
            Write(paths.ConfigRoot, "business.txt", "ordinary data");
            var rebuilds = 0;
            var migration = new DataRootMigration(paths, (packages, item, token) =>
            {
                token.ThrowIfCancellationRequested(); rebuilds++;
                WritePythonFixture(packages, item); return Task.CompletedTask;
            });
            Assert(migration.Queue(Target(paths), manager) is null);
            var result = migration.ApplyAtStartup(manager, null, default); using var lease = result.TargetLease;
            Assert(result.Error is null && rebuilds == 1 && migration.Snapshot.DataRoot == Target(paths));
            Assert(!Directory.Exists(paths.PackageRoot));
            Assert(File.ReadAllText(Path.Combine(result.Paths.ConfigRoot, "business.txt")) == "ordinary data");
            var targetEnvironment = Path.Combine(result.Paths.PackageRoot, environment.RelativeDirectory);
            var record = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(targetEnvironment, "environment.json")))!;
            var generation = record["generation"]!.GetValue<string>();
            Assert(File.ReadAllText(Path.Combine(targetEnvironment, generation, "runtime.txt")) == Path.Combine(targetEnvironment, generation));
            var journal = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(Target(paths), ".tractortool-migration.json")))!;
            Assert(journal["pythonRebuilds"]!.AsArray().Count == 1 && journal["pythonRebuilds"]![0]!["targetHashes"] is not null);
        });
        check("Mixed Python and Node migration resumes without rebuilding an already verified environment", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility);
            var python = PythonFixture(paths);
            var node = NodeEnvironmentChecks.WriteFixture(paths.DataRoot).Environment;
            Write(paths.ConfigRoot, "mixed-business.txt", "keep until both environments validate");
            var oldLog = new TractorTool.Core.Logging.AppLog(paths.LogRoot);
            oldLog.Write("Host", "retention fixture");
            File.SetLastWriteTimeUtc(oldLog.FilePath, DateTime.UtcNow.AddDays(-30));
            var pythonCalls = 0; var nodeCalls = 0;
            var migration = new DataRootMigration(paths,
                (packages, item, token) => { pythonCalls++; WritePythonFixture(packages, item); return Task.CompletedTask; },
                (packages, item, token) =>
                {
                    nodeCalls++;
                    if (nodeCalls == 1) throw new IOException("fixture Node rebuild interrupted");
                    NodeEnvironmentChecks.WriteInstalled(packages, item); return Task.CompletedTask;
                });
            Assert(migration.Queue(Target(paths), manager) is null);
            var interrupted = migration.ApplyAtStartup(manager, null, default);
            Assert(interrupted.Error is not null && pythonCalls == 1 && nodeCalls == 1 && migration.Snapshot.DataRoot is null);
            Assert(Directory.Exists(Path.Combine(paths.PackageRoot, python.RelativeDirectory)) &&
                Directory.Exists(Path.Combine(paths.PackageRoot, node.RelativeDirectory)));
            // A failed real startup opens the original root and writes a fresh session log.
            var startupLog = new TractorTool.Core.Logging.AppLog(paths.LogRoot, pruneFiles: interrupted.Error is null);
            startupLog.Write("Storage", interrupted.Error!);
            startupLog.Write("Host", "Started on the original data root");
            var logName = Path.GetFileName(startupLog.FilePath);
            var logText = File.ReadAllText(startupLog.FilePath);
            Assert(File.Exists(oldLog.FilePath));
            Write(Path.Combine(paths.DataRoot, "Cache"), "after-failure.bin", "disposable cache");
            migration.Retry();
            var resumed = migration.ApplyAtStartup(manager, null, default); using var lease = resumed.TargetLease;
            Assert(resumed.Error is null && pythonCalls == 1 && nodeCalls == 2 && !Directory.Exists(paths.PackageRoot));
            Assert(File.ReadAllText(Path.Combine(resumed.Paths.ConfigRoot, "mixed-business.txt")) == "keep until both environments validate");
            Assert(File.ReadAllText(Path.Combine(resumed.Paths.LogRoot, logName)) == logText);
            var journal = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(Target(paths), ".tractortool-migration.json")))!;
            Assert(journal["pythonRebuilds"]![0]!["targetHashes"] is not null && journal["nodeRebuilds"]![0]!["targetHashes"] is not null);
        });
        foreach (var change in new[] { "source", "target", "new-conflict" })
        check("Language migration retry protects ordinary data: " + change, () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility);
            _ = PythonFixture(paths);
            Write(paths.ConfigRoot, "business.txt", "original");
            var attempts = 0;
            var migration = new DataRootMigration(paths, (packages, item, _) =>
            {
                if (++attempts == 1) throw new IOException("interrupted rebuild");
                WritePythonFixture(packages, item); return Task.CompletedTask;
            });
            Assert(migration.Queue(Target(paths), manager) is null);
            Assert(migration.ApplyAtStartup(manager, null, default).Error is not null);
            var relative = change == "new-conflict" ? "new.txt" : "business.txt";
            var sourceFile = Path.Combine(paths.ConfigRoot, relative);
            var targetFile = Path.Combine(Target(paths), "Config", relative);
            if (change == "new-conflict") File.WriteAllText(sourceFile, "new source");
            var edited = change == "source" ? sourceFile : targetFile;
            var modified = File.GetLastWriteTimeUtc(edited);
            File.WriteAllText(edited, "modified");
            if (change != "new-conflict") File.SetLastWriteTimeUtc(edited, modified);
            migration.Retry();
            Assert(migration.ApplyAtStartup(manager, null, default).Error is not null);
            Assert(attempts == 1 && migration.Snapshot.DataRoot is null);
            Assert(File.ReadAllText(edited) == "modified" && Directory.Exists(paths.PackageRoot));
        });
        check("Interrupted Python rebuilding preserves source and resumes from retained original inputs", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility);
            var environment = PythonFixture(paths);
            Write(paths.ConfigRoot, "business.txt", "ordinary data");
            var attempts = 0;
            var migration = new DataRootMigration(paths, (packages, item, token) =>
            {
                token.ThrowIfCancellationRequested(); attempts++;
                if (attempts == 1)
                {
                    var identity = Path.Combine(packages, item.RelativeDirectory);
                    var generation = Guid.NewGuid().ToString("N");
                    var partial = Path.Combine(identity, generation);
                    Write(partial, ".owner", "TractorTool.PythonEnvironment.v1\n" + item.Definition.Identity);
                    Write(partial, "partial.bin", "interrupted");
                    var files = JsonSerializer.Serialize(new[] { new { Path = "partial.bin", Length = 11, Sha256 = Digest("interrupted") } }, JsonOptions.Value);
                    Write(identity, generation + ".files.json", files);
                    // Model interruption after full-file ownership is saved but before the new generation is published.
                    var record = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(identity, "environment.json")))!;
                    record["generationHashes"]![generation] = Digest(files);
                    record["preparingGenerations"] = JsonSerializer.SerializeToNode(new[] { generation });
                    Write(identity, "environment.json", record.ToJsonString(JsonOptions.Value));
                    throw new IOException("fixture interrupted rebuild");
                }
                WritePythonFixture(packages, item); return Task.CompletedTask;
            });
            Assert(migration.Queue(Target(paths), manager) is null);
            var failed = migration.ApplyAtStartup(manager, null, default);
            Assert(failed.Error is not null && migration.Snapshot.DataRoot is null);
            Assert(Directory.Exists(Path.Combine(paths.PackageRoot, environment.RelativeDirectory)));
            Assert(File.ReadAllText(Path.Combine(paths.ConfigRoot, "business.txt")) == "ordinary data");
            migration.Retry();
            var resumed = migration.ApplyAtStartup(manager, null, default); using var lease = resumed.TargetLease;
            Assert(resumed.Error is null && attempts == 2 && migration.Snapshot.Migration is null);
        });
        foreach (var changeExisting in new[] { false, true })
        check("Node migration never adopts " + (changeExisting ? "rewritten" : "unknown") + " files in a copied retired generation", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility);
            var fixture = NodeEnvironmentChecks.WriteFixture(paths.DataRoot);
            Write(paths.ConfigRoot, "node-business.txt", "source stays until target is validated");
            var attempts = 0;
            var migration = new DataRootMigration(paths, rebuildNodeEnvironment: (packages, item, token) =>
            {
                if (++attempts == 1) throw new IOException("fixture interrupted before Node rebuild");
                NodeEnvironmentChecks.WriteInstalled(packages, item); return Task.CompletedTask;
            });
            Assert(migration.Queue(Target(paths), manager) is null);
            Assert(migration.ApplyAtStartup(manager, null, default).Error is not null);
            var copied = Path.Combine(Target(paths), "Packages", fixture.Environment.RelativeDirectory, Path.GetFileName(fixture.Generation));
            var contaminated = changeExisting
                ? Path.Combine(copied, "node", $"node-v{fixture.Environment.Definition.NodeVersion}-win-x64", "node.exe")
                : Path.Combine(copied, "user-added.txt");
            var content = changeExisting ? "X" + File.ReadAllText(contaminated)[1..] : "do not adopt or delete";
            File.WriteAllText(contaminated, content);
            migration.Retry();
            var rejected = migration.ApplyAtStartup(manager, null, default);
            Assert(rejected.Error is not null && migration.Snapshot.DataRoot is null && attempts == 2);
            Assert(File.ReadAllText(contaminated) == content);
            Assert(Directory.Exists(fixture.Identity) && File.ReadAllText(Path.Combine(paths.ConfigRoot, "node-business.txt")) == "source stays until target is validated");
            var journal = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(Target(paths), ".tractortool-migration.json")))!;
            Assert(journal["nodeRebuilds"]![0]!["targetHashes"] is null);
        });
        check("A changed rebuilt Python target blocks source cleanup after the bootstrap commit", () =>
        {
            var paths = Paths(); using var manager = new PluginManager(paths, compatibility);
            var environment = PythonFixture(paths);
            Write(paths.ConfigRoot, "business.txt", "keep until validated");
            var migration = new DataRootMigration(paths, (packages, item, token) =>
            { WritePythonFixture(packages, item); return Task.CompletedTask; });
            Assert(migration.Queue(Target(paths), manager) is null);
            using var cancel = new CancellationTokenSource();
            Reject<OperationCanceledException>(() => migration.ApplyAtStartup(manager,
                new CallbackProgress(message => { if (message == "正在清理旧应用数据…") cancel.Cancel(); }), cancel.Token));
            var next = new AppPaths(paths.AppRoot, paths.BootstrapRoot, Target(paths));
            Assert(migration.Snapshot.DataRoot == next.DataRoot);
            var targetEnvironment = Path.Combine(next.PackageRoot, environment.RelativeDirectory);
            var record = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(targetEnvironment, "environment.json")))!;
            var runtime = Path.Combine(targetEnvironment, record["generation"]!.GetValue<string>(), "runtime.txt");
            var original = File.ReadAllText(runtime); var modified = File.GetLastWriteTimeUtc(runtime);
            File.WriteAllText(runtime, new string('x', original.Length)); File.SetLastWriteTimeUtc(runtime, modified);
            var reopened = new DataRootMigration(next);
            Reject<IOException>(() => reopened.ApplyAtStartup(manager, null, default));
            Assert(File.ReadAllText(Path.Combine(paths.ConfigRoot, "business.txt")) == "keep until validated");
            Assert(Directory.Exists(Path.Combine(paths.PackageRoot, environment.RelativeDirectory)));
            File.WriteAllText(runtime, original); File.SetLastWriteTimeUtc(runtime, modified);
            Assert(reopened.ApplyAtStartup(manager, null, default).Error is null && reopened.Snapshot.Migration is null);
        });
    }

    private static void Write(string directory, string file, string text)
    { Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, file), text); }

    // A synthetic installation record exercises the storage transaction without starting Python.
    // Actual installation/version/process validation belongs to the separate Python checks.
    private static PythonMigrationEnvironment PythonFixture(AppPaths paths)
    {
        var input = Path.Combine(paths.AppRoot, "python-input");
        var project = "[project]\nname = \"migration-fixture\"\nversion = \"1.0.0\"\n";
        var locked = "version = 1\nrevision = 3\n";
        Write(input, "pyproject.toml", project); Write(input, "uv.lock", locked);
        var declaration = new PythonEnvironmentDeclaration
        {
            Id = "worker", PythonVersion = PythonEnvironmentDefinitions.SupportedPythonVersion,
            Project = "pyproject.toml", Lock = "uv.lock", ProjectSha256 = Digest(project), LockSha256 = Digest(locked)
        };
        var definition = PythonEnvironmentDefinitions.Snapshot(declaration, "MigrationFixture", "1.0.0", "迁移夹具", input, "win-x64");
        var environment = new PythonMigrationEnvironment(Path.Combine("uv", "Environments", definition.PluginId, definition.Identity), definition);
        WritePythonFixture(paths.PackageRoot, environment);
        return environment;
    }

    private static void WritePythonFixture(string packages, PythonMigrationEnvironment environment)
    {
        Write(Path.Combine(packages, "uv"), ".python-packages", "TractorTool.PythonPackages.v1");
        var identity = Path.Combine(packages, environment.RelativeDirectory);
        var owner = "TractorTool.PythonEnvironment.v1\n" + environment.Definition.Identity;
        Write(identity, ".owner", owner);
        // Retain retired payload just as production does when cleanup has not completed;
        // deleting it here would hide migration validation bugs.
        var hashes = new Dictionary<string, string>();
        var recordPath = Path.Combine(identity, "environment.json");
        if (File.Exists(recordPath))
        {
            using var record = JsonDocument.Parse(File.ReadAllText(recordPath));
            foreach (var entry in record.RootElement.GetProperty("generationHashes").EnumerateObject())
                hashes.Add(entry.Name, entry.Value.GetString()!);
        }
        var generation = Guid.NewGuid().ToString("N"); var directory = Path.Combine(identity, generation);
        Write(directory, ".owner", owner); Write(directory, "runtime.txt", directory);
        var length = Encoding.UTF8.GetByteCount(directory);
        var inventory = JsonSerializer.Serialize(new[] { new { Path = "runtime.txt", Length = length, Sha256 = Digest(directory) } }, JsonOptions.Value);
        Write(identity, generation + ".files.json", inventory);
        hashes.Add(generation, Digest(inventory));
        Write(identity, "environment.json", JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, environment.Definition, Generation = generation, InstalledRoot = directory,
            ManifestHash = Digest(inventory), InstalledBytes = length,
            GenerationHashes = hashes, PreparingGenerations = Array.Empty<string>()
        }, JsonOptions.Value));
    }

    private static string Digest(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static void Assert(bool value) { if (!value) throw new Exception("Assertion failed"); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private sealed class CallbackProgress(Action<string> report) : IProgress<string>
    { public void Report(string value) => report(value); }
}
