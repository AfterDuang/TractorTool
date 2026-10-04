using System.Text.Json;
using TractorTool.Contracts.Packages;
using TractorTool.Core.Storage;

namespace TractorTool.Core.Packages;

public sealed record PackageTool(string Path, string VersionPrefix);
public sealed record PackageDefinition(PackageReference Package, string Rid, string Name, string Description,
    Uri DownloadUri, string Sha256, string License, Uri SourceUri, IReadOnlyDictionary<string, PackageTool> Tools);

public static class PackageCatalog
{
    // The source index is maintained and distributed by the host, independently of compiled code.
    public static IReadOnlyList<PackageDefinition> Load(string path)
    {
        PackageFiles.CheckPath(path);
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("依赖源索引超过大小限制。");
        var index = JsonSerializer.Deserialize<SourceIndex>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (index is null || index.SchemaVersion != 1 || index.Packages is null || index.Packages.Length > 1000)
            throw new InvalidDataException("依赖源索引格式不支持。");
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in index.Packages)
        {
            Validate(definition);
            if (!keys.Add($"{definition.Package.Id}/{definition.Package.Version}/{definition.Rid}"))
                throw new InvalidDataException("依赖源中存在重复包版本。");
        }
        return index.Packages;
    }

    internal static void Validate(PackageDefinition d)
    {
        if (d is null || d.Package is null) throw new InvalidDataException("依赖包定义为空。");
        AppPaths.ValidateId(d.Package.Id); AppPaths.ValidateId(d.Package.Version); AppPaths.ValidateId(d.Rid);
        if (string.IsNullOrWhiteSpace(d.Name) || d.DownloadUri is not { IsAbsoluteUri: true, Scheme: "https" } ||
            d.SourceUri is not { IsAbsoluteUri: true, Scheme: "https" } || d.Sha256 is not { Length: 64 } ||
            !d.Sha256.All(Uri.IsHexDigit) || d.Tools is not { Count: > 0 and <= 32 })
            throw new InvalidDataException("依赖包元数据无效。");
        foreach (var (name, tool) in d.Tools)
        {
            AppPaths.ValidateId(name);
            if (tool is null || string.IsNullOrWhiteSpace(tool.VersionPrefix)) throw new InvalidDataException("工具验证信息无效。");
            _ = PackageFiles.RelativeParts(tool.Path);
        }
    }

    private sealed record SourceIndex(int SchemaVersion, PackageDefinition[] Packages);
}
