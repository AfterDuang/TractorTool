using System.IO.Compression;
using TractorTool.Core.Storage;

namespace TractorTool.Core.Packages;

internal static class PackageFiles
{
    internal const long MaxDownload = 256L * 1024 * 1024;
    private const long MaxExpanded = 1024L * 1024 * 1024;

    internal static string SafePath(string root, string relative)
    {
        var parts = RelativeParts(relative);
        var result = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        if (result == root || !AppPaths.Contains(root, result)) throw new InvalidDataException("工具包路径越界。");
        CheckPath(result);
        return result;
    }

    internal static string[] RelativeParts(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) throw new InvalidDataException("工具路径为空。");
        var parts = relative.Replace('\\', '/').Split('/');
        if (relative.Length > 240 || parts.Length > 32 || parts.Any(p => p.Length == 0 || p is "." or ".." ||
            p.EndsWith(' ') || p.EndsWith('.') || p.Any(c => c < 32 || "<>:\"|?*".Contains(c)) ||
            System.Text.RegularExpressions.Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            throw new InvalidDataException("工具包包含不安全的相对路径。");
        return parts;
    }

    internal static void CheckPath(string path)
    {
        // Include existing ancestors, not just the final component (junctions may redirect writes).
        for (string? current = AppPaths.Absolute(path); current is not null; current = Path.GetDirectoryName(current))
        {
            // Attributes returns -1 for an absent path; other I/O failures still propagate.
            // Keep checking ancestors, including links whose children do not exist yet.
            var attributes = new FileInfo(current).Attributes;
            if (attributes != (FileAttributes)(-1) && (attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("外部工具目录不接受链接或重解析点。");
        }
    }

    internal static Task<long> ExtractAsync(string zip, string destination, CancellationToken token) =>
        ExtractArchivesAsync([(zip, null, Path.GetFileName(zip))], destination, token);

    internal static Task<long> ExtractMappedAsync(IReadOnlyList<(string Path, PackageArchive Definition)> archives,
        string destination, CancellationToken token) => ExtractArchivesAsync(archives.Select(a =>
            (a.Path, (IReadOnlyDictionary<string, string>?)a.Definition.Files, a.Definition.Url.AbsoluteUri)), destination, token);

    private static async Task<long> ExtractArchivesAsync(
        IEnumerable<(string Path, IReadOnlyDictionary<string, string>? Files, string Name)> archives,
        string destination, CancellationToken token)
    {
        Dictionary<string, (string Spelling, bool Directory)> targets = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> targetNames = new(StringComparer.OrdinalIgnoreCase);
        long expanded = 0, installed = 0;
        var entries = 0;
        foreach (var (zip, files, displayName) in archives)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var archive = ZipFile.OpenRead(zip);
                if (archive.Entries.Count > 10000 - entries) throw new InvalidDataException("工具包文件数超过限制。");
                entries += archive.Entries.Count;
                Dictionary<string, (string Spelling, bool Directory)> paths = new(StringComparer.OrdinalIgnoreCase);
                HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
                HashSet<string> selected = new(StringComparer.Ordinal);
                foreach (var entry in archive.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    var name = entry.FullName.Replace('\\', '/');
                    var directory = name.EndsWith('/');
                    name = name.TrimEnd('/');
                    _ = RelativeParts(name);
                    RegisterPath(name, directory, names, paths);
                    var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
                    if (unixType is not (0 or 0x8000 or 0x4000) || unixType == 0x4000 && !directory ||
                        unixType == 0x8000 && directory || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                        name.Equals("receipt.json", StringComparison.OrdinalIgnoreCase) ||
                        files is not null && NativePackageIntegrity.IsMetadata(name))
                        throw new InvalidDataException("工具包包含链接、保留记录或非普通文件。");
                    if (entry.Length < 0 || entry.Length > MaxExpanded - expanded) throw new InvalidDataException("工具包展开大小超过限制。");
                    expanded += entry.Length;
                    if (directory && entry.Length != 0) throw new InvalidDataException("工具包目录包含文件内容。");

                    // Even unselected entries participate in archive safety and cumulative limits.
                    string relative;
                    if (files is null) relative = name;
                    else if (files.TryGetValue(name, out var mapped))
                    {
                        if (directory) throw new InvalidDataException("文件映射指向归档目录。");
                        relative = mapped.Replace('\\', '/'); selected.Add(name);
                    }
                    else continue;
                    var path = SafePath(destination, relative);
                    if (files is not null) RegisterPath(relative, directory, targetNames, targets);
                    if (directory) { Directory.CreateDirectory(path); continue; }
                    if (files is not null && NativePackageIntegrity.IsMetadata(relative))
                        throw new InvalidDataException("文件映射包含宿主保留的安装记录。");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await CopyEntryAsync(entry, path, token).ConfigureAwait(false);
                    installed = checked(installed + entry.Length);
                }
                if (files is not null && selected.Count != files.Count)
                    throw new InvalidDataException("归档缺少声明的映射文件：" + string.Join(", ", files.Keys.Except(selected).Take(3)));
            }
            catch (InvalidDataException ex) when (files is not null)
            { throw new InvalidDataException($"归档 {displayName} 安装失败：{ex.Message}", ex); }
            catch (IOException ex) when (files is not null)
            { throw new IOException($"归档 {displayName} 安装失败：{ex.Message}", ex); }
        }
        return installed;
    }

    private static void RegisterPath(string name, bool directory, HashSet<string> names,
        Dictionary<string, (string Spelling, bool Directory)> paths)
    {
        if (!names.Add(name)) throw new InvalidDataException("工具包包含重名文件或目录。");
        var parts = name.Split('/');
        for (var i = 1; i <= parts.Length; i++)
        {
            var prefix = string.Join('/', parts.Take(i));
            var isDirectory = i < parts.Length || directory;
            if (paths.TryGetValue(prefix, out var previous) && (previous.Spelling != prefix || previous.Directory != isDirectory))
                throw new InvalidDataException("工具包路径包含大小写或文件/目录冲突。");
            paths[prefix] = (prefix, isDirectory);
        }
    }

    private static async Task CopyEntryAsync(ZipArchiveEntry entry, string path, CancellationToken token)
    {
        await using var input = entry.Open();
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        var buffer = new byte[81920];
        long written = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            written += count;
            if (written > entry.Length) throw new InvalidDataException("工具包文件长度不符。");
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
        if (written != entry.Length) throw new InvalidDataException("工具包文件不完整。");
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
