using System.Security.Cryptography;
using TractorTool.Plugins.FolderHistory.Models;

namespace TractorTool.Plugins.FolderHistory.Storage;

public static class WorkingTree
{
    public static Task<IReadOnlyList<HistoryEntry>> ScanAsync(string root, CancellationToken token) =>
        ScanAsync(root, excludeHistory: true, token);

    public static Task<IReadOnlyList<HistoryEntry>> ScanAsync(string root, IReadOnlyList<string> excludedRelativePaths,
        CancellationToken token) => ScanAsync(root, true, token, HistoryExclusions.Normalize(excludedRelativePaths));

    public static Task<IReadOnlyList<HistoryEntry>> ScanAsync(string root, bool excludeHistory, CancellationToken token) =>
        ScanAsync(root, excludeHistory, token, []);

    private static async Task<IReadOnlyList<HistoryEntry>> ScanAsync(string root, bool excludeHistory, CancellationToken token,
        IReadOnlyList<string> exclusions)
    {
        token.ThrowIfCancellationRequested();
        root = HistoryPaths.Absolute(root);
        HistoryPaths.RequireDirectory(root);
        var items = new Dictionary<string, HistoryEntry>(HistoryPaths.Comparer);
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(root))
                await VisitAsync(path, Path.GetFileName(path)).ConfigureAwait(false);
        }
        catch (IOException error) when (excludeHistory && (error is FileNotFoundException or DirectoryNotFoundException ||
            OperatingSystem.IsWindows() && (error.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021))))
        {
            // Editors replace or briefly lock files during a save. A missing root still requires attention.
            HistoryPaths.RequireDirectory(root);
            throw new WorkingTreeChangedException("文件在扫描期间被移动、删除或占用，稍后重新核对。", error);
        }
        token.ThrowIfCancellationRequested();
        return items.Values.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray();

        async Task VisitAsync(string path, string relative)
        {
            token.ThrowIfCancellationRequested();
            if (excludeHistory && HistoryPaths.IsHistoryPath(relative)) return;
            relative = HistoryPaths.NormalizeRelative(relative);
            if (HistoryExclusions.IsExcluded(relative, exclusions)) return;
            var attributes = HistoryPaths.Attributes(path) ?? throw new WorkingTreeChangedException("扫描时文件或目录消失：" + relative);
            var directory = attributes.HasFlag(FileAttributes.Directory);
            var item = directory ? new HistoryEntry(relative, true, 0, "") : await ReadFileAsync(path, relative, token).ConfigureAwait(false);
            if (!items.TryAdd(relative, item)) throw new InvalidDataException("文件名大小写冲突：" + relative);
            if (!directory) return;
            HistoryPaths.RequireDirectory(path);
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
                await VisitAsync(child, relative + "/" + Path.GetFileName(child)).ConfigureAwait(false);
        }
    }

    internal static async Task CopyFileAsync(string source, string destination, Action created, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        HistoryPaths.CheckPath(destination);
        using (var input = OpenRead(source))
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
        {
            created();
            await input.CopyToAsync(output, token).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            File.SetLastWriteTimeUtc(output.SafeFileHandle, File.GetLastWriteTimeUtc(input.SafeFileHandle));
        }
    }

    internal static async Task<HistoryEntry?> ReadEntryAsync(string root, string relative, CancellationToken token)
    {
        var path = HistoryPaths.Absolute(root);
        var actual = new List<string>();
        foreach (var part in HistoryPaths.NormalizeRelative(relative).Split('/'))
        {
            token.ThrowIfCancellationRequested();
            HistoryPaths.RequireDirectory(path);
            var match = Directory.EnumerateFileSystemEntries(path)
                .SingleOrDefault(item => HistoryPaths.Comparer.Equals(Path.GetFileName(item), part));
            if (match is null) return null;
            path = match;
            actual.Add(Path.GetFileName(path));
        }
        HistoryPaths.CheckPath(path);
        var attributes = HistoryPaths.Attributes(path);
        return attributes is null ? null : attributes.Value.HasFlag(FileAttributes.Directory)
            ? new(string.Join('/', actual), true, 0, "")
            : await ReadFileAsync(path, string.Join('/', actual), token).ConfigureAwait(false);
    }

    internal static async Task<HistoryEntry> ReadFileAsync(string path, string relative, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var input = OpenRead(path);
        var length = input.Length;
        var modified = File.GetLastWriteTimeUtc(input.SafeFileHandle);
        var hash = await SHA256.HashDataAsync(input, token).ConfigureAwait(false);
        HistoryPaths.CheckPath(path);
        if (length != input.Position || length != input.Length || modified != File.GetLastWriteTimeUtc(input.SafeFileHandle))
            throw new WorkingTreeChangedException("文件在读取期间发生变化：" + relative);
        return new(relative, false, length, Convert.ToHexString(hash));
    }

    internal static async Task MoveExpectedFileAsync(string source, string destination, HistoryEntry expected, CancellationToken token)
    {
        HistoryPaths.CheckPath(source);
        HistoryPaths.CheckPath(destination);
        // Keep writes denied across the rename; a new destination occupant is never overwritten.
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(input, token).ConfigureAwait(false);
        if (input.Length != expected.Length || input.Position != expected.Length || Convert.ToHexString(hash) != expected.Hash)
            throw new IOException("文件在移动前发生变化，未覆盖目标：" + expected.Path);
        token.ThrowIfCancellationRequested();
        File.Move(source, destination, overwrite: false);
        // Once published, finish verification even if cancellation arrived during the rename.
        var moved = await ReadFileAsync(destination, expected.Path, CancellationToken.None).ConfigureAwait(false);
        if (moved != expected)
        {
            if (HistoryPaths.Attributes(source) is null)
            {
                File.Move(destination, source, overwrite: false);
                throw new IOException("移动中的文件发生变化，冲突内容已无覆盖地返还原位置：" + source);
            }
            throw new IOException("移动中的文件发生变化，原位置已有新内容；两份内容均已保留：" + destination);
        }
    }

    private static FileStream OpenRead(string path)
    {
        HistoryPaths.CheckPath(path);
        // On Windows, deny writes and renames while capturing the original bytes.
        return new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }
}
