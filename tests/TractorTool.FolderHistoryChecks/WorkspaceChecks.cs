using TractorTool.Plugins.FolderHistory.Models;
using TractorTool.Plugins.FolderHistory.ViewModels;

internal static class WorkspaceChecks
{
    public static void Run()
    {
        var folder = new HistoryEntry("doc", true, 0, "");
        var draft = new HistoryEntry("doc/draft", false, 5, "old");
        var other = new HistoryEntry("docs", false, 10, "a");
        var before = Version(1, folder, draft, other);
        var update = Version(2, folder, draft, other with { Hash = "b" });
        var after = Version(3, folder, other with { Hash = "b" });
        var workspace = new HistoryWorkspace("测试", [after, before, update]);
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

    private static SavedVersion Version(int id, params HistoryEntry[] entries) =>
        new(new(id.ToString(), DateTimeOffset.UnixEpoch.AddSeconds(id), "root", entries), "", false);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
