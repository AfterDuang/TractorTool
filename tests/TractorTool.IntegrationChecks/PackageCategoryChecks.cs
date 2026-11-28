using System.Security.Cryptography;
using System.Text.Json;
using TractorTool.Contracts;
using TractorTool.Contracts.Packages;
using TractorTool.Core.Logging;
using TractorTool.Core.Packages;
using TractorTool.Core.Processes;
using TractorTool.ViewModels;

internal static class PackageCategoryChecks
{
    public static void Run(Action<string, Action> check, Action<Task> pump, string output, IAppLog log)
    {
        foreach (var node in new[] { false, true })
        check((node ? "Node" : "Python") + " cache confirmation ends normally when its view model stops", () =>
        {
            var data = Path.Combine(output, "cache-confirmation-" + Guid.NewGuid().ToString("N"));
            var cache = Path.Combine(data, "Cache", node ? "Node" : "uv");
            Directory.CreateDirectory(cache);
            var marker = node ? ".node-cache" : ".python-cache";
            File.WriteAllText(Path.Combine(cache, marker), node ? "TractorTool.NodeCache.v1" : "TractorTool.PythonCache.v1");
            File.WriteAllText(Path.Combine(cache, "keep.bin"), "cache must survive declined confirmation");
            using var http = new HttpClient();
            var runner = new ProcessRunner("CacheConfirmation.Check", log, _ => throw new InvalidOperationException("Must not start a process"));
            var service = new ExternalPackageService(Path.Combine(data, "Packages"), "win-x64", http, runner, log);
            using var model = new ExternalToolsViewModel(service, _ => { }, log);
            var prompted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task? cleanup = null;
            model.ConnectConfirmation(async (_, _, token) =>
            {
                prompted.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return true;
            });
            try
            {
                model.SelectedCategoryIndex = node ? 3 : 2;
                Require(model.CleanEnvironmentCacheCommand.CanExecute(null));
                cleanup = model.CleanEnvironmentCacheCommand.ExecuteAsync(null);
                pump(prompted.Task);
                model.Dispose();
                pump(cleanup);
                Require(cleanup.IsCompletedSuccessfully && File.Exists(Path.Combine(cache, "keep.bin")));
            }
            finally
            {
                model.Dispose();
                if (cleanup is not null) pump(cleanup);
                pump(service.DisposeAsync().AsTask()); pump(runner.DisposeAsync().AsTask());
            }
        });
        check("Package cleanup keeps hidden categories and search results, even when selection changes during confirmation", () =>
        {
            var root = Path.Combine(output, "package-category-" + Guid.NewGuid().ToString("N"));
            var chosen = Definition("Chosen.Tool", "Chosen tool", PackageKind.Tool);
            var hidden = Definition("Hidden.Tool", "Hidden tool", PackageKind.Tool);
            var native = Definition("Chosen.Runtime", "Chosen runtime", PackageKind.NativeLibrary);
            var chosenDirectory = WriteInstalled(root, chosen);
            var hiddenDirectory = WriteInstalled(root, hidden);
            var nativeDirectory = WriteInstalled(root, native);
            using var http = new HttpClient();
            var runner = new ProcessRunner("PackageCategory.Check", log, _ => throw new InvalidOperationException("Cleanup must not run a tool"));
            var service = new ExternalPackageService(root, "win-x64", http, runner, log);
            service.UpdateReferences(new([], true));
            using var model = new ExternalToolsViewModel(service, _ => { }, log);
            var prompted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task? cleanup = null;
            model.ConnectConfirmation((_, message, _) => { prompted.TrySetResult(message); return answer.Task; });
            try
            {
                Require(service.GetPackages().Count == 3 && service.GetPackages().All(p => p.CanRemove));
                Require(model.VisibleCards.Count == 2);
                Require(model.PackageCategories.SequenceEqual(["外部工具", "DLL 运行库", "Python 环境", "Node 环境"]));
                model.SelectedCategoryIndex = 3;
                Require(model.IsEmpty && model.VisibleCards.Count == 0 && !model.HasEnvironmentCache && !model.CleanCommand.CanExecute(null));
                model.SelectedCategoryIndex = 0;
                model.SearchText = "Chosen";
                Require(model.VisibleCards.Single().Key == chosen.Package && model.CleanCommand.CanExecute(null));
                cleanup = model.CleanCommand.ExecuteAsync(null);
                pump(prompted.Task);
                var message = prompted.Task.Result;
                Require(message.Contains(chosen.Name) && !message.Contains(hidden.Name) && !message.Contains(native.Name));

                // Consent belongs to the displayed candidate list, not whichever tab is selected later.
                model.SelectedCategoryIndex = 1;
                Require(model.VisibleCards.Single().Key == native.Package);
                answer.SetResult(true);
                pump(cleanup);
                Require(!Directory.Exists(chosenDirectory));
                Require(Directory.Exists(hiddenDirectory) && Directory.Exists(nativeDirectory));
                Require(service.GetPackages().Select(p => p.Package).ToHashSet().SetEquals([hidden.Package, native.Package]));
            }
            finally
            {
                answer.TrySetResult(false);
                if (cleanup is not null) pump(cleanup);
                model.Dispose(); pump(service.DisposeAsync().AsTask()); pump(runner.DisposeAsync().AsTask());
            }
        });

        check("Package errors show shared reference failures once and preserve independent environment storage errors", () =>
        {
            var root = Path.Combine(output, "package-errors-" + Guid.NewGuid().ToString("N"));
            foreach (var node in new[] { false, true })
            {
                var directory = Path.Combine(root, node ? "Node" : "uv");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, node ? ".node-packages" : ".python-packages"),
                    node ? "TractorTool.NodePackages.v1" : "TractorTool.PythonPackages.v1");
                File.WriteAllText(Path.Combine(directory, "unknown.txt"), "Preserve this unrecognized content.");
            }
            using var http = new HttpClient();
            var runner = new ProcessRunner("PackageErrors.Check", log,
                _ => throw new InvalidOperationException("Listing package errors must not start a process"));
            var service = new ExternalPackageService(root, "win-x64", http, runner, log);
            using var model = new ExternalToolsViewModel(service, _ => { }, log);
            try
            {
                service.UpdateReferences(new([], true));
                var pythonError = service.Python.Error;
                var nodeError = service.Node.Error;
                Require(pythonError.StartsWith("Python 环境记录不可用", StringComparison.Ordinal) &&
                    nodeError.StartsWith("Node 环境记录不可用", StringComparison.Ordinal),
                    "The fixture must independently block both environment stores.");
                const string sharedError = "无法确认全部插件依赖，暂停清理：\r\n旧版本的 plugin.json 不存在。";
                service.UpdateReferences(new([], false, sharedError));
                Require(model.Error == string.Join("\n", [sharedError.Replace("\r\n", "\n"), pythonError, nodeError]),
                    "Each shared diagnostic line must appear once without hiding distinct Python or Node storage errors.");

                service.UpdateReferences(new([], true));
                Require(model.Error == string.Join("\n", [pythonError, nodeError]),
                    "Repairing references must remove their error while preserving unrelated storage errors.");
            }
            finally { model.Dispose(); pump(service.DisposeAsync().AsTask()); pump(runner.DisposeAsync().AsTask()); }
        });

        foreach (var duplicateName in new[] { true, false })
        check((duplicateName ? "Duplicate source names" : "Normalized duplicate source addresses") +
            " preserve settings and form input, log a warning, and allow correction", () =>
        {
            var data = Path.Combine(output, "source-validation-" + Guid.NewGuid().ToString("N"));
            var sourcesFile = Path.Combine(data, "Config", "dependency-sources.json");
            var sourceLog = new AppLog(Path.Combine(data, "Logs"));
            using var http = new HttpClient();
            var runner = new ProcessRunner("SourceValidation.Check", sourceLog,
                _ => throw new InvalidOperationException("Source settings must not start a process"));
            var service = new ExternalPackageService(Path.Combine(data, "Packages"), "win-x64", http, runner,
                sourceLog, sourcesFile: sourcesFile);
            service.AddSource("Team", "https://packages.example.invalid/releases/", DependencySourceKind.Https);
            using var model = new ExternalToolsViewModel(service, _ => { }, sourceLog);
            try
            {
                var before = File.ReadAllBytes(sourcesFile);
                var previousSources = service.GetSources().ToArray();
                var previousLogId = sourceLog.Snapshot().Entries.Last().Id;
                var name = duplicateName ? "  tEaM  " : "Second team";
                var address = duplicateName ? "https://other.example.invalid/releases/" : "  https://PACKAGES.example.invalid/releases  ";
                model.SourceKindIndex = 3;
                model.SourceName = name; model.SourceAddress = address; model.ShowSourceForm = true;
                pump(model.AddSourceCommand.ExecuteAsync(null));

                Require(model.HasSourceFormError && model.SourceFormError.Contains(duplicateName ? "名称" : "地址"),
                    "The form must explain whether the source name or normalized address is duplicated.");
                Require(model.SourceName == name && model.SourceAddress == address && model.ShowSourceForm,
                    "Rejected source input must remain visible and editable.");
                Require(File.ReadAllBytes(sourcesFile).SequenceEqual(before) && service.GetSources().SequenceEqual(previousSources),
                    "A duplicate source must not change persisted settings or active sources.");
                var entries = sourceLog.Snapshot().Entries.Where(e => e.Id > previousLogId).ToArray();
                Require(entries.Length == 1 && entries[0].Level == AppLogLevel.Warning && entries[0].Detail is null &&
                    entries[0].Message.Contains(model.SourceFormError),
                    "A rejected duplicate must create one warning with the form reason and no exception detail.");
                Require(sourceLog.Snapshot().UnreadErrors == 0,
                    "Normal duplicate input must not increase unread errors.");

                model.SourceName = "Corrected team"; model.SourceAddress = "https://corrected.example.invalid/releases/";
                Require(model.AddSourceCommand.CanExecute(null), "Corrected source input must be resubmittable.");
                pump(model.AddSourceCommand.ExecuteAsync(null));
                Require(!model.HasSourceFormError && model.SourceFormError.Length == 0 && !model.ShowSourceForm &&
                    model.SourceName.Length == 0 && model.SourceAddress.Length == 0,
                    "A successful correction must clear the validation message and reset the form.");
                var added = service.GetSources().Single(s => s.Name == "Corrected team");
                Require(added.Kind == DependencySourceKind.Https && added.Url == "https://corrected.example.invalid/releases/" &&
                    ExternalPackageService.ReadConfiguredSources(sourcesFile).Contains(added) && model.Sources.Contains(added),
                    "The corrected HTTPS source must be persisted and visible in the source list.");
            }
            finally { model.Dispose(); pump(service.DisposeAsync().AsTask()); pump(runner.DisposeAsync().AsTask()); }
        });

        check("Source persistence failure retains input, logs the IO exception, and does not add a source", () =>
        {
            var data = Path.Combine(output, "source-save-failure-" + Guid.NewGuid().ToString("N"));
            var sourcesFile = Path.Combine(data, "Config", "dependency-sources.json");
            var sourceLog = new AppLog(Path.Combine(data, "Logs"));
            using var http = new HttpClient();
            var runner = new ProcessRunner("SourceSaveFailure.Check", sourceLog,
                _ => throw new InvalidOperationException("Source settings must not start a process"));
            var service = new ExternalPackageService(Path.Combine(data, "Packages"), "win-x64", http, runner,
                sourceLog, sourcesFile: sourcesFile);
            using var model = new ExternalToolsViewModel(service, _ => { }, sourceLog);
            try
            {
                var previousSources = service.GetSources().ToArray();
                // Fail an actual write after loading valid settings, rather than simulating invalid input.
                Directory.CreateDirectory(sourcesFile);
                model.SourceKindIndex = 3; model.SourceName = "Team";
                model.SourceAddress = "https://packages.example.invalid/releases/"; model.ShowSourceForm = true;
                pump(model.AddSourceCommand.ExecuteAsync(null));

                Require(model.HasSourceFormError && model.ShowSourceForm && model.SourceName == "Team" &&
                    model.SourceAddress == "https://packages.example.invalid/releases/",
                    "A failed settings write must show its error without discarding source input.");
                Require(service.GetSources().SequenceEqual(previousSources) && model.Sources.SequenceEqual(previousSources) &&
                    Directory.Exists(sourcesFile) && !File.Exists(sourcesFile),
                    "A failed settings write must leave both source lists and the conflicting directory unchanged.");
                var entries = sourceLog.Snapshot().Entries;
                Require(entries.Count == 1 && entries[0].Level == AppLogLevel.Error &&
                    entries[0].Detail is { } detail && detail.Contains("JsonFileStore"),
                    "A real IO failure must be logged as Error with exception details from the settings save.");
                Require(sourceLog.Snapshot().UnreadErrors == 1,
                    "A real source persistence failure must be visible as one unread error.");
            }
            finally { model.Dispose(); pump(service.DisposeAsync().AsTask()); pump(runner.DisposeAsync().AsTask()); }
        });
    }

    private static PackageDefinition Definition(string id, string name, PackageKind kind) =>
        new(new(id, "1.0.0"), "win-x64", name, "Synthetic cleanup fixture", new("https://example.invalid/package.zip"),
            new string('a', 64), "Fixture", new("https://example.invalid/source"),
            kind == PackageKind.Tool ? new Dictionary<string, PackageTool> { ["tool"] = new("tool.exe", "fixture") } : new Dictionary<string, PackageTool>())
        {
            Kind = kind,
            Libraries = kind == PackageKind.NativeLibrary ? new Dictionary<string, string> { ["runtime"] = "runtime.dll" } : new Dictionary<string, string>()
        };

    // Only package-list metadata and cleanup are exercised; these synthetic payloads are never acquired or executed.
    private static string WriteInstalled(string root, PackageDefinition definition)
    {
        var native = definition.Kind == PackageKind.NativeLibrary;
        var parent = native ? Path.Combine(root, "DLL") : root;
        Directory.CreateDirectory(parent);
        if (native) File.WriteAllText(Path.Combine(parent, ".native-packages"), "TractorTool.NativePackages.v1");
        var directory = Path.Combine(parent, definition.Package.Id, definition.Package.Version, definition.Rid);
        Directory.CreateDirectory(directory);
        var payloadName = native ? "runtime.dll" : "tool.exe";
        byte[] payload = [1, 2, 3, 4];
        File.WriteAllBytes(Path.Combine(directory, payloadName), payload);
        var payloadHash = Convert.ToHexString(SHA256.HashData(payload));
        string? manifestHash = null;
        if (native)
        {
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = 1,
                files = new[] { new { path = payloadName, length = payload.Length, sha256 = payloadHash } }
            });
            File.WriteAllBytes(Path.Combine(directory, ".package-files.json"), manifest);
            manifestHash = Convert.ToHexString(SHA256.HashData(manifest));
        }
        File.WriteAllText(Path.Combine(directory, "receipt.json"), JsonSerializer.Serialize(new
        {
            Schema = native ? 3 : 2, ArchiveHash = definition.Sha256,
            Tools = native ? new Dictionary<string, string>() : new Dictionary<string, string> { ["tool"] = payloadHash },
            InstalledBytes = payload.Length, Definition = definition, ManifestHash = manifestHash
        }));
        return directory;
    }

    private static void Require(bool value, string message = "Package category cleanup assertion failed.")
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
