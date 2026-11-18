namespace TractorTool.Plugins.FolderHistory.Storage;

public static class HistoryPaths
{
    public const string DirectoryName = ".tractortool-history";
    internal static readonly StringComparer Comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly StringComparison Comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly char[] Separators = ['/', '\\'];
    private static readonly char[] InvalidNameCharacters = Path.GetInvalidFileNameChars();
    internal const FileAttributes UnsupportedAttributes = FileAttributes.ReparsePoint | FileAttributes.Device | FileAttributes.Offline;

    public static string NormalizeRoot(string path, string appRoot, string dataRoot)
    {
        var root = Absolute(path);
        if (Path.GetDirectoryName(root) is null || Overlaps(root, appRoot) || Overlaps(root, dataRoot))
            throw new InvalidDataException("请选择与软件目录和应用数据目录互不包含的本地子目录。");
        RejectHistoryContainer(root);
        RequireLocalVolume(root);
        RequireDirectory(root);
        return root;
    }

    public static string HistoryDirectory(string root) => Path.Combine(root, DirectoryName);

    public static bool Overlaps(string first, string second)
    {
        first = Absolute(first);
        second = Absolute(second);
        return Contains(first, second) || Contains(second, first);
    }

    private static bool Contains(string parent, string child) => Comparer.Equals(parent, child) || child.StartsWith(
        Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, Comparison);

    internal static bool IsHistoryPath(string relative) => Comparer.Equals(relative.Split(Separators, 2)[0], DirectoryName);

    internal static bool IsSameOrDescendant(string parent, string path) => Comparer.Equals(parent, path) ||
        path.Length > parent.Length && path[parent.Length] == '/' && Comparer.Equals(parent, path[..parent.Length]);

    internal static void RejectHistoryContainer(string path)
    {
        if (path.Split(Separators).Contains(DirectoryName, Comparer))
            throw new InvalidDataException("不能把历史库或其子目录用作工作目录或另存位置。");
    }

    internal static void RequireLocalVolume(string path)
    {
        if (OperatingSystem.IsWindows() && new DriveInfo(Path.GetPathRoot(path)!).DriveType is not (DriveType.Fixed or DriveType.Removable))
            throw new InvalidDataException("当前只支持本机普通磁盘上的目录。");
    }

    public static string NormalizeRelative(string relative)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relative);
        var parts = relative.Split(Separators);
        foreach (var part in parts) ValidateName(part);
        if (parts.Contains(DirectoryName, Comparer)) throw new InvalidDataException("不能使用任何层级的历史库保留路径；当前不支持嵌套历史库。");
        return string.Join('/', parts);
    }

    public static string ResolveWorkPath(string root, string relativePath)
    {
        root = Absolute(root);
        var path = Path.Combine(root, NormalizeRelative(relativePath).Replace('/', Path.DirectorySeparatorChar));
        // Windows normalization expands short aliases, which could otherwise bypass the reserved name.
        var normalized = Absolute(path);
        if (!Comparer.Equals(path, normalized)) throw new InvalidDataException("不能通过路径别名操作工作文件。");
        CheckPath(path);
        return path;
    }

    internal static string Absolute(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) || OperatingSystem.IsWindows() &&
            (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || !Separators.Contains(path[2])))
            throw new InvalidDataException("需要本机绝对路径。");
        var volume = Path.GetPathRoot(path)!;
        foreach (var part in path[volume.Length..].TrimEnd(Separators).Split(Separators, StringSplitOptions.RemoveEmptyEntries))
            ValidateName(part);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    internal static FileAttributes? Attributes(string path)
    {
        var attributes = new FileInfo(path).Attributes;
        if (attributes == (FileAttributes)(-1)) return null;
        if ((attributes & UnsupportedAttributes) != 0) throw new InvalidDataException("路径包含链接、设备或离线文件：" + path);
        return attributes;
    }

    internal static void CheckPath(string path)
    {
        var parents = new Stack<string>();
        for (var current = Path.GetDirectoryName(path); current is not null; current = Path.GetDirectoryName(current)) parents.Push(current);
        foreach (var current in parents)
            if (Attributes(current) is { } attributes && !attributes.HasFlag(FileAttributes.Directory))
                throw new IOException("路径的上级被文件占用：" + current);
        _ = Attributes(path);
    }

    internal static void RequireDirectory(string path)
    {
        CheckPath(path);
        if (Attributes(path)?.HasFlag(FileAttributes.Directory) != true) throw new DirectoryNotFoundException("目录不可用：" + path);
    }

    private static void ValidateName(string part)
    {
        if (part.Length == 0 || part is "." or ".." || part[^1] is ' ' or '.' ||
            part.IndexOfAny(InvalidNameCharacters) >= 0 || part.Contains(':'))
            throw new InvalidDataException("路径包含不支持的名称：" + part);
        if (!OperatingSystem.IsWindows()) return;
        var stem = part.Split('.', 2)[0].TrimEnd(' ').ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
            "0123456789¹²³".Contains(stem[3]))
            throw new InvalidDataException("路径包含 Windows 保留名称：" + part);
    }
}
