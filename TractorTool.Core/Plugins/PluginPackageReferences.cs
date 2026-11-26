using TractorTool.Core.Packages;
using TractorTool.Core.Storage;
using TractorTool.Core.Packages.Python;
using TractorTool.Core.Packages.Node;

namespace TractorTool.Core.Plugins;

/// <summary>Manifests and the management journal are the persistent source of dependency references.</summary>
public sealed class PluginPackageReferences : IDisposable
{
    private readonly AppPaths _paths;
    private readonly PluginManager? _manager;
    private readonly ExternalPackageService _packages;
    private readonly object _gate = new();
    private PluginRecord[] _active = [];
    private string _scanError = "正在读取插件依赖。";
    private bool _disposed;

    public PluginPackageReferences(AppPaths paths, PluginManager? manager, ExternalPackageService packages)
    {
        _paths = paths; _manager = manager; _packages = packages;
        if (manager is not null) { manager.StateChanging += Invalidate; manager.StateChanged += Refresh; }
    }

    public void SetDiscovered(IReadOnlyList<PluginRecord> records, string? directoryError)
    {
        lock (_gate)
        {
            _active = records.Where(r => r.Manifest is not null).ToArray();
            _scanError = _manager is null ? "插件管理不可用，暂停清理依赖。" : !string.IsNullOrEmpty(directoryError) ? directoryError :
                records.Any(r => r.Manifest is null) ? "部分插件清单无法读取，暂停清理依赖。" : "";
        }
        Refresh();
    }

    private void Invalidate() => _packages.InvalidateReferences();

    private void Refresh()
    {
        // Keep the manager lock until publication: an old snapshot must never replace a newer one.
        if (_manager is null) Publish([]);
        else _manager.ReadState(Publish);
    }

    private void Publish(IReadOnlyList<ManagedPlugin> managed)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var manifests = _active.Select(r => (Manifest: r.Manifest!, r.Directory)).ToList();
            var python = new List<PythonEnvironmentDefinition>();
            var node = new List<NodeEnvironmentDefinition>();
            var error = _scanError;
            try
            {
                foreach (var item in managed)
                {
                    if (item.Error.Length != 0) error = "部分插件变更尚未恢复，暂停清理依赖。";
                    if (item.ActiveVersion is { } activeVersion)
                    {
                        if (error.Length == 0 && !_active.Any(r => r.Manifest!.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase) && r.Manifest.Version == activeVersion))
                            error = $"未发现已登记的插件 {item.Id} {activeVersion}，暂停清理依赖。";
                        if (item.Recovery is { } recovery) manifests.Add(Read(item.Id, recovery.Version));
                    }
                    if (item.Pending is { Kind: PluginChangeKind.Activate, Version: { } version } pending)
                    {
                        var staging = PluginFiles.Under(_paths.PluginRoot, ".management", "staging", pending.Token, item.Id, version);
                        manifests.Add(Directory.Exists(staging) ? (ManifestValidator.Read(staging), staging) : Read(item.Id, version));
                    }
                }
                foreach (var (manifest, directory) in manifests)
                {
                    foreach (var declaration in manifest.PythonEnvironments)
                        python.Add(PythonEnvironmentDefinitions.Snapshot(declaration, manifest.Id, manifest.Version,
                            manifest.Name, directory, System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier));
                    foreach (var declaration in manifest.NodeEnvironments)
                        node.Add(NodeEnvironmentDefinitions.Snapshot(declaration, manifest.Id, manifest.Version,
                            manifest.Name, directory, System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier));
                }
            }
            catch (Exception ex) { error = "无法确认全部插件依赖，暂停清理：" + ex.Message; }
            _packages.UpdateReferences(new(manifests.SelectMany(p => p.Manifest.Dependencies.Select(d =>
                    new PackageDependency(d.Package, p.Manifest.Id, p.Manifest.Name, d.Downloads)))
                .Distinct().ToArray(), error.Length == 0, error)
                { PythonEnvironments = python.DistinctBy(p => p.Identity).ToArray(), NodeEnvironments = node.DistinctBy(p => p.Identity).ToArray() });
        }
    }

    private (PluginManifest Manifest, string Directory) Read(string id, string version)
    {
        var directory = PluginFiles.Under(_paths.PluginRoot, id, version);
        return (ManifestValidator.Read(directory), directory);
    }

    public void Dispose()
    {
        if (_manager is not null) { _manager.StateChanging -= Invalidate; _manager.StateChanged -= Refresh; }
        lock (_gate) _disposed = true;
    }
}
