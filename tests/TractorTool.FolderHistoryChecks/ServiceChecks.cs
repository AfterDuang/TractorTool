using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TractorTool.Contracts;
using TractorTool.Contracts.Packages;
using TractorTool.Contracts.Processes;
using TractorTool.Contracts.Tasks;
using TractorTool.Plugins.FolderHistory.Models;
using TractorTool.Plugins.FolderHistory.Processing;
using TractorTool.Plugins.FolderHistory.Services;
using TractorTool.Plugins.FolderHistory.Storage;

internal static class ServiceChecks
{
    internal static async Task RunAsync(string fixture)
    {
        CheckSchedulingAndRetention();
        var root = Directory.CreateDirectory(Path.Combine(fixture, "service")).FullName;
        var host = new TestHost(root);
        using var service = new FolderHistoryService(new("checks", host, host, host, CancellationToken.None)
        {
            Packages = host, Processes = host, Tasks = host
        });

        var broken = Directory.CreateDirectory(Path.Combine(root, "broken")).FullName;
        var history = Directory.CreateDirectory(HistoryPaths.HistoryDirectory(broken)).FullName;
        var metadata = Path.Combine(history, "history.json");
        await File.WriteAllTextAsync(metadata, "{}");
        await ThrowsAsync<JsonException>(() => service.AddFolderAsync(broken));
        Assert(await File.ReadAllTextAsync(metadata) == "{}" && Directory.GetFileSystemEntries(history).Length == 1,
            "missing fields caused a broken repository to be rewritten");

        var future = JsonSerializer.Serialize(new { schemaVersion = 3, id = Guid.NewGuid(), engine = "Duplicati", versions = Array.Empty<object>() });
        await File.WriteAllTextAsync(metadata, future);
        await ThrowsAsync<InvalidDataException>(() => service.AddFolderAsync(broken));
        Assert(await File.ReadAllTextAsync(metadata) == future && Directory.GetFileSystemEntries(history).Length == 1,
            "unknown schema caused a future repository to be rewritten");
        Assert((await service.GetFoldersAsync()).Count == 0, "invalid repository was added to the registry");

        var incompleteRules = JsonSerializer.Serialize(new { schemaVersion = 2, id = Guid.NewGuid(), engine = "Duplicati", versions = Array.Empty<object>() });
        await File.WriteAllTextAsync(metadata, incompleteRules);
        await ThrowsAsync<InvalidDataException>(() => service.AddFolderAsync(broken));
        Assert(await File.ReadAllTextAsync(metadata) == incompleteRules, "schema 2 without rules was silently accepted or rewritten");

        var legacy = Directory.CreateDirectory(Path.Combine(root, "legacy")).FullName;
        var legacyHistory = Directory.CreateDirectory(HistoryPaths.HistoryDirectory(legacy)).FullName;
        var legacyMetadata = Path.Combine(legacyHistory, "history.json");
        var legacyJson = JsonSerializer.Serialize(new { schemaVersion = 1, id = Guid.NewGuid(), engine = "Duplicati", versions = Array.Empty<object>() });
        await File.WriteAllTextAsync(legacyMetadata, legacyJson);
        await service.AddFolderAsync(legacy);
        Assert(!(await service.GetFoldersAsync()).Single().ProtectionEnabled && await File.ReadAllTextAsync(legacyMetadata) == legacyJson,
            "reading a legacy repository enabled protection or upgraded it without a write");
        var rules = new FolderSettings { ExcludedPaths = ["cache", "cache/child"] };
        await service.SaveSettingsAsync(legacy, rules);
        rules.ExcludedPaths[0] = "changed-by-caller";
        var readRules = await service.GetSettingsAsync(legacy);
        Assert(readRules.ExcludedPaths.SequenceEqual(new[] { "cache" }), "settings did not freeze normalized exclusions");
        readRules.ExcludedPaths[0] = "changed-return-value";
        Assert((await service.GetSettingsAsync(legacy)).ExcludedPaths.SequenceEqual(new[] { "cache" }), "returned settings changed persisted rules");
        var upgraded = JsonNode.Parse(await File.ReadAllTextAsync(legacyMetadata))!;
        Assert(upgraded["schemaVersion"]!.GetValue<int>() == 2 && !upgraded["protectionEnabled"]!.GetValue<bool>(), "legacy write failed to upgrade safely");
        upgraded["schemaVersion"] = 1;
        await File.WriteAllTextAsync(legacyMetadata, upgraded.ToJsonString());
        await ThrowsAsync<InvalidDataException>(() => service.GetSettingsAsync(legacy));
        await service.RemoveFolderAsync(legacy);

        var initial = Directory.CreateDirectory(Path.Combine(root, "initial-exclusion")).FullName;
        var ignored = Path.Combine(initial, "ignored.txt");
        await File.WriteAllTextAsync(ignored, "not protected");
        var initialRules = new FolderSettings { ExcludedPaths = ["ignored.txt"] };
        using (var blocked = new FileStream(ignored, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var preparation = await service.InspectFolderAsync(initial, initialRules);
            Assert(preparation.FileCount == 0 && preparation.TotalBytes == 0, "initial exclusions were ignored by preparation scan");
            await service.AddFolderAsync(initial, initialRules);
        }
        Assert((await service.GetSettingsAsync(initial)).ExcludedPaths.SequenceEqual(new[] { "ignored.txt" }), "initial exclusion was not saved");
        await service.RemoveFolderAsync(initial);
        await service.AddFolderAsync(initial, new FolderSettings { ExcludedPaths = ["different.txt"] });
        Assert((await service.GetSettingsAsync(initial)).ExcludedPaths.SequenceEqual(new[] { "ignored.txt" }), "reassociation silently replaced existing rules");
        await service.RemoveFolderAsync(initial);

        var first = Directory.CreateDirectory(Path.Combine(root, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(root, "second")).FullName;
        await File.WriteAllTextAsync(Path.Combine(first, "file.txt"), "work");
        await service.AddFolderAsync(first);
        await service.AddFolderAsync(second);
        var id = (await service.GetFoldersAsync()).Single(folder => folder.Root == first).Id;

        if (OperatingSystem.IsWindows())
        {
            using var locked = File.Open(Path.Combine(HistoryPaths.HistoryDirectory(first), "operation.lock"),
                FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            await ThrowsAsync<IOException>(() => service.GetVersionsAsync(first));
        }

        var moved = Path.Combine(root, "moved");
        Directory.Move(first, moved);
        await ThrowsAsync<InvalidOperationException>(() => service.AddFolderAsync(moved));
        await service.RemoveFolderAsync(first);
        await service.AddFolderAsync(moved);
        Assert((await service.GetFoldersAsync()).Single(folder => folder.Root == moved).Id == id, "moving the directory changed the repository identity");
        if (OperatingSystem.IsWindows())
        {
            using var writer = File.Open(Path.Combine(moved, "file.txt"), FileMode.Open, FileAccess.Write, FileShare.None);
            await service.RemoveFolderAsync(moved);
            var existingPreparation = await service.InspectFolderAsync(moved);
            Assert(existingPreparation.ExistingHistory && !existingPreparation.WorkingTreeScanned,
                "reassociation tried to scan locked work files or reported fabricated file counts");
            await service.AddFolderAsync(moved);
            Assert((await service.GetFoldersAsync()).Single(folder => folder.Root == moved).Id == id,
                "reconnecting an existing repository required reading its working files");
        }

        var secondMetadata = Path.Combine(HistoryPaths.HistoryDirectory(second), "history.json");
        var replacement = JsonNode.Parse(await File.ReadAllTextAsync(secondMetadata))!;
        replacement["id"] = Guid.NewGuid().ToString();
        await File.WriteAllTextAsync(secondMetadata, replacement.ToJsonString());
        var folders = await service.GetFoldersAsync();
        Assert(folders.Count == 2 && folders.Single(folder => folder.Root == second).Status.StartsWith("需要处理", StringComparison.Ordinal) &&
            folders.Single(folder => folder.Root == moved).VersionCount == 0 && !folders.Single(folder => folder.Root == moved).ProtectionEnabled,
            "an invalid association hid the healthy repository or marked an unprotected folder as protected");
        await service.RemoveFolderAsync(second);
        Assert((await service.GetFoldersAsync()).Count == 1 && File.Exists(secondMetadata), "removing an invalid association deleted its repository");

        await ThrowsAsync<OperationCanceledException>(() => service.SaveAsync(moved, "declined"));
        await ThrowsAsync<OperationCanceledException>(() => service.SetProtectionEnabledAsync(moved, true));
        Assert(!(await service.GetFoldersAsync()).Single().ProtectionEnabled, "dependency refusal marked the folder as protected");
        Assert(host.Queued == 0 && host.Processes == 0, "dependency refusal started a task or process");
        host.AllowPackage = true;
        await ThrowsAsync<IOException>(() => service.SaveAsync(moved, "fake success"));
        Assert(host.Queued == 1 && host.Processes == 1 && !host.Leased && (await service.GetVersionsAsync(moved)).Count == 0,
            "a successful process with no version was accepted or left a live lease");

        host.ProcessResult = new(2, "diagnostic stdout", "diagnostic stderr", false, false);
        var failure = await ThrowsAsync<IOException>(() => service.SaveAsync(moved, "diagnostic failure"));
        Assert(!failure.Message.Contains("diagnostic stdout", StringComparison.Ordinal) &&
            host.TaskError!.ToString().Contains("diagnostic stdout", StringComparison.Ordinal) &&
            host.TaskError.ToString().Contains("diagnostic stderr", StringComparison.Ordinal) &&
            host.TaskError.ToString().Contains("Command: backup", StringComparison.Ordinal),
            "task summary exposed command output or task logging lost the full diagnostics");

        var movedHistory = HistoryPaths.HistoryDirectory(moved);
        var repository = Path.Combine(movedHistory, "repository");
        const string goodId = "duplicati-20261007T120000Z.dlist.zip";
        var badVolume = Path.Combine(repository, "duplicati-20261007T130000Z.dlist.zip");
        var folder = new { type = "Folder", path = moved + Path.DirectorySeparatorChar };
        var workFile = Path.Combine(moved, "file.txt");
        EngineChecks.WriteVolume(Path.Combine(repository, goodId), true, "SHA256", folder,
            new { type = "File", path = workFile, size = new FileInfo(workFile).Length, hash = Convert.ToBase64String(SHA256.HashData(await File.ReadAllBytesAsync(workFile))) });
        var movedMetadata = Path.Combine(movedHistory, "history.json");
        var committed = JsonNode.Parse(await File.ReadAllTextAsync(movedMetadata))!;
        committed["versions"] = JsonSerializer.SerializeToNode(new[] { new { id = goodId, sourceRoot = moved, note = "registered", pinned = true, restoreProtection = false } });
        await File.WriteAllTextAsync(movedMetadata, committed.ToJsonString());
        await File.WriteAllTextAsync(workFile, "changed work");
        foreach (var malformed in new[] { true, false })
        {
            if (malformed) await File.WriteAllTextAsync(badVolume, "invalid unregistered zip");
            else EngineChecks.WriteVolume(badVolume, true, "SHA256", folder, new { type = "Symlink", path = workFile });
            var versions = await service.GetVersionsAsync(moved);
            Assert(versions.Count == 1 && versions[0].Id == goodId && versions[0].Pinned &&
                (await service.GetFoldersAsync()).Single().VersionCount == 1, "an unregistered bad volume hid the registered good version");
            var processCount = host.Processes;
            await ThrowsAsync<IOException>(() => service.SaveAsync(moved, "must refuse damaged repository"));
            Assert(host.Processes == processCount && (await service.GetVersionsAsync(moved)).Count == 1 && File.Exists(badVolume),
                "saving a damaged repository ran the engine, registered a version or removed the damaged volume");
        }
        await ThrowsAsync<InvalidOperationException>(() => service.GetCleanupPreviewAsync(moved, deleteAll: true));
        var beforeDeletionRequests = host.Processes;
        await ThrowsAsync<InvalidOperationException>(() => service.DeleteVersionsAsync(moved, new[] { goodId }));
        Assert(host.Processes == beforeDeletionRequests && File.Exists(Path.Combine(repository, goodId)), "manual deletion bypassed important-version protection");
        await service.SetPinnedAsync(moved, goodId, false);
        const string addedAfterPreview = "duplicati-20261007T140000Z.dlist.zip";
        EngineChecks.WriteVolume(Path.Combine(repository, addedAfterPreview), true, "SHA256", folder);
        committed = JsonNode.Parse(await File.ReadAllTextAsync(movedMetadata))!;
        committed["versions"]!.AsArray().Add(JsonSerializer.SerializeToNode(new { id = addedAfterPreview, sourceRoot = moved, note = "after confirmation", pinned = false, restoreProtection = false }));
        await File.WriteAllTextAsync(movedMetadata, committed.ToJsonString());
        await ThrowsAsync<InvalidOperationException>(() => service.DeleteVersionsAsync(moved, new[] { goodId }, deleteAll: true));
        Assert(host.Processes == beforeDeletionRequests && File.Exists(Path.Combine(repository, goodId)) && File.Exists(Path.Combine(repository, addedAfterPreview)),
            "delete-all confirmation silently changed scope after a new version appeared");
        await File.WriteAllTextAsync(badVolume, "damaged manifest volume");
        await ThrowsAsync<InvalidDataException>(() => service.DeleteVersionsAsync(moved, new[] { goodId }));
        Assert(!(await service.GetFoldersAsync()).Single().HasPendingCleanup && host.Processes == beforeDeletionRequests,
            "read-only cleanup preflight rejection created a pending deletion or ran a process");
        File.Delete(badVolume);
        var nativeDeleteStarted = false;
        host.ProcessHandler = (request, _) =>
        {
            nativeDeleteStarted |= request.Arguments[0] is "delete" or "compact";
            return Task.FromResult(new ProcessResult(2, "repair failed before native deletion", "", false, false));
        };
        await ThrowsAsync<IOException>(() => service.DeleteVersionsAsync(moved, new[] { goodId }));
        Assert(!nativeDeleteStarted && !(await service.GetFoldersAsync()).Single().HasPendingCleanup && File.Exists(Path.Combine(repository, goodId)),
            "repair failure before native deletion froze the repository or deleted a version");
        await service.StopAsync();
        await CheckBackgroundLifecycleAsync(fixture);
        await CheckAutomaticCleanupContinuesAsync(fixture);
        await CheckReassociationStartsPausedAsync(fixture);
        await CheckRestoreRequiresReadableProtectionAsync(fixture);
    }

    private static async Task CheckReassociationStartsPausedAsync(string fixture)
    {
        var root = Directory.CreateDirectory(Path.Combine(fixture, "reassociation-paused")).FullName;
        var host = new TestHost(root);
        using var service = new FolderHistoryService(new("checks", host, host, host, CancellationToken.None)
        { Packages = host, Processes = host, Tasks = host });
        var work = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
        await service.AddFolderAsync(work);
        await service.RemoveFolderAsync(work);
        var history = HistoryPaths.HistoryDirectory(work);
        var repository = Directory.CreateDirectory(Path.Combine(history, "repository")).FullName;
        const string id = "duplicati-20261007T120000Z.dlist.zip";
        EngineChecks.WriteVolume(Path.Combine(repository, id), true, "SHA256", new { type = "Folder", path = work + Path.DirectorySeparatorChar });
        var metadata = Path.Combine(history, "history.json");
        var state = JsonNode.Parse(await File.ReadAllTextAsync(metadata))!;
        state["versions"] = JsonSerializer.SerializeToNode(new[] { new { id, sourceRoot = work, note = "prior complete version", pinned = false, restoreProtection = false } });
        state["protectionEnabled"] = true;
        state["settings"]!["keepLatestVersions"] = 1;
        await File.WriteAllTextAsync(metadata, state.ToJsonString());
        try
        {
            host.FailSettingsSave = true;
            await ThrowsAsync<IOException>(() => service.AddFolderAsync(work));
            Assert(!JsonNode.Parse(await File.ReadAllTextAsync(metadata))!["protectionEnabled"]!.GetValue<bool>() &&
                (await service.GetFoldersAsync()).Count == 0 && File.Exists(Path.Combine(repository, id)),
                "failed association publication left persisted automatic capture or cleanup enabled");
            host.FailSettingsSave = false;
            state["protectionEnabled"] = true;
            await File.WriteAllTextAsync(metadata, state.ToJsonString());
            await service.AddFolderAsync(work);
            Assert(!(await service.GetFoldersAsync()).Single().ProtectionEnabled &&
                !JsonNode.Parse(await File.ReadAllTextAsync(metadata))!["protectionEnabled"]!.GetValue<bool>() && host.PackageRequests == 0 && host.Queued == 0,
                "an association published before the optional enable step could resume unattended protection");
        }
        finally { host.FailSettingsSave = false; await service.StopAsync(); }
    }

    private static async Task CheckBackgroundLifecycleAsync(string fixture)
    {
        var root = Directory.CreateDirectory(Path.Combine(fixture, "automatic-service")).FullName;
        var host = new TestHost(root);
        using var service = new FolderHistoryService(new("checks", host, host, host, CancellationToken.None)
        { Packages = host, Processes = host, Tasks = host });
        var work = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
        await service.AddFolderAsync(work, new FolderSettings { StableSeconds = 1, MinimumIntervalSeconds = 1 });
        var history = HistoryPaths.HistoryDirectory(work);
        var repository = Directory.CreateDirectory(Path.Combine(history, "repository")).FullName;
        const string id = "duplicati-20261007T120000Z.dlist.zip";
        EngineChecks.WriteVolume(Path.Combine(repository, id), true, "SHA256", new { type = "Folder", path = work + Path.DirectorySeparatorChar });
        var metadata = Path.Combine(history, "history.json");
        var state = JsonNode.Parse(await File.ReadAllTextAsync(metadata))!;
        state["versions"] = JsonSerializer.SerializeToNode(new[] { new { id, sourceRoot = work, note = "prior complete version", pinned = false, restoreProtection = false } });
        state["protectionEnabled"] = true;
        await File.WriteAllTextAsync(metadata, state.ToJsonString());
        var run = service.RunAsync(CancellationToken.None);
        try
        {
            await host.AutomaticPaused.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert(host.PackageRequests == 0 && host.Queued == 0 && host.Processes == 0,
                "background dependency failure requested installation or started a task/process");
            Assert((await service.GetFoldersAsync()).Single().NeedsAttention, "automatic failure did not leave a visible retry state");
            Assert(!run.IsCompleted, "background failure ended the plugin lifetime loop");
        }
        finally { await service.StopAsync(); }
        Assert(run.IsCompleted, "stop did not drain the scheduler");
        await ThrowsAsync<OperationCanceledException>(() => service.GetFoldersAsync());
    }

    private static void CheckSchedulingAndRetention()
    {
        var start = new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
        var settings = new FolderSettings();
        var schedule = new CaptureSchedule();
        schedule.Signal(start);
        schedule.Signal(start.AddSeconds(3));
        Assert(schedule.Pending && schedule.DueAt(settings) == start.AddSeconds(8), "repeated writes did not extend the stability window");
        var captured = schedule.Generation;
        schedule.Signal(start.AddSeconds(10));
        schedule.Complete(captured, start.AddSeconds(12));
        Assert(schedule.Pending && schedule.DueAt(settings) == start.AddSeconds(72), "writes during capture were lost or minimum interval was skipped");
        schedule.Complete(schedule.Generation, start.AddSeconds(72));
        Assert(!schedule.Pending, "unchanged final state remained scheduled");
        schedule.Defer(start.AddSeconds(80));
        Assert(schedule.Pending && schedule.DueAt(settings) == start.AddSeconds(140), "a changing source was faulted or retried before the minimum interval");

        SavedVersion Version(string id, int age, bool pinned = false, bool protection = false) =>
            new(new(id, start.AddDays(-age), "root", []), "", pinned) { IsRestoreProtection = protection };
        SavedVersion[] versions = [Version("latest", 1), Version("recent", 2), Version("old", 30), Version("important", 40, true), Version("recovery", 50, false, true)];
        Assert(HistoryRetention.Candidates(versions, settings, start).Count == 0, "default retention deleted history");
        Assert(HistoryRetention.Candidates(versions, settings with { KeepLatestVersions = 1 }, start).Select(v => v.Id).SequenceEqual(new[] { "recent", "old" }),
            "retention did not protect latest, important and recovery versions");
        Assert(HistoryRetention.Candidates(versions, settings with { KeepLatestVersions = 1, KeepDays = 7 }, start).Single().Id == "old",
            "count and age rules did not retain versions satisfying either rule");
    }

    private static async Task CheckAutomaticCleanupContinuesAsync(string fixture)
    {
        var root = Directory.CreateDirectory(Path.Combine(fixture, "automatic-cleanup")).FullName;
        var host = new TestHost(root) { AllowPackage = true };
        using var service = new FolderHistoryService(new("checks", host, host, host, CancellationToken.None)
        { Packages = host, Processes = host, Tasks = host });
        var work = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
        await service.AddFolderAsync(work, new FolderSettings { StableSeconds = 1, MinimumIntervalSeconds = 1, KeepLatestVersions = 1 });
        var history = HistoryPaths.HistoryDirectory(work);
        var repository = Directory.CreateDirectory(Path.Combine(history, "repository")).FullName;
        string[] ids = ["duplicati-20261007T120000Z.dlist.zip", "duplicati-20261007T130000Z.dlist.zip"];
        foreach (var id in ids) EngineChecks.WriteVolume(Path.Combine(repository, id), true, "SHA256", new { type = "Folder", path = work + Path.DirectorySeparatorChar });
        var metadata = Path.Combine(history, "history.json");
        var state = JsonNode.Parse(await File.ReadAllTextAsync(metadata))!;
        state["versions"] = JsonSerializer.SerializeToNode(ids.Select(id => new { id, sourceRoot = work, note = "prior complete", pinned = false, restoreProtection = false }));
        state["protectionEnabled"] = true;
        await File.WriteAllTextAsync(metadata, state.ToJsonString());
        var enteredDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextBackup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backupAfterFailedRemoval = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backupCount = 0;
        host.ProcessHandler = async (request, cancellation) =>
        {
            if (request.Arguments[0] == "repair")
                await File.WriteAllTextAsync(request.Arguments.Single(argument => argument.StartsWith("--dbpath=", StringComparison.Ordinal))[9..], "fixture index", cancellation);
            else if (request.Arguments[0] == "delete")
            {
                enteredDelete.TrySetResult();
                await releaseDelete.Task.WaitAsync(cancellation);
                var volumes = Directory.GetFiles(repository, "*.dlist.zip").OrderByDescending(path => path, StringComparer.Ordinal).ToArray();
                var selection = request.Arguments.Single(argument => argument.StartsWith("--version=", StringComparison.Ordinal))[10..];
                foreach (var index in selection.Split(',').Select(int.Parse)) File.Delete(volumes[index]);
            }
            else if (request.Arguments[0] == "backup")
            {
                backupCount++;
                var current = await WorkingTree.ScanAsync(work, cancellation);
                var entries = new List<object> { new { type = "Folder", path = work + Path.DirectorySeparatorChar } };
                foreach (var entry in current)
                    entries.Add(entry.IsDirectory ? new { type = "Folder", path = Path.Combine(work, entry.Path) + Path.DirectorySeparatorChar } :
                        new { type = "File", path = Path.Combine(work, entry.Path), size = entry.Length, hash = Convert.ToBase64String(Convert.FromHexString(entry.Hash)) });
                EngineChecks.WriteVolume(Path.Combine(repository, $"duplicati-20261117T0000{backupCount:D2}Z.dlist.zip"), true, "SHA256", entries.ToArray());
                if (backupCount == 1)
                    await File.WriteAllTextAsync(Path.Combine(work, "during-cleanup.txt"), "changed while the first backup was running", cancellation);
                else if (backupCount == 2) nextBackup.TrySetResult();
                else backupAfterFailedRemoval.TrySetResult();
            }
            else throw new InvalidOperationException("unexpected cleanup command");
            return new(0, "", "", false, false);
        };
        _ = service.RunAsync(CancellationToken.None);
        try
        {
            await enteredDelete.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var changeObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service.Changed += OnChanged;
            try
            {
                await File.WriteAllTextAsync(Path.Combine(work, "during-cleanup.txt"), "must be captured after cleanup");
                await changeObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { service.Changed -= OnChanged; }
            releaseDelete.TrySetResult();
            await nextBackup.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert(host.PackageRequests == 0, "automatic cleanup/capture requested package installation");
            Assert(host.TaskError is WorkingTreeChangedException && !(await service.GetFoldersAsync()).Single().NeedsAttention,
                "source-change rejection did not defer and complete a later stable capture");
            host.FailSettingsSave = true;
            await ThrowsAsync<IOException>(() => service.RemoveFolderAsync(work));
            host.FailSettingsSave = false;
            await File.WriteAllTextAsync(Path.Combine(work, "during-cleanup.txt"), "changed after failed association removal");
            await backupAfterFailedRemoval.Task.WaitAsync(TimeSpan.FromSeconds(10));
            void OnChanged(object? sender, EventArgs args) => changeObserved.TrySetResult();
        }
        finally { releaseDelete.TrySetResult(); await service.StopAsync(); }
    }

    private static async Task CheckRestoreRequiresReadableProtectionAsync(string fixture)
    {
        var root = Directory.CreateDirectory(Path.Combine(fixture, "restore-protection")).FullName;
        var host = new TestHost(root) { AllowPackage = true };
        using var service = new FolderHistoryService(new("checks", host, host, host, CancellationToken.None)
        { Packages = host, Processes = host, Tasks = host });
        var work = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
        var workFile = Path.Combine(work, "file.txt");
        await File.WriteAllTextAsync(workFile, "current bytes must survive");
        await service.AddFolderAsync(work);
        var history = HistoryPaths.HistoryDirectory(work);
        var repository = Directory.CreateDirectory(Path.Combine(history, "repository")).FullName;
        const string targetId = "duplicati-20261007T120000Z.dlist.zip";
        const string protectionId = "duplicati-20261007T130000Z.dlist.zip";
        var current = await File.ReadAllBytesAsync(workFile);
        var old = System.Text.Encoding.UTF8.GetBytes("old target");
        var folder = new { type = "Folder", path = work + Path.DirectorySeparatorChar };
        foreach (var item in new[] { (Id: targetId, Bytes: old), (Id: protectionId, Bytes: current) })
            EngineChecks.WriteVolume(Path.Combine(repository, item.Id), true, "SHA256", folder,
                new { type = "File", path = workFile, size = item.Bytes.Length, hash = Convert.ToBase64String(SHA256.HashData(item.Bytes)) });
        var metadata = Path.Combine(history, "history.json");
        var state = JsonNode.Parse(await File.ReadAllTextAsync(metadata))!;
        state["versions"] = JsonSerializer.SerializeToNode(new[] { targetId, protectionId }.Select(id =>
            new { id, sourceRoot = work, note = "manifest readable", pinned = false, restoreProtection = false }));
        await File.WriteAllTextAsync(metadata, state.ToJsonString());
        var protectionReads = 0;
        var targetReads = 0;
        host.ProcessHandler = async (request, cancellation) =>
        {
            Assert(request.Arguments[0] == "restore", "matching protection unexpectedly created another snapshot");
            if (request.Arguments.Contains("--version=0"))
            {
                protectionReads++;
                return new(2, "missing protection blocks", "", false, false);
            }
            targetReads++;
            var staging = request.Arguments.Single(argument => argument.StartsWith("--restore-path=", StringComparison.Ordinal))[15..];
            await File.WriteAllBytesAsync(Path.Combine(staging, "file.txt"), old, cancellation);
            return new(0, "", "", false, false);
        };
        try
        {
            var preview = await service.PreviewRestoreAsync(work, targetId);
            await ThrowsAsync<IOException>(() => service.RestoreInPlaceAsync(preview));
            Assert(protectionReads == 1 && targetReads == 0 && (await File.ReadAllBytesAsync(workFile)).SequenceEqual(current) &&
                InPlaceRestoreTransaction.ReadPending(work) is null,
                "an unreadable protection snapshot allowed target restoration or modified current bytes");

            var plan = await InPlaceRestoreTransaction.PreviewAsync(work, DuplicatiEngine.ReadVersion(history, targetId)!, null, [], CancellationToken.None);
            var operationId = Guid.NewGuid();
            var operation = Directory.CreateDirectory(Path.Combine(history, "restore-pending")).FullName;
            foreach (var area in new[] { "ready", "undo", "discard", "rollback" }) Directory.CreateDirectory(Path.Combine(operation, area));
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            await File.WriteAllTextAsync(Path.Combine(operation, "plan.json"), JsonSerializer.Serialize(
                new { schemaVersion = 2, id = operationId, plan, protectionVersionId = protectionId }, json));
            await File.WriteAllTextAsync(Path.Combine(operation, "operation.json"), JsonSerializer.Serialize(
                new { schemaVersion = 1, id = operationId, status = "Applying", appliedCount = 0, stepStarted = true, rollbackNext = -1, rollbackStarted = false }, json));
            var undo = Path.Combine(operation, "undo", "0");
            File.Move(workFile, undo);
            const string unrelated = "duplicati-20261007T110000Z.dlist.zip";
            await File.WriteAllTextAsync(Path.Combine(repository, unrelated), "damaged unrelated older manifest");
            state = JsonNode.Parse(await File.ReadAllTextAsync(metadata))!;
            state["versions"]!.AsArray().Add(JsonSerializer.SerializeToNode(new { id = unrelated, sourceRoot = work, note = "unrelated", pinned = false, restoreProtection = false }));
            await File.WriteAllTextAsync(metadata, state.ToJsonString());
            Assert((await service.GetFoldersAsync()).Single().HasPendingRestore, "timeline damage hid a validated recovery action");
            await ThrowsAsync<IOException>(() => service.ResumeRestoreAsync(work));
            Assert(protectionReads == 2 && File.Exists(undo) && !File.Exists(workFile) && InPlaceRestoreTransaction.ReadPending(work) is not null,
                "resuming with unreadable protection discarded undo or advanced the interrupted recovery");
            host.AllowPackage = false;
            var packageRequests = host.PackageRequests;
            await service.AbandonRestoreAsync(work);
            Assert(InPlaceRestoreTransaction.ReadPending(work) is null && (await File.ReadAllBytesAsync(workFile)).SequenceEqual(current) && host.PackageRequests == packageRequests,
                "an unrelated damaged version or unavailable engine blocked undoing recovery from preserved originals");
        }
        finally { await service.StopAsync(); }
    }

    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TestHost(string root) : IAppPaths, IPluginSettingsStore, IAppLog, IExternalPackageService, IProcessRunner, IBackgroundTaskService
    {
        private IVersionedSettings? _settings;
        public bool AllowPackage { get; set; }
        public bool Leased { get; private set; }
        public int Queued { get; private set; }
        public int Processes { get; private set; }
        public int PackageRequests { get; private set; }
        public bool FailSettingsSave { get; set; }
        public Func<ProcessRequest, CancellationToken, Task<ProcessResult>>? ProcessHandler { get; set; }
        public TaskCompletionSource AutomaticPaused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProcessResult ProcessResult { get; set; } = new(0, "", "", false, false);
        public Exception? TaskError { get; private set; }
        public string AppRoot => Path.Combine(root, "app");
        public string DataRoot => Path.Combine(root, "data");
        public string PluginRoot => Path.Combine(AppRoot, "Plugins");
        public string PackageRoot => Path.Combine(DataRoot, "Packages");
        public string GetPluginDataDirectory(string pluginId) => Path.Combine(DataRoot, "Data", "Plugins", pluginId);
        public T Load<T>(string name) where T : IVersionedSettings, new() => _settings is T settings ? settings : new T();
        public void Save<T>(string name, T value) where T : IVersionedSettings, new()
        {
            if (FailSettingsSave) throw new IOException("injected settings save failure");
            _settings = value;
        }
        public void Write(string source, string message, Exception? error = null)
        { if (message.Contains("自动保护已暂停", StringComparison.Ordinal)) AutomaticPaused.TrySetResult(); }
        public PackageStatus GetStatus(PackageReference package) => new(package, "win-x64", AllowPackage ? PackagePhase.Ready : PackagePhase.Missing, 0, null, "", [], false, PackageRoot, null);
        public Task<IPackageLease> AcquireAsync(PackageReference package, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!AllowPackage) throw new InvalidOperationException("package missing");
            Leased = true;
            return Task.FromResult<IPackageLease>(new TestLease(this));
        }

        public Task<IPackageLease?> TryRequestAsync(PackageReference package, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PackageRequests++;
            Leased = AllowPackage;
            return Task.FromResult<IPackageLease?>(AllowPackage ? new TestLease(this) : null);
        }

        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert(Leased, "tool ran without a package lease");
            if (ProcessHandler is null) Assert(request.Arguments[0] == "backup", "unexpected tool command");
            Processes++;
            EngineChecks.WriteOperationLog(request);
            return ProcessHandler is null ? ProcessResult : await ProcessHandler(request, cancellationToken);
        }

        public Task<IBackgroundTask> QueueAsync(BackgroundTaskRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Queued++;
            return Task.FromResult<IBackgroundTask>(new TestTask(this, request));
        }

        public Task<IReadOnlyList<IBackgroundTask>> QueueBatchAsync(IReadOnlyList<BackgroundTaskRequest> requests, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private sealed class TestLease(TestHost host) : IPackageLease
        {
            public string Directory => host.PackageRoot;
            public IReadOnlyDictionary<string, string> Tools { get; } = new Dictionary<string, string>
            {
                [DuplicatiEngine.ToolName] = Path.Combine(host.PackageRoot, "Duplicati.CommandLine.exe")
            };
            public void Dispose() => host.Leased = false;
        }

        private sealed class TestTask(TestHost host, BackgroundTaskRequest request) : IBackgroundTask
        {
            private readonly CancellationTokenSource _stop = new();
            public Guid Id { get; } = Guid.NewGuid();
            public void Cancel() => _stop.Cancel();
            public void Dispose() => _stop.Dispose();

            public async Task<BackgroundTaskSnapshot> RunAsync(Func<IProgress<BackgroundTaskProgress>, CancellationToken, Task<string?>> execute)
            {
                try { return Result(BackgroundTaskState.Succeeded, await execute(new Progress<BackgroundTaskProgress>(), _stop.Token), null); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return Result(BackgroundTaskState.Cancelled, null, null); }
                catch (Exception ex)
                {
                    host.TaskError = ex;
                    return Result(BackgroundTaskState.Failed, null, ex.GetBaseException().Message);
                }
            }

            private BackgroundTaskSnapshot Result(BackgroundTaskState state, string? value, string? error) => new(Id,
                "checks", request.BatchId, request.Title, state, null, "", value, error, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        }
    }
}
