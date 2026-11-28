using System.Text.Json;

namespace TractorTool.Core.Plugins;

public sealed record PluginHistoryCleanup(string PluginId, string Name, string ActiveVersion,
    string? RecoveryVersion, IReadOnlyList<string> Versions);

public sealed partial class PluginManager
{
    public IReadOnlyList<PluginHistoryCleanup> GetHistoryCleanup(IReadOnlyList<PluginRecord> current)
    {
        lock (_gate)
        {
            Check();
            return _state.Plugins.Where(p => CanCleanHistory(p, current)).Select(HistoryCleanup)
                .Where(p => p.Versions.Count != 0).ToArray();
        }
    }

    // Queue the exact preview in one save; a late plugin change must not expand the user's consent.
    public string? QueueHistoryCleanup(IReadOnlyList<PluginHistoryCleanup> plan, IReadOnlyList<PluginRecord> current)
    {
        ArgumentNullException.ThrowIfNull(plan);
        lock (_gate)
        {
            Check();
            if (plan.Count == 0) return "没有可清理的历史版本。";
            var plugins = _state.Plugins.ToArray();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in plan)
            {
                if (candidate is null || !ids.Add(candidate.PluginId)) return "历史版本列表无效，请重新检查。";
                var index = Array.FindIndex(plugins, p => p.Id == candidate.PluginId);
                if (index < 0 || !CanCleanHistory(plugins[index], current)) return "插件状态已变化，请重新检查历史版本。";
                var item = plugins[index];
                var latest = HistoryCleanup(item);
                if (latest.ActiveVersion != candidate.ActiveVersion || latest.RecoveryVersion != candidate.RecoveryVersion ||
                    candidate.Versions is null || latest.Versions.Count == 0 || !latest.Versions.SequenceEqual(candidate.Versions))
                    return "历史版本列表已变化，请重新确认后清理。";
                plugins[index] = item with { Pending = new(PluginChangeKind.CleanHistory, Guid.NewGuid().ToString("N"), _paths.DataRoot)
                    { HistoryVersions = latest.Versions, DestructiveStarted = false } };
            }
            SaveState(_state with { Plugins = plugins });
            return null;
        }
    }

    private static bool CanCleanHistory(ManagedPlugin item, IReadOnlyList<PluginRecord> current)
    {
        if (item.ActiveVersion is null || item.Pending is not null || item.AwaitingLoad || item.Error.Length != 0) return false;
        var records = current.Where(p => p.Manifest?.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase) == true).ToArray();
        return records.Length == 1 && records[0].Manifest!.Version == item.ActiveVersion &&
            records[0].State is PluginState.Loaded or PluginState.Disabled;
    }

    private PluginHistoryCleanup HistoryCleanup(ManagedPlugin item)
    {
        RequireVersionIdentity(item.Id, item.ActiveVersion!, allowMissingManifest: false);
        var versions = new HashSet<string>(StringComparer.Ordinal);
        if (item.Recovery is { } recovery && recovery.Version != item.ActiveVersion)
        {
            RequireVersionIdentity(item.Id, recovery.Version, allowMissingManifest: true);
            versions.Add(recovery.Version);
        }
        var root = PluginFiles.Under(_paths.PluginRoot, item.Id);
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);
            if (name == item.ActiveVersion || name == item.Recovery?.Version || !Version.TryParse(name, out var version) ||
                version.Build < 0 || version.Revision >= 0 || version.ToString(3) != name) continue;
            var path = VersionPath(item.Id, name);
            // Unknown directories without a manifest are not owned merely because their name looks like a version.
            if (!PathPresent(PluginFiles.Under(path, "plugin.json"))) continue;
            RequireVersionIdentity(item.Id, name, allowMissingManifest: false);
            versions.Add(name);
        }
        return new(item.Id, item.Name, item.ActiveVersion!, item.Recovery?.Version,
            Array.AsReadOnly(versions.OrderBy(Version.Parse).ToArray()));
    }

    private void RequireVersionIdentity(string id, string version, bool allowMissingManifest)
    {
        var directory = VersionPath(id, version);
        if (!PluginFiles.DirectoryPresent(directory))
        {
            if (allowMissingManifest) return;
            throw new DirectoryNotFoundException("插件版本目录不可用：" + directory);
        }
        var manifest = PluginFiles.Under(directory, "plugin.json");
        if (allowMissingManifest && !PathPresent(manifest)) return;
        // Cleanup needs ownership, not compatibility with today's SDK or dependency toolchain.
        using var stream = File.OpenRead(manifest);
        if (stream.Length > 128 * 1024) throw new InvalidDataException("历史插件清单超过 128 KiB：" + manifest);
        using var reader = new StreamReader(stream);
        try
        {
            var manifestIdentity = JsonSerializer.Deserialize<PluginManifest>(reader.ReadToEnd(), ManifestValidator.Json);
            if (manifestIdentity?.Id != id || manifestIdentity.Version != version)
                throw new InvalidDataException("历史插件清单与目录归属不一致：" + directory);
        }
        catch (JsonException ex) { throw new InvalidDataException($"历史插件清单无法读取：{manifest}。{ex.Message}", ex); }
    }

    private ManagedPlugin ApplyHistoryCleanup(ManagedPlugin item)
    {
        var pending = item.Pending!;
        RequireVersionIdentity(item.Id, item.ActiveVersion!, allowMissingManifest: false);
        var trash = PluginFiles.Under(_root, "removed", pending.Token);
        var started = pending.DestructiveStarted == true;
        foreach (var version in pending.HistoryVersions!)
        {
            var source = VersionPath(item.Id, version);
            var target = PluginFiles.Under(trash, version);
            if (!PluginFiles.DirectoryPresent(source)) continue;
            // The saved preview establishes ownership even if files were manually removed since confirmation.
            RequireVersionIdentity(item.Id, version, allowMissingManifest: true);
            if (PathPresent(target)) throw new IOException("历史版本同时出现在安装目录与清理目录，已保留现场：" + source);
            _ = PluginFiles.Files(source).ToArray();
            if (!started) { MarkDestructiveStarted(item); started = true; }
            Directory.CreateDirectory(trash);
            Directory.Move(source, target);
        }
        foreach (var version in pending.HistoryVersions!)
            PluginFiles.Delete(trash, PluginFiles.Under(trash, version));
        if (PluginFiles.DirectoryPresent(trash)) Directory.Delete(trash); // Never recursively delete unlisted entries.
        return item with { Recovery = null };
    }
}
