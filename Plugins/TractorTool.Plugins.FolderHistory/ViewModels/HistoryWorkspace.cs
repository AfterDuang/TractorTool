using Avalonia;
using TractorTool.Plugins.FolderHistory.Models;
using TractorTool.Plugins.FolderHistory.Storage;

namespace TractorTool.Plugins.FolderHistory.ViewModels;

internal enum TreePresence { Recorded, Untracked, Historical, Excluded, Unavailable }

internal sealed record TreeItem(string Path, string Name, int Depth, bool IsDirectory, bool HasChildren,
    TreePresence Presence, bool IsExpanded = false)
{
    public Thickness Indent => new(Depth * 16, 0, 0, 0);
    public bool IsUntracked => Presence == TreePresence.Untracked;
    public bool IsHistoricalOnly => Presence == TreePresence.Historical;
    public string StatusText => Presence switch
    {
        TreePresence.Untracked => "未快照",
        TreePresence.Historical => "仅历史",
        TreePresence.Excluded => "已排除",
        TreePresence.Unavailable => "无法读取",
        _ => ""
    };
    public string StatusDescription => (Path.Length == 0 ? Name : Path) + (Presence switch
    {
        TreePresence.Untracked => "，尚未收录到最新快照",
        TreePresence.Historical => "，仅在历史中存在",
        TreePresence.Excluded => "，已从后续快照排除",
        TreePresence.Unavailable => "，无法核对当前文件",
        _ => ""
    });

    public static TreeItem[] ResolveSelection(IEnumerable<TreeItem> selection, IReadOnlyDictionary<string, TreeItem> visible) =>
        selection.Select(item => visible.GetValueOrDefault(item.Path)).OfType<TreeItem>()
            .DistinctBy(item => item.Path, HistoryPaths.Comparer).ToArray();
}

internal sealed record VersionItem(SavedVersion Saved, IReadOnlyList<HistoryChange> Changes, string Summary)
{
    public IReadOnlyDictionary<string, HistoryEntry> Entries { get; } =
        Saved.Version.Entries.ToDictionary(entry => entry.Path, HistoryPaths.Comparer);
    public string Id => Saved.Id;
    public string Title => Saved.Title;
    public string NumberLabel => Saved.NumberLabel;
    public string SavedAt => Saved.SavedAt;
    public string SelectionLabel => Note.Length == 0 ? NumberLabel : $"{NumberLabel} · {Note}";
    public string Note => Saved.Note;
    public bool Pinned => Saved.Pinned;
    public bool IsRestoreProtection => Saved.IsRestoreProtection;
}

internal sealed record FileItem(string Path, string Kind, HistoryEntry? Before, HistoryEntry? After)
{
    public static FileItem ForChange(HistoryChange change, VersionItem? before, VersionItem? after) =>
        new(change.Path, change.Kind,
            change.Kind == "新增" ? null : before?.Entries.GetValueOrDefault(change.Path),
            change.Kind == "删除" ? null : after?.Entries.GetValueOrDefault(change.Path));

    public static FileItem ForFile(string path, VersionItem? before, VersionItem after)
    {
        var left = before?.Entries.GetValueOrDefault(path);
        var right = after.Entries.GetValueOrDefault(path);
        var kind = (left, right) switch
        {
            (null, null) => "两版均未收录",
            (null, _) => "新增",
            (_, null) => "删除",
            _ when left.Path != right.Path && left.Hash != right.Hash => "名称及内容已修改",
            _ when left.Path != right.Path => "名称大小写变化",
            _ => left == right ? "相同" : "修改"
        };
        return new(right?.Path ?? left?.Path ?? path, kind, left, right);
    }

    public bool IsDirectory => (After ?? Before)?.IsDirectory == true;
    public string SizeLabel => $"{Describe(Before)} → {Describe(After)}";
    public static string Describe(HistoryEntry? entry) => entry switch
    {
        null => "此版本中没有此项",
        { IsDirectory: true } => "文件夹",
        _ => FolderHistoryViewModel.FormatSize(entry.Length)
    };
}

internal sealed record VersionActionContext(string Root, string VersionId, string VersionTitle,
    IReadOnlyList<string> RelativePaths, string ScopeTitle, string? SingleFilePath, string? UnavailableReason,
    bool Pinned)
{
    public static VersionActionContext? Capture(string root, VersionItem row, IEnumerable<string> selection, string title)
    {
        var paths = selection.ToArray();
        if (paths.Length == 0) return null;
        var missing = paths.FirstOrDefault(path => path.Length != 0 && !row.Entries.ContainsKey(path));
        var file = paths.Length == 1 && row.Entries.GetValueOrDefault(paths[0]) is { IsDirectory: false } item ? item.Path : null;
        return new(root, row.Id, row.Title, Array.AsReadOnly(paths), title, file,
            missing is null ? null : "此版本没有所选项目：" + missing, row.Pinned);
    }
}

internal sealed class HistoryWorkspace
{
    private readonly Dictionary<string, TreeItem[]> _children;
    private readonly TreeItem _root;
    public VersionItem[] Versions { get; }

