using System.Security.Cryptography;
using System.Text.Json;
using TractorTool.Contracts;
using TractorTool.Contracts.Packages;
using TractorTool.Core.Packages;
using TractorTool.Core.Processes;
using TractorTool.ViewModels;

internal static class PackageCategoryChecks
{
    public static void Run(Action<string, Action> check, Action<Task> pump, string output, IAppLog log)
    {
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
            using var model = new ExternalToolsViewModel(service, _ => { });
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

    private static void Require(bool value)
    {
        if (!value) throw new InvalidOperationException("Package category cleanup assertion failed.");
    }
}
