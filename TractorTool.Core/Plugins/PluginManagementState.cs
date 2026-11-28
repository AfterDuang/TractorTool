using TractorTool.Contracts;
using TractorTool.Core.Storage;

namespace TractorTool.Core.Plugins;

public enum PluginChangeKind { Activate, Enable, Disable, Uninstall, Rollback, CleanHistory }
public sealed record PendingPluginChange(PluginChangeKind Kind, string Token, string DataRoot, string? Version = null, string? Digest = null)
{
    // Null denotes a legacy journal, not proof that a failed change is reversible.
    public bool? DestructiveStarted { get; init; }
    public IReadOnlyList<string>? HistoryVersions { get; init; }
}
public sealed record PluginRecovery(string Version, string Token, string DataRoot, bool HadData);
public sealed record RetainedPluginRemoval(string PluginId, string Token);
public sealed record ManagedPlugin
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? ActiveVersion { get; init; }
    public bool Enabled { get; init; } = true;
    public PendingPluginChange? Pending { get; init; }
    public PluginRecovery? Recovery { get; init; }
    public bool AwaitingLoad { get; init; }
    public string Error { get; init; } = "";
}

public sealed record PluginManagementState : IVersionedSettings
{
    public int SchemaVersion { get; init; } = 1;
    public ManagedPlugin[] Plugins { get; init; } = [];
    public RetainedPluginRemoval[] RetainedRemovals { get; init; } = [];

    public static void Validate(PluginManagementState value)
    {
        if (value.Plugins is null) throw new InvalidDataException("插件管理列表无效。");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in value.Plugins)
        {
            if (item is null) throw new InvalidDataException("插件管理条目为空。");
            if (item.Name is null || item.Error is null) throw new InvalidDataException("插件管理条目的名称或错误信息为空。");
            AppPaths.ValidateId(item.Id);
            if (!ids.Add(item.Id)) throw new InvalidDataException("插件管理记录 ID 重复。");
            if (item.ActiveVersion is { } active) ManifestValidator.ValidateVersion(active);
            if (item.Pending is { } pending)
            {
                if (!Enum.IsDefined(pending.Kind)) throw new InvalidDataException("未知插件变更。");
                ValidateToken(pending.Token);
                _ = AppPaths.Absolute(pending.DataRoot);
                if (pending.Kind == PluginChangeKind.Activate)
                {
                    ManifestValidator.ValidateVersion(pending.Version!);
                    if (pending.Digest is null || pending.Digest.Length != 64 || !pending.Digest.All(Uri.IsHexDigit))
                        throw new InvalidDataException("待安装包校验值无效。");
                }
                else if (item.ActiveVersion is null) throw new InvalidDataException("待变更插件没有活动版本。");
                if (pending.Kind == PluginChangeKind.Rollback && item.Recovery is null)
                    throw new InvalidDataException("回退缺少恢复记录。");
                if (pending.Kind != PluginChangeKind.CleanHistory && pending.HistoryVersions is { Count: > 0 })
                    throw new InvalidDataException("历史版本清理记录无效。");
                if (pending.Kind == PluginChangeKind.CleanHistory)
                {
                    var versions = new HashSet<string>(StringComparer.Ordinal);
                    if (pending.HistoryVersions is not { Count: > 0 }) throw new InvalidDataException("历史版本清理列表为空。");
                    foreach (var version in pending.HistoryVersions)
                    {
                        ManifestValidator.ValidateVersion(version);
                        if (version == item.ActiveVersion || !versions.Add(version))
                            throw new InvalidDataException("历史版本清理列表包含活动版本或重复版本。");
                    }
                    if (item.Recovery is { } saved && !versions.Contains(saved.Version))
                        throw new InvalidDataException("历史版本清理列表与回退记录不一致。");
                }
            }
            if (item.Recovery is { } recovery)
            {
                ManifestValidator.ValidateVersion(recovery.Version);
                ValidateToken(recovery.Token);
                _ = AppPaths.Absolute(recovery.DataRoot);
            }
        }
        if (value.RetainedRemovals is null) throw new InvalidDataException("插件卸载清理记录无效。");
        var removals = new HashSet<string>(StringComparer.Ordinal);
        foreach (var removal in value.RetainedRemovals)
        {
            if (removal is null) throw new InvalidDataException("插件卸载清理记录为空。");
            AppPaths.ValidateId(removal.PluginId);
            ValidateToken(removal.Token);
            if (!ids.Contains(removal.PluginId) || !removals.Add(removal.Token))
                throw new InvalidDataException("插件卸载清理记录缺少归属或重复。");
        }
    }

    internal static void ValidateToken(string token)
    {
        if (!Guid.TryParseExact(token, "N", out _)) throw new InvalidDataException("插件事务标识无效。");
    }
}
