namespace TractorTool.Plugins.FolderHistory.Storage;

internal sealed record WorkingDirectoryEntry(string Path, bool IsDirectory, bool Unavailable);

// A browser listing reads names and attributes only, and never descends into a collapsed branch.
internal sealed class WorkingDirectoryListing(IReadOnlyList<string> exclusions)
{
    public IReadOnlyList<string> Exclusions { get; } = exclusions;
    public Dictionary<string, WorkingDirectoryEntry[]> Children { get; } = new(HistoryPaths.Comparer);
    public HashSet<string> Unreadable { get; } = new(HistoryPaths.Comparer);
    public string Warning { get; private set; } = "";

    public static WorkingDirectoryListing Read(string root, Guid repositoryId, IReadOnlySet<string> expanded,
        IReadOnlyList<string> exclusions, CancellationToken token, Action<string>? entering = null)
    {
        var result = new WorkingDirectoryListing(exclusions);
        try
        {
            HistoryStore.RequireIdentity(root, repositoryId);
            result.ReadDirectories(root, expanded, token, entering);
            HistoryStore.RequireIdentity(root, repositoryId);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            result.Children.Clear();
            result.Unreadable.Add("");
            result.Warning = "无法核对当前目录的历史库：" + error.Message;
        }
        return result;
    }

    private void ReadDirectories(string root, IReadOnlySet<string> expanded, CancellationToken token, Action<string>? entering)
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
                    entries.Add(new(relative, isDirectory, unavailable));
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
