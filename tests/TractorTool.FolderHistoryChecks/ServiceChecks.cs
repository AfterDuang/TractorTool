using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TractorTool.Contracts;
using TractorTool.Contracts.Packages;
using TractorTool.Contracts.Processes;
using TractorTool.Contracts.Tasks;
using TractorTool.Plugins.FolderHistory.Processing;
using TractorTool.Plugins.FolderHistory.Services;
using TractorTool.Plugins.FolderHistory.Storage;

internal static class ServiceChecks
{
    internal static async Task RunAsync(string fixture)
    {
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

        var future = JsonSerializer.Serialize(new { schemaVersion = 2, id = Guid.NewGuid(), engine = "Duplicati", versions = Array.Empty<object>() });
        await File.WriteAllTextAsync(metadata, future);
        await ThrowsAsync<InvalidDataException>(() => service.AddFolderAsync(broken));
        Assert(await File.ReadAllTextAsync(metadata) == future && Directory.GetFileSystemEntries(history).Length == 1,
            "unknown schema caused a future repository to be rewritten");
        Assert((await service.GetFoldersAsync()).Count == 0, "invalid repository was added to the registry");

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
            folders.Single(folder => folder.Root == moved).Status == "尚无完整版本", "an invalid association hid the healthy repository");
        await service.RemoveFolderAsync(second);
        Assert((await service.GetFoldersAsync()).Count == 1 && File.Exists(secondMetadata), "removing an invalid association deleted its repository");

        await ThrowsAsync<OperationCanceledException>(() => service.SaveAsync(moved, "declined"));
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
        committed["versions"] = JsonSerializer.SerializeToNode(new[] { new { id = goodId, sourceRoot = moved, note = "registered", pinned = true } });
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
        await service.StopAsync();
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
        public ProcessResult ProcessResult { get; set; } = new(0, "", "", false, false);
        public Exception? TaskError { get; private set; }
        public string AppRoot => Path.Combine(root, "app");
        public string DataRoot => Path.Combine(root, "data");
        public string PluginRoot => Path.Combine(AppRoot, "Plugins");
        public string PackageRoot => Path.Combine(DataRoot, "Packages");
        public string GetPluginDataDirectory(string pluginId) => Path.Combine(DataRoot, "Data", "Plugins", pluginId);
        public T Load<T>(string name) where T : IVersionedSettings, new() => _settings is T settings ? settings : new T();
        public void Save<T>(string name, T value) where T : IVersionedSettings, new() => _settings = value;
        public void Write(string source, string message, Exception? error = null) { }
        public PackageStatus GetStatus(PackageReference package) => throw new NotSupportedException();
        public Task<IPackageLease> AcquireAsync(PackageReference package, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IPackageLease?> TryRequestAsync(PackageReference package, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Leased = AllowPackage;
            return Task.FromResult<IPackageLease?>(AllowPackage ? new TestLease(this) : null);
        }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert(Leased, "tool ran without a package lease");
            Assert(request.Arguments[0] == "backup", "unexpected tool command");
            Processes++;
            EngineChecks.WriteOperationLog(request);
            return Task.FromResult(ProcessResult);
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
