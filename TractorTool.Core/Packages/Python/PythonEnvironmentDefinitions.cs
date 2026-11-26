using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TractorTool.Core.Plugins;
using TractorTool.Core.Storage;

namespace TractorTool.Core.Packages.Python;

public sealed record PythonEnvironmentDeclaration
{
    public string Id { get; init; } = "";
    public string PythonVersion { get; init; } = "";
    public string Project { get; init; } = "Python/pyproject.toml";
    public string Lock { get; init; } = "Python/uv.lock";
    public string ProjectSha256 { get; init; } = "";
    public string LockSha256 { get; init; } = "";
    public string Source { get; init; } = PythonEnvironmentDefinitions.PyPiUrl;
    public string Description { get; init; } = "";
}

public sealed record PythonEnvironmentDefinition(string Identity, string PluginId, string PluginVersion,
    string PluginName, string Id, string PythonVersion, string Rid, string Source, string Description,
    string ProjectText, string LockText, string UvVersion, string PythonBuild) : Environments.IEnvironmentDefinition;

public static class PythonEnvironmentDefinitions
{
    public const string PyPiUrl = "https://pypi.org/simple/";
    public const string UvVersion = "0.13.10";
    public const string UvArchiveSha256 = "297b3881fe1adfb922becc2c1613202abb41dc83ea5ff43d472aa087ca1f8683";
    public const string PythonBuild = "20261124";
    public const string SupportedPythonVersion = "3.13.18";
    internal const int MaxInputBytes = 2 * 1024 * 1024;
    internal static readonly UTF8Encoding Utf8 = new(false, true);

    public static void ValidateDeclaration(PythonEnvironmentDeclaration value)
    {
        ArgumentNullException.ThrowIfNull(value);
        AppPaths.ValidateId(value.Id);
        ManifestValidator.ValidateVersion(value.PythonVersion);
        if (value.PythonVersion != SupportedPythonVersion)
            throw new InvalidDataException($"当前 Python 环境支持已固定的 CPython {SupportedPythonVersion}。");
        var project = PackageFiles.RelativeParts(value.Project);
        var locked = PackageFiles.RelativeParts(value.Lock);
        if (project[^1] != "pyproject.toml" || locked[^1] != "uv.lock" ||
            !project[..^1].SequenceEqual(locked[..^1]))
            throw new InvalidDataException("Python 项目和锁文件必须为同一目录中的 pyproject.toml / uv.lock。");
        if (!IsHash(value.ProjectSha256) || !IsHash(value.LockSha256))
            throw new InvalidDataException("Python 项目与锁文件必须声明 SHA256。");
        _ = IndexUri(value.Source);
        if (value.Description is null || value.Description.Length > 1000)
            throw new InvalidDataException("Python 环境用途说明无效。");
    }

    public static PythonEnvironmentDefinition Snapshot(PythonEnvironmentDeclaration value, string pluginId,
        string pluginVersion, string pluginName, string pluginDirectory, string rid)
    {
        ValidateDeclaration(value);
        AppPaths.ValidateId(pluginId); ManifestValidator.ValidateVersion(pluginVersion);
        if (rid != "win-x64") throw new NotSupportedException("当前 Python 环境接入支持 Windows x64。");
        var project = ReadInput(PackageFiles.SafePath(pluginDirectory, value.Project), value.ProjectSha256);
        var locked = ReadInput(PackageFiles.SafePath(pluginDirectory, value.Lock), value.LockSha256);
        var definition = new PythonEnvironmentDefinition("", pluginId, pluginVersion, pluginName, value.Id,
            value.PythonVersion, rid, IndexUri(value.Source).AbsoluteUri, value.Description, project, locked, UvVersion, PythonBuild);
        definition = definition with { Identity = Identity(definition) };
        Validate(definition);
        return definition;
    }

    internal static void Validate(PythonEnvironmentDefinition value)
    {
        ArgumentNullException.ThrowIfNull(value);
        AppPaths.ValidateId(value.PluginId); AppPaths.ValidateId(value.Id); ManifestValidator.ValidateVersion(value.PluginVersion);
        ManifestValidator.ValidateVersion(value.PythonVersion);
        if (value.Rid != "win-x64" || value.PythonVersion != SupportedPythonVersion || value.UvVersion != UvVersion || value.PythonBuild != PythonBuild ||
            value.ProjectText is null || value.LockText is null || value.PluginName is null || value.PluginName.Length > 256 ||
            value.Description is null || value.Description.Length > 1000 ||
            Utf8.GetByteCount(value.ProjectText) > MaxInputBytes || Utf8.GetByteCount(value.LockText) > MaxInputBytes ||
            IndexUri(value.Source).AbsoluteUri != value.Source || !IsHash(value.Identity) || value.Identity != Identity(value))
            throw new InvalidDataException("Python 环境安装身份或原始锁定资料无效，原文件已保留。");
    }

    public static Uri IndexUri(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || !uri.IsDefaultPort ||
            !uri.AbsolutePath.TrimEnd('/').EndsWith("/simple", StringComparison.Ordinal) || address.Length > 2048)
            throw new InvalidDataException("PyPI 来源须为无认证的 HTTPS Simple API 地址，例如 https://pypi.org/simple/。");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }

    internal static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static string Identity(PythonEnvironmentDefinition definition) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { definition.PluginId, definition.PluginVersion, definition.Id,
            definition.PythonVersion, definition.Rid, definition.Source, definition.ProjectText, definition.LockText,
            definition.UvVersion, UvArchiveSha256, definition.PythonBuild })));

    private static string ReadInput(string path, string expected)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > MaxInputBytes) throw new InvalidDataException("Python 项目或锁文件大小无效。");
        var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Python 项目或锁文件与声明的 SHA256 不符。");
        return Utf8.GetString(bytes);
    }
}
