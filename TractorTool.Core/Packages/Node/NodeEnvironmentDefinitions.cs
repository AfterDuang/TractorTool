using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TractorTool.Core.Plugins;
using TractorTool.Core.Storage;

namespace TractorTool.Core.Packages.Node;

public sealed record NodeEnvironmentDeclaration
{
    public string Id { get; init; } = "";
    public string NodeVersion { get; init; } = "";
    public string Project { get; init; } = "Node/package.json";
    public string Lock { get; init; } = "Node/pnpm-lock.yaml";
    public string ProjectSha256 { get; init; } = "";
    public string LockSha256 { get; init; } = "";
    public string Source { get; init; } = NodeEnvironmentDefinitions.NpmUrl;
    public string Description { get; init; } = "";
}

public sealed record NodeEnvironmentDefinition(string Identity, string PluginId, string PluginVersion,
    string PluginName, string Id, string NodeVersion, string Rid, string Source, string Description,
    string ProjectText, string LockText, string PnpmVersion) : Environments.IEnvironmentDefinition;

public static class NodeEnvironmentDefinitions
{
    public const string NpmUrl = "https://registry.npmjs.org/";
    public const string SupportedNodeVersion = "24.22.3";
    public const string PnpmVersion = "10.34.9";
    public const string NodeArchiveSha256 = "e612af99f626ab415ea225af6bce5c971feb448cb5cae250bb1c3fa50c5f7d9f";
    public const string PnpmArchiveSha256 = "7e8a946108e7baab119e51d27c24d6178b46545422643dbc91e34e2f1ee32e0f";
    internal const int MaxInputBytes = 2 * 1024 * 1024;
    internal static readonly UTF8Encoding Utf8 = new(false, true);

    public static void ValidateDeclaration(NodeEnvironmentDeclaration value)
    {
        ArgumentNullException.ThrowIfNull(value);
        AppPaths.ValidateId(value.Id);
        if (value.NodeVersion != SupportedNodeVersion)
            throw new InvalidDataException($"当前 Node 环境支持已固定的 Node.js {SupportedNodeVersion}。");
        var project = PackageFiles.RelativeParts(value.Project);
        var locked = PackageFiles.RelativeParts(value.Lock);
        if (project[^1] != "package.json" || locked[^1] != "pnpm-lock.yaml" || !project[..^1].SequenceEqual(locked[..^1]))
            throw new InvalidDataException("Node 项目和锁文件必须为同一目录中的 package.json / pnpm-lock.yaml。");
        if (!IsHash(value.ProjectSha256) || !IsHash(value.LockSha256))
            throw new InvalidDataException("Node 项目与锁文件必须声明 SHA256。");
        _ = RegistryUri(value.Source);
        if (value.Description is null || value.Description.Length > 1000)
            throw new InvalidDataException("Node 环境用途说明无效。");
    }

    public static NodeEnvironmentDefinition Snapshot(NodeEnvironmentDeclaration value, string pluginId,
        string pluginVersion, string pluginName, string pluginDirectory, string rid)
    {
        ValidateDeclaration(value);
        AppPaths.ValidateId(pluginId); ManifestValidator.ValidateVersion(pluginVersion);
        if (rid != "win-x64") throw new NotSupportedException("当前 Node 环境接入支持 Windows x64。");
        var definition = new NodeEnvironmentDefinition("", pluginId, pluginVersion, pluginName, value.Id,
            value.NodeVersion, rid, RegistryUri(value.Source).AbsoluteUri, value.Description,
            ReadInput(PackageFiles.SafePath(pluginDirectory, value.Project), value.ProjectSha256),
            ReadInput(PackageFiles.SafePath(pluginDirectory, value.Lock), value.LockSha256), PnpmVersion);
        definition = definition with { Identity = Identity(definition) };
        Validate(definition);
        return definition;
    }

    internal static void Validate(NodeEnvironmentDefinition value)
    {
        ArgumentNullException.ThrowIfNull(value);
        AppPaths.ValidateId(value.PluginId); AppPaths.ValidateId(value.Id); ManifestValidator.ValidateVersion(value.PluginVersion);
        if (value.Rid != "win-x64" || value.NodeVersion != SupportedNodeVersion || value.PnpmVersion != PnpmVersion ||
            value.ProjectText is null || value.LockText is null || value.PluginName is null || value.PluginName.Length > 256 ||
            value.Description is null || value.Description.Length > 1000 ||
            Utf8.GetByteCount(value.ProjectText) > MaxInputBytes || Utf8.GetByteCount(value.LockText) > MaxInputBytes ||
            RegistryUri(value.Source).AbsoluteUri != value.Source || !IsHash(value.Identity) || value.Identity != Identity(value))
            throw new InvalidDataException("Node 环境安装身份或原始锁定资料无效，原文件已保留。");
    }

    public static Uri RegistryUri(string address)
    {
        if (address is null || address.Length > 2048 || !Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || !uri.IsDefaultPort)
            throw new InvalidDataException("npm 来源须为无认证的 HTTPS registry 地址，例如 https://registry.npmjs.org/。");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }

    internal static string NodeExecutable(string generation, NodeEnvironmentDefinition definition) =>
        Path.Combine(generation, "node", $"node-v{definition.NodeVersion}-win-x64", "node.exe");
    internal static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static string Identity(NodeEnvironmentDefinition value) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { value.PluginId, value.PluginVersion, value.Id, value.NodeVersion,
            value.Rid, value.Source, value.ProjectText, value.LockText, value.PnpmVersion, NodeArchiveSha256, PnpmArchiveSha256 })));

    private static string ReadInput(string path, string expected)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > MaxInputBytes) throw new InvalidDataException("Node 项目或锁文件大小无效。");
        var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Node 项目或锁文件与声明的 SHA256 不符。");
        return Utf8.GetString(bytes);
    }
}