    public HistoryWorkspace(string rootName, IReadOnlyList<SavedVersion> snapshot, CancellationToken token = default)
    {
        var ordered = snapshot.OrderByDescending(item => item.Number).ToArray();
        Versions = ordered.Select((saved, index) =>
        {
            token.ThrowIfCancellationRequested();
            var changes = HistoryComparison.Compare(index + 1 < ordered.Length ? ordered[index + 1].Version.Entries : [],
                saved.Version.Entries);
            return new VersionItem(saved, changes, Summarize(changes));
        }).ToArray();

        var paths = new Dictionary<string, (bool Directory, bool Historical)>(HistoryPaths.Comparer);
        for (var index = 0; index < ordered.Length; index++)
        {
            foreach (var entry in ordered[index].Version.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (!paths.TryAdd(entry.Path, (entry.IsDirectory, index > 0)) && entry.IsDirectory)
                    paths[entry.Path] = (true, paths[entry.Path].Historical);
            }
        }
        var parents = paths.Keys.Select(Parent).ToHashSet(HistoryPaths.Comparer);
        _children = paths.Select(pair => new TreeItem(pair.Key, pair.Key[(pair.Key.LastIndexOf('/') + 1)..],
                1 + pair.Key.Count(c => c == '/'), pair.Value.Directory, parents.Contains(pair.Key),
                pair.Value.Historical ? TreePresence.Historical : TreePresence.Recorded))
            .GroupBy(item => Parent(item.Path), HistoryPaths.Comparer)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.IsDirectory)
                .ThenBy(item => item.Name, HistoryPaths.Comparer).ToArray(), HistoryPaths.Comparer);
        _root = new("", rootName, 0, true, _children.ContainsKey(""), TreePresence.Recorded);
    }

    public IReadOnlyList<TreeItem> VisibleTree(IReadOnlySet<string> expanded, WorkingDirectoryListing? disk = null,
        CancellationToken token = default)
    {
        var visible = new List<TreeItem>();
        var pending = new Stack<TreeItem>();
        pending.Push(_root with
        {
            HasChildren = _root.HasChildren || disk?.Children.GetValueOrDefault("") is { Length: > 0 },
            Presence = disk?.Unreadable.Contains("") == true ? TreePresence.Unavailable : TreePresence.Recorded
        });
        while (pending.TryPop(out var item))
        {
            token.ThrowIfCancellationRequested();
            if (item.HasChildren && expanded.Contains(item.Path))
            {
                item = item with { IsExpanded = true };
                var children = _children.GetValueOrDefault(item.Path) ?? [];
                if (disk is not null) children = MergeChildren(item, children, disk, token);
                for (var index = children.Length - 1; index >= 0; index--) pending.Push(children[index]);
            }
            visible.Add(item);
        }
        return visible;
    }

    private TreeItem[] MergeChildren(TreeItem parent, TreeItem[] history, WorkingDirectoryListing disk, CancellationToken token)
    {
        var rows = new Dictionary<string, TreeItem>(HistoryPaths.Comparer);
        var listed = disk.Children.TryGetValue(parent.Path, out var entries);
        foreach (var row in history)
        {
            token.ThrowIfCancellationRequested();
            var presence = HistoryExclusions.IsExcluded(row.Path, disk.Exclusions) ? TreePresence.Excluded :
                parent.Presence == TreePresence.Unavailable || disk.Unreadable.Contains(parent.Path) ? TreePresence.Unavailable :
                listed || parent.IsHistoricalOnly || !parent.IsDirectory ? TreePresence.Historical : TreePresence.Recorded;
            rows.Add(row.Path, row with { Presence = presence });
        }
        foreach (var entry in entries ?? [])
        {
            token.ThrowIfCancellationRequested();
            var path = entry.Path;
            var recorded = Versions.FirstOrDefault()?.Entries.GetValueOrDefault(path);
            var presence = HistoryExclusions.IsExcluded(path, disk.Exclusions) ? TreePresence.Excluded :
                entry.Unavailable || disk.Unreadable.Contains(path) ? TreePresence.Unavailable :
                recorded?.IsDirectory == entry.IsDirectory ? TreePresence.Recorded : TreePresence.Untracked;
            var canExpand = _children.ContainsKey(path) || entry.IsDirectory &&
                presence is not (TreePresence.Excluded or TreePresence.Unavailable) &&
                (!disk.Children.TryGetValue(path, out var children) || children.Length > 0);
            rows[path] = new(path, path[(path.LastIndexOf('/') + 1)..], parent.Depth + 1, entry.IsDirectory, canExpand, presence);
        }
        return rows.Values.OrderByDescending(item => item.IsDirectory).ThenBy(item => item.Name, HistoryPaths.Comparer).ToArray();
    }

    public IReadOnlyList<VersionItem> Timeline(IReadOnlySet<string> selected, CancellationToken token = default)
    {
        if (selected.Count == 0) return [];
        if (selected.Contains("")) return Versions;
        var result = new List<VersionItem>();
        foreach (var version in Versions)
        {
            token.ThrowIfCancellationRequested();
            var changes = version.Changes.Where(change =>
            {
                token.ThrowIfCancellationRequested();
                return HistorySelection.Contains(selected, change.Path);
            }).ToArray();
            if (changes.Length > 0)
                result.Add(version with { Summary = changes.Length == 1 ? changes[0].Kind : Summarize(changes) });
        }
        return result;
    }

    internal static string Summarize(IReadOnlyList<HistoryChange> changes) => changes.Count == 0 ? "没有变化"
        : string.Join(" · ", changes.GroupBy(change => change.Kind).Select(group => $"{group.Key} {group.Count()} 项"));

    private static string Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }
}
