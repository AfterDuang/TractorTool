using System.Security.Cryptography;
using TractorTool.Plugins.FolderHistory.Models;

namespace TractorTool.Plugins.FolderHistory.Storage;

internal sealed record WorkingDirectoryEntry(string Path, bool IsDirectory, bool Unavailable, HistoryEntry? Content = null);

// Only visible files with history need a content check; collapsed branches stay unread.
internal sealed class WorkingDirectoryListing(IReadOnlyList<string> exclusions)
{
    public IReadOnlyList<string> Exclusions { get; } = exclusions;
    public Dictionary<string, WorkingDirectoryEntry[]> Children { get; } = new(HistoryPaths.Comparer);
    public HashSet<string> Unreadable { get; } = new(HistoryPaths.Comparer);
    public string Warning { get; private set; } = "";

    public static WorkingDirectoryListing Read(string root, Guid repositoryId, IReadOnlySet<string> expanded,
        IReadOnlyList<string> exclusions, CancellationToken token, Action<string>? entering = null,
        IReadOnlySet<string>? trackedFiles = null, WorkingFileCache? cache = null)
    {
        token.ThrowIfCancellationRequested();
        var result = new WorkingDirectoryListing(exclusions);
        try
        {
            HistoryStore.RequireIdentity(root, repositoryId);
            result.ReadDirectories(root, expanded, token, entering, trackedFiles, cache ?? new());
            HistoryStore.RequireIdentity(root, repositoryId);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            result.Children.Clear();
            cache?.Invalidate();
            result.Unreadable.Add("");
            result.Warning = "无法核对当前目录的历史库：" + error.Message;
        }
        return result;
    }

    private void ReadDirectories(string root, IReadOnlySet<string> expanded, CancellationToken token, Action<string>? entering,
        IReadOnlySet<string>? trackedFiles, WorkingFileCache cache)
    {
        var pending = new Stack<string>();
        pending.Push("");
        while (pending.TryPop(out var parent))
        {
            token.ThrowIfCancellationRequested();
            entering?.Invoke(parent);
            try
            {
                var directory = parent.Length == 0 ? root : HistoryPaths.ResolveWorkPath(root, parent);
                HistoryPaths.RequireDirectory(directory);
                var entries = new List<WorkingDirectoryEntry>();
                foreach (var child in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    token.ThrowIfCancellationRequested();
                    if (HistoryPaths.Comparer.Equals(child.Name, HistoryPaths.DirectoryName)) continue;
                    var relative = parent.Length == 0 ? child.Name : parent + "/" + child.Name;
                    var attributes = child.Attributes;
                    if (attributes == (FileAttributes)(-1)) continue;
                    var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                    var unavailable = (attributes & HistoryPaths.UnsupportedAttributes) != 0;
                    HistoryEntry? content = null;
                    if (!isDirectory && !unavailable && trackedFiles?.Contains(relative) == true &&
                        !HistoryExclusions.IsExcluded(relative, Exclusions))
                    {
                        try { content = cache.Read(child.FullName, relative, token); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                        {
                            unavailable = true;
                            cache.Invalidate(relative);
                            if (Warning.Length == 0) Warning = "无法核对文件 " + relative + "：" + error.Message;
                        }
                    }
                    entries.Add(new(relative, isDirectory, unavailable, content));
                    if (isDirectory && !unavailable && expanded.Contains(relative) && expanded.Contains(parent) &&
                        !HistoryExclusions.IsExcluded(relative, Exclusions)) pending.Push(relative);
                }
                Children.Add(parent, entries.ToArray());
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Unreadable.Add(parent);
                if (Warning.Length == 0)
                    Warning = "无法读取目录 " + (parent.Length == 0 ? root : parent) + "：" + error.Message;
            }
        }
    }
}

internal sealed class WorkingFileCache
{
    private sealed record CachedFile(long Length, DateTime Created, DateTime Modified, HistoryEntry? Content);
    private readonly object _gate = new();
    private readonly Dictionary<string, CachedFile> _files = new(HistoryPaths.Comparer);

    public void Invalidate(string? path = null, bool includeDescendants = false)
    {
        lock (_gate)
        {
            if (path is null) _files.Clear();
            else
            {
                if (_files.Remove(path) || !includeDescendants) return;
                foreach (var key in _files.Keys.Where(key => HistoryPaths.IsSameOrDescendant(path, key)).ToArray())
                    _files.Remove(key);
            }
        }
    }

    public HistoryEntry Read(string path, string relative, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        HistoryPaths.CheckPath(path);
        // Browsing must not prevent the editor from saving; discard a hash if its file changes.
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var stamp = (input.Length, File.GetCreationTimeUtc(input.SafeFileHandle), File.GetLastWriteTimeUtc(input.SafeFileHandle));
        var pending = new CachedFile(stamp.Item1, stamp.Item2, stamp.Item3, null);
        lock (_gate)
        {
            if (_files.TryGetValue(relative, out var cached) && cached.Content?.Path == relative &&
                (cached.Length, cached.Created, cached.Modified) == stamp) return cached.Content;
            _files[relative] = pending;
        }
        var hash = SHA256.HashDataAsync(input, token).GetAwaiter().GetResult();
        HistoryPaths.CheckPath(path);
        var current = new FileInfo(path);
        if (input.Position != stamp.Item1 ||
            stamp != (input.Length, File.GetCreationTimeUtc(input.SafeFileHandle), File.GetLastWriteTimeUtc(input.SafeFileHandle)) ||
            stamp != (current.Length, current.CreationTimeUtc, current.LastWriteTimeUtc))
            throw new WorkingTreeChangedException("文件在读取期间发生变化：" + relative);
        var content = new HistoryEntry(relative, false, stamp.Item1, Convert.ToHexString(hash));
        lock (_gate)
        {
            if (!ReferenceEquals(_files.GetValueOrDefault(relative), pending))
                throw new WorkingTreeChangedException("文件在读取期间发生变化：" + relative);
            _files[relative] = pending with { Content = content };
        }
        return content;
    }
}
