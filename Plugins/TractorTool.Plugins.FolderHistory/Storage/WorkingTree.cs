using System.Security.Cryptography;
using TractorTool.Plugins.FolderHistory.Models;

namespace TractorTool.Plugins.FolderHistory.Storage;

public static class WorkingTree
{
    public static Task<IReadOnlyList<HistoryEntry>> ScanAsync(string root, CancellationToken token) =>
        ScanAsync(root, excludeHistory: true, token);

    public static async Task<IReadOnlyList<HistoryEntry>> ScanAsync(string root, bool excludeHistory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        root = HistoryPaths.Absolute(root);
        HistoryPaths.RequireDirectory(root);
        var items = new Dictionary<string, HistoryEntry>(HistoryPaths.Comparer);
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
            await VisitAsync(path, Path.GetFileName(path)).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return items.Values.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray();

        async Task VisitAsync(string path, string relative)
        {
            token.ThrowIfCancellationRequested();
            if (excludeHistory && HistoryPaths.IsHistoryPath(relative)) return;
            relative = HistoryPaths.NormalizeRelative(relative);
            var attributes = HistoryPaths.Attributes(path) ?? throw new IOException("扫描时文件或目录消失：" + relative);
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

    private static async Task<HistoryEntry> ReadFileAsync(string path, string relative, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var input = OpenRead(path);
        var length = input.Length;
        var modified = File.GetLastWriteTimeUtc(input.SafeFileHandle);
        var hash = await SHA256.HashDataAsync(input, token).ConfigureAwait(false);
        HistoryPaths.CheckPath(path);
        if (length != input.Position || length != input.Length || modified != File.GetLastWriteTimeUtc(input.SafeFileHandle))
            throw new IOException("文件在读取期间发生变化：" + relative);
        return new(relative, false, length, Convert.ToHexString(hash));
    }

    private static FileStream OpenRead(string path)
    {
        HistoryPaths.CheckPath(path);
        // On Windows, deny writes and renames while capturing the original bytes.
        return new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }
}
