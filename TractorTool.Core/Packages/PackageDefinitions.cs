using System.Text.Json;
using TractorTool.Contracts.Packages;
using TractorTool.Core.Storage;

namespace TractorTool.Core.Packages;

public sealed record PackageTool(string Path, string VersionPrefix,
    IReadOnlyList<string>? Arguments = null, string Output = "stdout");
public sealed record PackageDefinition(PackageReference Package, string Rid, string Name, string Description,
    Uri DownloadUri, string Sha256, string License, Uri SourceUri, IReadOnlyDictionary<string, PackageTool> Tools);

public static class PackageDefinitions
{
    internal static PackageDefinition Snapshot(PackageDefinition definition)
    {
        Validate(definition);
        return definition with { Tools = new System.Collections.ObjectModel.ReadOnlyDictionary<string, PackageTool>(
            definition.Tools.ToDictionary(p => p.Key, p => p.Value with
            { Arguments = p.Value.Arguments is { } args ? Array.AsReadOnly(args.ToArray()) : null })) };
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
            if (tool is null || string.IsNullOrWhiteSpace(tool.VersionPrefix) || tool.VersionPrefix.Length > 512 ||
                tool.Output is not ("stdout" or "stderr") || tool.Arguments is { Count: > 16 } ||
                tool.Arguments?.Any(a => a is null || a.Length > 1024 || a.Contains('\0')) == true)
                throw new InvalidDataException("工具验证信息无效。");
            _ = PackageFiles.RelativeParts(tool.Path);
        }
        // Receipts are capped at 64 KiB; reserve room for up to 32 tool hashes and their keys.
        if (JsonSerializer.SerializeToUtf8Bytes(d).Length > 48 * 1024)
            throw new InvalidDataException("依赖包定义过大，无法保存完整安装记录。");
    }

}
