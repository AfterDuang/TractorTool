using TractorTool.Core.Packages;
using TractorTool.Core.Storage;

namespace TractorTool.Core.Plugins;

/// <summary>Manifests and the management journal are the persistent source of dependency references.</summary>
public sealed class PluginPackageReferences : IDisposable
{
    private readonly AppPaths _paths;
    private readonly PluginManager? _manager;
    private readonly ExternalPackageService _packages;
    private readonly object _gate = new();
    private PluginManifest[] _active = [];
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
            _active = records.Where(r => r.Manifest is not null).Select(r => r.Manifest!).ToArray();
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
            var manifests = new List<PluginManifest>(_active);
            var error = _scanError;
            try
            {
                foreach (var item in managed)
                {
                    if (item.Error.Length != 0) error = "部分插件变更尚未恢复，暂停清理依赖。";
                    if (item.ActiveVersion is { } activeVersion)
                    {
                        if (error.Length == 0 && !_active.Any(m => m.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase) && m.Version == activeVersion))
                            error = $"未发现已登记的插件 {item.Id} {activeVersion}，暂停清理依赖。";
                        if (item.Recovery is { } recovery) manifests.Add(Read(item.Id, recovery.Version));
                    }
                    if (item.Pending is { Kind: PluginChangeKind.Activate, Version: { } version } pending)
                    {
                        var staging = PluginFiles.Under(_paths.PluginRoot, ".management", "staging", pending.Token, item.Id, version);
                        manifests.Add(Directory.Exists(staging) ? ManifestValidator.Read(staging) : Read(item.Id, version));
                    }
                }
            }
            catch (Exception ex) { error = "无法确认全部插件依赖，暂停清理：" + ex.Message; }
            _packages.UpdateReferences(new(manifests.SelectMany(m => m.Dependencies.Select(p => new PackageDependency(p.Package, m.Id, m.Name, p.Downloads)))
                .Distinct().ToArray(), error.Length == 0, error));
        }
    }

    private PluginManifest Read(string id, string version) => ManifestValidator.Read(PluginFiles.Under(_paths.PluginRoot, id, version));

    public void Dispose()
    {
        if (_manager is not null) { _manager.StateChanging -= Invalidate; _manager.StateChanged -= Refresh; }
        lock (_gate) _disposed = true;
    }
}
