using TractorTool.Plugins.FolderHistory.Models;
using TractorTool.Plugins.FolderHistory.Storage;
using TractorTool.Plugins.FolderHistory.ViewModels;

internal static class WorkspaceChecks
{
    public static void Run(string fixture)
    {
        var folder = new HistoryEntry("doc", true, 0, "");
        var draft = new HistoryEntry("doc/draft", false, 5, "old");
        var other = new HistoryEntry("docs", false, 10, "a");
        var before = Version(1, folder, draft, other);
        var update = Version(2, folder, draft, other with { Hash = "b" });
        var after = Version(3, folder, other with { Hash = "b" });
        var workspace = new HistoryWorkspace("测试", [after, before, update]);
        CheckWorkingDirectory(fixture, workspace);
        var tree = workspace.VisibleTree(new HashSet<string> { "", "doc" });
        Assert(tree.Select(row => row.Path).SequenceEqual(["", "doc", "doc/draft", "docs"]), "wrong tree order or hierarchy");
        Assert(tree.Single(row => row.Path == draft.Path) is { IsHistoricalOnly: true, Depth: 2 }, "deleted path missing from history");
        var refreshedTree = tree.Select(row => row with { }).ToDictionary(row => row.Path, StringComparer.Ordinal);
        var ctrlSelection = TreeItem.ResolveSelection([tree[1], refreshedTree["docs"], tree[1], tree[1] with { Path = "gone" }], refreshedTree);
        Assert(ctrlSelection.Length == 2 && ReferenceEquals(ctrlSelection[0], refreshedTree["doc"]) &&
            ReferenceEquals(ctrlSelection[1], refreshedTree["docs"]), "Ctrl selection lost an item retained by the view across refresh");
        Assert(workspace.VisibleTree(new HashSet<string> { "" }).All(row => row.Path != draft.Path), "collapsed branch leaked");
        Assert(Ids("").SequenceEqual(["3", "2", "1"]), "root timeline lost a revision");
        Assert(Ids().Length == 0, "empty selection was interpreted as root");
        Assert(Ids("doc").SequenceEqual(["3", "1"]), "directory matched a similar file name");
        Assert(Ids(draft.Path).SequenceEqual(["3", "1"]), "deleted file not in its deletion revision");
        var filtered = workspace.Timeline(new HashSet<string> { draft.Path });
        Assert(filtered.Select(row => row.NumberLabel).SequenceEqual(["#3", "#1"]) && filtered[0].Title.Contains(filtered[0].SavedAt),
            "filtering renumbered a snapshot or removed its exact save time");
        Assert(Ids("doc", "docs", draft.Path).SequenceEqual(["3", "2", "1"]), "overlapping paths duplicated or lost revisions");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try
            {
                workspace.Timeline(new HashSet<string> { "doc" }, cancelled.Token);
                throw new InvalidOperationException("superseded timeline scan ignored cancellation");
            }
            catch (OperationCanceledException error) when (error.CancellationToken == cancelled.Token) { }
        }

        var selection = new List<string> { "doc", draft.Path };
        var target = VersionActionContext.Capture("root", workspace.Versions[0], selection, "已选 2 项")!;
        selection.Clear();
        Assert(target.RelativePaths.SequenceEqual(["doc", draft.Path]) && target.UnavailableReason?.Contains(draft.Path) == true,
            "menu did not freeze explicit missing child");
        Assert(VersionActionContext.Capture("root", workspace.Versions[0], [], "") is null, "empty selection created an action");
        Assert(VersionActionContext.Capture("root", workspace.Versions[2], [draft.Path], draft.Path)?.SingleFilePath == draft.Path,
            "older file could not be saved as a single file");
        var asFile = Version(4, new HistoryEntry("doc", false, 0, "empty"));
        var typeChange = new HistoryWorkspace("测试", [before, asFile]);
        Assert(VersionActionContext.Capture("root", typeChange.Versions[0], ["doc"], "")?.SingleFilePath == "doc" &&
            VersionActionContext.Capture("root", typeChange.Versions[1], ["doc"], "")?.SingleFilePath is null,
            "target type must come from the clicked version");
        var renamed = new HistoryWorkspace("测试", [Version(1, other), Version(2, other with { Path = "Docs" })]).Versions;
        var renameRows = renamed[0].Changes.Select(change => FileItem.ForChange(change, renamed[1], renamed[0])).ToArray();
        Assert(renameRows.Single(file => file.Kind == "新增") is { Before: null, After.Path: "Docs" } &&
            renameRows.Single(file => file.Kind == "删除") is { Before.Path: "docs", After: null },
            "case-only rename lost the missing side");
        if (OperatingSystem.IsWindows())
            Assert(FileItem.ForFile("Docs", renamed[1], renamed[0]) is
                { Before.Path: "docs", After.Path: "Docs", Kind: "名称大小写变化" }, "single-file scope lost its old spelling");

