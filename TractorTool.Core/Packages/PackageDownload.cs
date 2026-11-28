using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TractorTool.Contracts.Packages;

namespace TractorTool.Core.Packages;

// Download instructions belong to the plugin; PackageReference remains the stable public identity.
public sealed record PluginDependency(string Id, string Version)
{
    public PackageDownload[] Downloads { get; init; } = [];
    [JsonIgnore] public PackageReference Package => new(Id, Version);
}

public sealed record PackageDownload(string Rid, string Repository, string Tag, string Asset,
    string Sha256, string Name, string Description, string License, IReadOnlyDictionary<string, PackageTool> Tools)
{
    public IReadOnlyDictionary<string, PackageTool> Tools { get; init; } = Tools ?? ReadOnlyDictionary<string, PackageTool>.Empty;
    public PackageKind Kind { get; init; }
    public IReadOnlyDictionary<string, string> Libraries { get; init; } = ReadOnlyDictionary<string, string>.Empty;
    public string Source { get; init; } = DependencySource.GitHubUrl;
    public DependencySourceKind SourceKind { get; init; }
    public IReadOnlyList<PackageArchive> Archives { get; init; } = [];

    internal PackageDefinition Definition(PackageReference package)
    {
        if (Archives is null) throw new InvalidDataException("依赖归档列表不能为空。");
        if (Archives.Count != 0)
        {
            if (Kind != PackageKind.NativeLibrary || !string.IsNullOrEmpty(Repository) || !string.IsNullOrEmpty(Tag) ||
                !string.IsNullOrEmpty(Asset) || !string.IsNullOrEmpty(Sha256))
                throw new InvalidDataException("原生归档声明不能混用 GitHub 仓库、标签或单包摘要。");
            var origin = DependencySource.Address(SourceKind, Source);
            PackageArchiveDefinitions.Validate(Archives, SourceKind, origin);
            var native = new PackageDefinition(package, Rid, Name, Description, Archives[0].Url,
                PackageArchiveDefinitions.Identity(Archives, SourceKind, origin), License, origin, Tools)
                { Kind = Kind, Libraries = Libraries, Archives = Archives, SourceKind = SourceKind };
            PackageDefinitions.Validate(native);
            return native;
        }
        if (SourceKind != DependencySourceKind.GitHub) throw new InvalidDataException("单 ZIP 依赖必须使用 GitHub 来源。");
        var source = DependencySource.ApiUri(Source);
        if (Repository is null || !Regex.IsMatch(Repository, @"^[A-Za-z0-9_-]+/[A-Za-z0-9_.-]+$") ||
            Repository.Split('/')[1] is "." or ".." || string.IsNullOrWhiteSpace(Tag) || Tag is "." or ".." || Tag.Length > 256 ||
            Tag.Any(char.IsControl) || string.IsNullOrWhiteSpace(Asset) || Asset.Length > 256 ||
            Asset.IndexOfAny(['/', '\\']) >= 0 || Asset.Any(char.IsControl) || !Asset.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHub 依赖必须指定 owner/repository、精确标签和 ZIP 附件名称。");
        var site = source.Host == "api.github.com" ? "https://github.com" : source.GetLeftPart(UriPartial.Authority);
        var release = $"{site}/{Repository}/releases";
        var result = new PackageDefinition(package, Rid, Name, Description,
            new($"{release}/download/{Uri.EscapeDataString(Tag)}/{Uri.EscapeDataString(Asset)}"), Sha256, License,
            new($"{release}/tag/{Uri.EscapeDataString(Tag)}"), Tools) { Kind = Kind, Libraries = Libraries };
        PackageDefinitions.Validate(result);
        return result;
    }

    internal PackageDownload Snapshot(PackageReference package)
    {
        var definition = PackageDefinitions.Snapshot(Definition(package));
        return this with { Source = DependencySource.Address(SourceKind, Source).AbsoluteUri,
            Tools = definition.Tools, Libraries = definition.Libraries, Archives = definition.Archives };
    }
}

public sealed record DependencySource(string Name, string Url)
{
    public const string GitHubUrl = "https://api.github.com/";
    public const string PyPiUrl = "https://pypi.org/simple/";
    public const string NpmUrl = "https://registry.npmjs.org/";
    public DependencySourceKind Kind { get; init; }
    [JsonIgnore] public bool IsDefault => Kind switch
    {
        DependencySourceKind.GitHub => Url == GitHubUrl,
        DependencySourceKind.PythonIndex => Url == PyPiUrl,
        DependencySourceKind.NpmRegistry => Url == NpmUrl,
        _ => false
    };
    [JsonIgnore] public string KindLabel => Kind switch
    {
        DependencySourceKind.GitHub => "GitHub Releases",
        DependencySourceKind.PythonIndex => "Python 包索引",
        DependencySourceKind.NpmRegistry => "npm registry",
        DependencySourceKind.Https => "HTTPS 文件来源",
        _ => "未知来源"
    };
    internal static Uri Address(DependencySourceKind kind, string address) => kind switch
    {
        DependencySourceKind.GitHub => ApiUri(address),
        DependencySourceKind.PythonIndex => IndexUri(address),
        DependencySourceKind.NpmRegistry => Node.NodeEnvironmentDefinitions.RegistryUri(address),
        DependencySourceKind.Https => HttpsUri(address),
        _ => throw new InvalidDataException("不支持的依赖来源类型。")
    };
    internal static Uri HttpsUri(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || !uri.IsDefaultPort)
            throw new InvalidDataException("文件来源须为无认证的 HTTPS 目录地址。");
        return new(uri.AbsoluteUri.TrimEnd('/') + "/");
    }
    internal static Uri IndexUri(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || !uri.IsDefaultPort ||
            !uri.AbsolutePath.TrimEnd('/').EndsWith("/simple", StringComparison.Ordinal))
            throw new InvalidDataException("PyPI 来源须为无认证的 HTTPS Simple API 地址，例如 https://pypi.org/simple/。");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }
    internal static Uri ApiUri(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || !uri.IsDefaultPort ||
            (uri.Host == "api.github.com" ? uri.AbsolutePath != "/" : uri.AbsolutePath.TrimEnd('/') != "/api/v3"))
            throw new InvalidDataException("请填写 GitHub API 地址： https://api.github.com/ 或 https://主机/api/v3/。");
        return new(uri.AbsoluteUri.TrimEnd('/') + "/");
    }
}

[JsonConverter(typeof(DependencySourceKindConverter))]
public enum DependencySourceKind { GitHub, PythonIndex, NpmRegistry, Https }

internal sealed class DependencySourceKindConverter : JsonStringEnumConverter<DependencySourceKind>
{
    public DependencySourceKindConverter() : base(System.Text.Json.JsonNamingPolicy.CamelCase, allowIntegerValues: false) { }
}
