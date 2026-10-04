using System.IO.Compression;
using TractorTool.Core.Storage;

namespace TractorTool.Core.Packages;

internal static class PackageFiles
{
    internal const long MaxDownload = 256L * 1024 * 1024;
    private const long MaxExpanded = 1024L * 1024 * 1024;

    internal static string SafePath(string root, string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        if (relative.Length > 240 || parts.Length > 32 || parts.Any(p => p.Length == 0 || p is "." or ".." ||
            p.EndsWith(' ') || p.EndsWith('.') || p.Any(c => c < 32 || "<>:\"|?*".Contains(c)) ||
            System.Text.RegularExpressions.Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            throw new InvalidDataException("工具包包含不安全的相对路径。");
        var result = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        if (result == root || !AppPaths.Contains(root, result)) throw new InvalidDataException("工具包路径越界。");
        CheckPath(result);
        return result;
    }

    internal static void CheckPath(string path)
    {
        // Include existing ancestors, not just the final component (junctions may redirect writes).
        for (string? current = AppPaths.Absolute(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("外部工具目录不接受链接或重解析点。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static async Task<long> ExtractAsync(string zip, string destination, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count > 10000) throw new InvalidDataException("工具包文件数超过限制。");
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> spellings = new(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            var directory = name.EndsWith('/');
            name = name.TrimEnd('/');
            var path = SafePath(destination, name);
            var parts = name.Split('/');
            for (var i = 1; i <= parts.Length; i++)
            {
                var prefix = string.Join('/', parts.Take(i));
                if (spellings.TryGetValue(prefix, out var spelling) && prefix != spelling)
                    throw new InvalidDataException("工具包路径包含大小写冲突。");
                spellings[prefix] = prefix;
            }
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (!names.Add(name) || unixType is not (0 or 0x8000 or 0x4000) ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || name == "receipt.json")
                throw new InvalidDataException("工具包包含重名、链接或非普通文件。");
            total = checked(total + entry.Length);
            if (entry.Length < 0 || total > MaxExpanded) throw new InvalidDataException("工具包展开大小超过限制。");
            if (directory)
            {
                if (entry.Length != 0) throw new InvalidDataException("工具包目录包含文件内容。");
                Directory.CreateDirectory(path); continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var input = entry.Open();
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920];
            long written = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, token)) != 0)
            {
                written += count;
                if (written > entry.Length) throw new InvalidDataException("工具包文件长度不符。");
                await output.WriteAsync(buffer.AsMemory(0, count), token);
            }
            if (written != entry.Length) throw new InvalidDataException("工具包文件不完整。");
        }
        return total;
    }

    internal static void DeleteOwnedTree(string root, string path, string? deleteLast = null)
    {
        root = AppPaths.Absolute(root); path = AppPaths.Absolute(path);
        if (root == path || !AppPaths.Contains(root, path)) throw new InvalidOperationException("拒绝删除工具根目录以外的路径。");
        CheckPath(path);
        if (!Directory.Exists(path)) return;
        // Never follow a reparse point during recursive cleanup.
        foreach (var child in Directory.EnumerateFileSystemEntries(path).OrderBy(child => Path.GetFileName(child) == deleteLast))
        {
            CheckPath(child);
            if (Directory.Exists(child)) DeleteOwnedTree(root, child);
            else File.Delete(child);
        }
        Directory.Delete(path);
    }
}
