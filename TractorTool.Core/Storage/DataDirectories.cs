namespace TractorTool.Core.Storage;

internal static class DataDirectories
{
    internal static readonly string[] PersistentNames = ["Config", "Data", "Packages", "Logs"];
    internal static readonly string[] OwnedNames = [.. PersistentNames, "Cache"];

    // Directory.Exists also returns false for inaccessible paths; that must not authorize a reset.
    internal static bool Exists(string root)
    {
        root = AppPaths.Absolute(root);
        var current = Path.GetPathRoot(root)!;
        RequireDirectory(current, File.GetAttributes(current));
        foreach (var part in Path.GetRelativePath(current, root).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part == ".") continue;
            var next = Path.Combine(current, part);
            var attributes = new DirectoryInfo(next).Attributes;
            if (attributes == (FileAttributes)(-1))
            {
                // Confirm the parent is accessible; direct lookup also accepts Windows short names.
                _ = Directory.EnumerateFileSystemEntries(current).Take(1).ToArray();
                if (new Uri(root).IsUnc) throw new IOException("网络数据目录不可用，请恢复连接后重试。");
                if (OperatingSystem.IsWindows() && new DriveInfo(Path.GetPathRoot(root)!).DriveType is
                    DriveType.Network or DriveType.Unknown or DriveType.NoRootDirectory)
                    throw new IOException("无法确认本地数据目录已删除，请恢复磁盘或网络连接后重试。");
                return false;
            }
            RequireDirectory(next, attributes);
            current = next;
        }
        return true;
    }

    private static void RequireDirectory(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("数据目录不能经过链接或重解析点：" + path);
        if ((attributes & FileAttributes.Directory) == 0)
            throw new IOException("数据目录被文件占用：" + path);
    }

    internal static void CheckPath(string path)
    {
        for (string? current = AppPaths.Absolute(path); current is not null; current = Path.GetDirectoryName(current))
        {
            // Attributes returns -1 for a missing entry; access and IO errors still propagate.
            var attributes = new FileInfo(current).Attributes;
            if (attributes == (FileAttributes)(-1)) continue;
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("数据路径不能经过链接或重解析点：" + current);
        }
    }

    internal static void Clear(string root, CancellationToken token)
    {
        root = AppPaths.Absolute(root);
        if (Path.GetDirectoryName(root) is null) throw new IOException("不能清理卷根目录。");
        if (!Exists(root)) throw new DirectoryNotFoundException(root);
        var entries = new List<string>();
        var children = Directory.GetFileSystemEntries(root);
        foreach (var name in OwnedNames)
        {
            var path = Path.Combine(root, name);
            CheckPath(path);
            if (!children.Contains(path, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)) continue;
            RequireDirectory(path, File.GetAttributes(path));
            Collect(path);
        }
        // Validate every tree before deleting anything; never follow a link out of the data root.
        foreach (var path in entries)
        {
            token.ThrowIfCancellationRequested();
            CheckPath(path);
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) Directory.Delete(path);
            else File.Delete(path);
        }
        void Collect(string path)
        {
            token.ThrowIfCancellationRequested();
            CheckPath(path);
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                foreach (var child in Directory.EnumerateFileSystemEntries(path)) Collect(child);
            entries.Add(path);
        }
    }
}
