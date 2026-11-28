using System.Collections.ObjectModel;
using System.Text.Json;
using TractorTool.Contracts.Packages;
using TractorTool.Core.Storage;

namespace TractorTool.Core.Packages;

public sealed record PackageTool(string Path, string VersionPrefix,
    IReadOnlyList<string>? Arguments = null, string Output = "stdout");
public sealed record PackageDefinition(PackageReference Package, string Rid, string Name, string Description,
    Uri DownloadUri, string Sha256, string License, Uri SourceUri, IReadOnlyDictionary<string, PackageTool> Tools)
{
    public IReadOnlyDictionary<string, PackageTool> Tools { get; init; } = Tools ?? ReadOnlyDictionary<string, PackageTool>.Empty;
    public PackageKind Kind { get; init; }
    public IReadOnlyDictionary<string, string> Libraries { get; init; } = ReadOnlyDictionary<string, string>.Empty;
    public DependencySourceKind SourceKind { get; init; }
    public IReadOnlyList<PackageArchive> Archives { get; init; } = [];
}

public static class PackageDefinitions
{
    internal static PackageDefinition Snapshot(PackageDefinition definition)
    {
        Validate(definition);
        return definition with
        {
            Tools = new ReadOnlyDictionary<string, PackageTool>(definition.Tools.ToDictionary(p => p.Key, p => p.Value with
                { Arguments = p.Value.Arguments is { } args ? Array.AsReadOnly(args.ToArray()) : null }, StringComparer.Ordinal)),
            Libraries = new ReadOnlyDictionary<string, string>(definition.Libraries.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)),
            Archives = Array.AsReadOnly(definition.Archives.Select(a => a with
                { Files = new ReadOnlyDictionary<string, string>(a.Files.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)) }).ToArray())
        };
    }

    internal static void Validate(PackageDefinition d)
    {
        if (d is null || d.Package is null) throw new InvalidDataException("依赖包定义为空。");
        AppPaths.ValidateId(d.Package.Id); AppPaths.ValidateId(d.Package.Version); AppPaths.ValidateId(d.Rid);
        if (string.IsNullOrWhiteSpace(d.Name) || d.DownloadUri is not { IsAbsoluteUri: true, Scheme: "https" } ||
            d.SourceUri is not { IsAbsoluteUri: true, Scheme: "https" } || d.Sha256 is not { Length: 64 } ||
            !d.Sha256.All(Uri.IsHexDigit) || d.Tools is null || d.Libraries is null || d.Archives is null)
            throw new InvalidDataException("依赖包元数据无效。");
        switch (d.Kind)
        {
            case PackageKind.Tool when d.Tools.Count is > 0 and <= 32 && d.Libraries.Count == 0:
                break;
            case PackageKind.NativeLibrary when d.Tools.Count == 0 && d.Libraries.Count is > 0 and <= 32 &&
                d.Rid is "win-x64" or "win-x86" or "win-arm64":
                break;
            default:
                throw new InvalidDataException("依赖包类型、平台或入口声明无效；工具与原生运行库不能混合声明。");
        }
        if (d.Archives.Count != 0)
        {
            if (d.Kind != PackageKind.NativeLibrary) throw new InvalidDataException("多归档仅支持原生运行库。");
            PackageArchiveDefinitions.Validate(d.Archives, d.SourceKind, d.SourceUri);
            if (d.DownloadUri != d.Archives[0].Url || !string.Equals(d.Sha256,
                PackageArchiveDefinitions.Identity(d.Archives, d.SourceKind, d.SourceUri), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("原生归档组合摘要与安装资料不符。");
            var targets = d.Archives.SelectMany(a => a.Files.Values).ToHashSet(StringComparer.Ordinal);
            if (d.Libraries.Values.Any(path => !targets.Contains(path)))
                throw new InvalidDataException("原生运行库入口未包含在归档映射中。");
        }
        else if (d.SourceKind != DependencySourceKind.GitHub)
            throw new InvalidDataException("单 ZIP 安装资料的来源类型无效。");
        foreach (var (name, tool) in d.Tools)
        {
            AppPaths.ValidateId(name);
            if (tool is null || string.IsNullOrWhiteSpace(tool.VersionPrefix) || tool.VersionPrefix.Length > 512 ||
                tool.Output is not ("stdout" or "stderr") || tool.Arguments is { Count: > 16 } ||
                tool.Arguments?.Any(a => a is null || a.Length > 1024 || a.Contains('\0')) == true)
                throw new InvalidDataException("工具验证信息无效。");
            _ = PackageFiles.RelativeParts(tool.Path);
        }
        foreach (var (name, path) in d.Libraries)
        {
            AppPaths.ValidateId(name);
            _ = PackageFiles.RelativeParts(path);
            if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("原生运行库入口必须是包内 DLL 相对路径。");
        }
        // Receipts are capped at 64 KiB; reserve room for up to 32 tool hashes and their keys.
        // Defaulted native fields must not push a previously valid schema 1/2 definition over its limit.
        var bytes = d.Kind == PackageKind.Tool
            ? JsonSerializer.SerializeToUtf8Bytes(new { d.Package, d.Rid, d.Name, d.Description, d.DownloadUri,
                d.Sha256, d.License, d.SourceUri, d.Tools })
            : d.Archives.Count == 0
                ? JsonSerializer.SerializeToUtf8Bytes(new { d.Package, d.Rid, d.Name, d.Description, d.DownloadUri,
                    d.Sha256, d.License, d.SourceUri, d.Tools, d.Kind, d.Libraries })
                : JsonSerializer.SerializeToUtf8Bytes(d);
        if (bytes.Length > 48 * 1024)
            throw new InvalidDataException("依赖包定义过大，无法保存完整安装记录。");
    }

}