        string[] Ids(params string[] paths) => workspace.Timeline(paths.ToHashSet(StringComparer.Ordinal))
            .Select(row => row.Id).ToArray();
    }

    private static void CheckWorkingDirectory(string fixture, HistoryWorkspace workspace)
    {
        var root = Directory.CreateDirectory(Path.Combine(fixture, "tree-browse")).FullName;
        var repositoryId = HistoryStore.Create(root).Id;
        Directory.CreateDirectory(Path.Combine(root, "doc"));
        Directory.CreateDirectory(Path.Combine(root, "cache"));
        File.WriteAllText(Path.Combine(root, "cache", "ignored"), "excluded");
        File.WriteAllText(Path.Combine(root, "doc", "new.txt"), "local");
        // A listing must still work when another application denies all reads of the file contents.
        using var locked = File.Open(Path.Combine(root, "new.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var expanded = new HashSet<string>(HistoryPaths.Comparer) { "", "cache" };
        expanded.Add("doc/previously-expanded");
        var observed = new List<string>();
        var collapsed = WorkingDirectoryListing.Read(root, repositoryId, expanded, ["cache"], CancellationToken.None, observed.Add);
        Assert(collapsed.Children.Count == 1 && observed.SequenceEqual([""]), "listing watched a collapsed or excluded directory");
        expanded.Add("doc");
        var listing = WorkingDirectoryListing.Read(root, repositoryId, expanded, ["cache"], CancellationToken.None);
        var tree = workspace.VisibleTree(expanded, listing).ToDictionary(item => item.Path);
        Assert(tree["new.bin"].IsUntracked && tree["doc/new.txt"].IsUntracked && tree["doc"].Presence == TreePresence.Recorded,
            "new items or tracked directory status incorrect");
        Assert(tree["docs"].IsHistoricalOnly && tree["doc/draft"].IsHistoricalOnly, "disk deletion lost historical paths");
        Assert(tree["cache"] is { Presence: TreePresence.Excluded, HasChildren: false } && !tree.ContainsKey("cache/ignored")
            && !tree.ContainsKey(HistoryPaths.DirectoryName), "excluded directory or repository was browsed");
        listing.Children.Remove("doc");
        listing.Unreadable.Add("doc");
        Assert(workspace.VisibleTree(expanded, listing).Single(item => item.Path == "doc/draft").Presence == TreePresence.Unavailable,
            "failed enumeration was displayed as deletion");
    }

    internal static void CheckLiveRepositoryMove(string root, FolderHistoryViewModel model, Action<Func<bool>, string> until)
    {
        var moved = root + "-moved";
        Directory.Move(root, moved);
        until(() => model.HasLiveTreeWarning && model.VisibleTreeItems.All(item => !item.IsUntracked),
            "moving the selected library left its live tree active");
        Directory.CreateDirectory(root);
        HistoryStore.Create(root);
        File.WriteAllText(Path.Combine(root, "foreign.txt"), "belongs to a different history");
        until(() => model.LiveTreeWarning.Contains("与已关联的文件夹不同", StringComparison.Ordinal),
            "replacing the selected directory did not recheck repository identity");
        Assert(model.VisibleTreeItems.All(item => item.Path != "foreign.txt"), "files from the replacement repository appeared in the old tree");
        Directory.Move(root, root + "-replacement");
        Directory.Move(moved, root);
        File.WriteAllText(Path.Combine(root, "returned.txt"), "belongs to the original history");
        until(() => !model.HasLiveTreeWarning && model.VisibleTreeItems.Any(item => item.Path == "returned.txt" && item.IsUntracked),
            "restoring the original directory did not reattach its watcher");
        File.WriteAllText(Path.Combine(root, "after-return.txt"), "a later event on the restored directory");
        until(() => model.VisibleTreeItems.Any(item => item.Path == "after-return.txt" && item.IsUntracked),
            "the watcher still followed the replacement directory after the original one returned");
    }

    private static SavedVersion Version(int id, params HistoryEntry[] entries) =>
        new(new(id.ToString(), DateTimeOffset.UnixEpoch.AddSeconds(id), "root", entries), "", false, id);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
