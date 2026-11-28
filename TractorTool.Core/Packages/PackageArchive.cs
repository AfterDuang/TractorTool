using System.Security.Cryptography;
using System.Text.Json;

namespace TractorTool.Core.Packages;

public sealed record PackageArchive(Uri Url, string Sha256, IReadOnlyDictionary<string, string> Files);

internal static class PackageArchiveDefinitions
{
    internal static void Validate(IReadOnlyList<PackageArchive> archives, DependencySourceKind kind, Uri source)
    {
        if (archives.Count is < 1 or > 16 || kind is not (DependencySourceKind.PythonIndex or DependencySourceKind.Https) ||
            DependencySource.Address(kind, source.AbsoluteUri) != source)
            throw new InvalidDataException("原生归档数量或来源无效。");
        HashSet<Uri> urls = [];
        HashSet<string> targets = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> spellings = new(StringComparer.OrdinalIgnoreCase);
        foreach (var archive in archives)
        {
            if (archive is null || archive.Sha256 is not { Length: 64 } || !archive.Sha256.All(Uri.IsHexDigit) ||
                archive.Files is not { Count: > 0 } || !urls.Add(archive.Url))
                throw new InvalidDataException("归档地址、摘要或文件映射无效或重复。");
            ValidateDownloadUri(kind, source, archive.Url);
            if (!archive.Url.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                !archive.Url.AbsolutePath.EndsWith(".whl", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("原生归档仅支持 ZIP 和 wheel 文件。");
            HashSet<string> entries = new(StringComparer.OrdinalIgnoreCase);
            foreach (var (entry, target) in archive.Files)
            {
                if (string.Join('/', PackageFiles.RelativeParts(entry)) != entry || !entries.Add(entry))
                    throw new InvalidDataException("归档内文件映射须使用唯一的正斜杠相对路径。");
                var parts = PackageFiles.RelativeParts(target);
                if (string.Join('/', parts) != target || parts[0].Equals("receipt.json", StringComparison.OrdinalIgnoreCase) ||
                    parts[0].Equals(".package-files.json", StringComparison.OrdinalIgnoreCase) || !targets.Add(target) || directories.Contains(target))
                    throw new InvalidDataException("归档映射目标重复、占用目录或保留元数据路径。");
                for (var i = 1; i <= parts.Length; i++)
                {
                    var prefix = string.Join('/', parts.Take(i));
                    if (spellings.TryGetValue(prefix, out var spelling) && spelling != prefix)
                        throw new InvalidDataException("归档映射存在大小写冲突。");
                    spellings[prefix] = prefix;
                    if (i < parts.Length)
                    {
                        if (targets.Contains(prefix)) throw new InvalidDataException("归档映射存在文件与目录冲突。");
                        directories.Add(prefix);
                    }
                }
                if (targets.Count > 10000) throw new InvalidDataException("归档映射文件数超过限制。");
            }
        }
    }

    internal static void ValidateDownloadUri(DependencySourceKind kind, Uri source, Uri url)
    {
        if (url is not { IsAbsoluteUri: true, Scheme: "https", IsDefaultPort: true } ||
            url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0)
            throw new InvalidDataException("原生制品须使用无认证的固定 HTTPS 地址。");
        var allowed = kind switch
        {
            DependencySourceKind.PythonIndex when source.AbsoluteUri == DependencySource.PyPiUrl => url.Host == "files.pythonhosted.org",
            DependencySourceKind.PythonIndex => url.GetLeftPart(UriPartial.Authority) == source.GetLeftPart(UriPartial.Authority),
            DependencySourceKind.Https => url.AbsoluteUri.StartsWith(source.AbsoluteUri, StringComparison.Ordinal),
            _ => false
        };
        if (!allowed) throw new InvalidDataException($"归档地址不属于声明的来源：{url}。");
    }

    internal static string Identity(IReadOnlyList<PackageArchive> archives, DependencySourceKind kind, Uri source)
    {
        // Persist the complete recipe, so repair and offline reuse never depend on a plugin's current files.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            SourceKind = kind, Source = source.AbsoluteUri,
            Archives = archives.OrderBy(a => a.Url.AbsoluteUri, StringComparer.Ordinal).Select(a => new
            {
                Url = a.Url.AbsoluteUri, Sha256 = a.Sha256.ToLowerInvariant(),
                Files = a.Files.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new { Entry = p.Key, Target = p.Value })
            })
        });
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
