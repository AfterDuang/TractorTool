using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TractorTool.Contracts.Packages;

namespace TractorTool.Core.Packages;

// Download instructions belong to the plugin; PackageReference remains the stable public identity.
public sealed record PluginDependency(string Id, string Version)
{
    public GitHubPackage[] Downloads { get; init; } = [];
    [JsonIgnore] public PackageReference Package => new(Id, Version);
}

public sealed record GitHubPackage(string Rid, string Repository, string Tag, string Asset,
    string Sha256, string Name, string Description, string License, IReadOnlyDictionary<string, PackageTool> Tools)
{
    public string Source { get; init; } = DependencySource.GitHubUrl;

    internal PackageDefinition Definition(PackageReference package)
    {
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
            new($"{release}/tag/{Uri.EscapeDataString(Tag)}"), Tools);
        PackageDefinitions.Validate(result);
        return result;
    }

    internal GitHubPackage Snapshot(PackageReference package) => this with
    { Source = DependencySource.ApiUri(Source).AbsoluteUri, Tools = PackageDefinitions.Snapshot(Definition(package)).Tools };
}

public sealed record DependencySource(string Name, string Url)
{
    public const string GitHubUrl = "https://api.github.com/";
    [JsonIgnore] public bool IsDefault => Url == GitHubUrl;
    internal static Uri ApiUri(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || !uri.IsDefaultPort ||
            (uri.Host == "api.github.com" ? uri.AbsolutePath != "/" : uri.AbsolutePath.TrimEnd('/') != "/api/v3"))
            throw new InvalidDataException("请填写 GitHub API 地址： https://api.github.com/ 或 https://主机/api/v3/。");
        return new(uri.AbsoluteUri.TrimEnd('/') + "/");
    }
}
