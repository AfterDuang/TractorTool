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
        await service.StopAsync();
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
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
            return Task.FromResult(new ProcessResult(0, "", "", false, false));
        }

        public Task<IBackgroundTask> QueueAsync(BackgroundTaskRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Queued++;
            return Task.FromResult<IBackgroundTask>(new TestTask(request));
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

        private sealed class TestTask(BackgroundTaskRequest request) : IBackgroundTask
        {
            private readonly CancellationTokenSource _stop = new();
            public Guid Id { get; } = Guid.NewGuid();
            public void Cancel() => _stop.Cancel();
            public void Dispose() => _stop.Dispose();

            public async Task<BackgroundTaskSnapshot> RunAsync(Func<IProgress<BackgroundTaskProgress>, CancellationToken, Task<string?>> execute)
            {
                try { return Result(BackgroundTaskState.Succeeded, await execute(new Progress<BackgroundTaskProgress>(), _stop.Token), null); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return Result(BackgroundTaskState.Cancelled, null, null); }
                catch (Exception ex) { return Result(BackgroundTaskState.Failed, null, ex.Message); }
            }

            private BackgroundTaskSnapshot Result(BackgroundTaskState state, string? value, string? error) => new(Id,
                "checks", request.BatchId, request.Title, state, null, "", value, error, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        }
    }
}
