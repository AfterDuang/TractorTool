using System.Security.Cryptography;
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
        CheckCurrentFileStates(fixture);
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

    private static void CheckCurrentFileStates(string fixture)
    {
        var root = Directory.CreateDirectory(Path.Combine(fixture, "tree-content")).FullName;
        var repositoryId = HistoryStore.Create(root).Id;
        var path = Path.Combine(root, "sample.bin");
        byte[] original = [1, 2, 3, 4];
        byte[] latest = [4, 3, 2, 1];
        byte[] unsaved = [4, 2, 3, 1];
        var oldFile = Entry(original);
        var newFile = Entry(latest);
        var other = new HistoryEntry("other", false, 1, "first");
        var workspace = new HistoryWorkspace("内容", [Version(1, oldFile, other),
            Version(2, oldFile, other with { Hash = "second" }), Version(3, newFile, other),
            Version(4, newFile, other with { Hash = "fourth" })]);
        var expanded = new HashSet<string>(HistoryPaths.Comparer) { "" };
        var cache = new WorkingFileCache();
        File.WriteAllBytes(path, latest);
        var initial = Read();
        var initialTree = Tree(initial);
        Assert(initialTree is { Presence: TreePresence.Recorded, CurrentVersionId: "3", CurrentVersionNumber: 3 },
            "unchanged snapshots replaced the file's actual revision");
        var second = Read();
        Assert(ReferenceEquals(initial.Children[""][0].Content, second.Children[""][0].Content),
            "an unchanged visible file was hashed again");
        var rows = workspace.Timeline(new HashSet<string> { "sample.bin" });
        HistoryWorkspace.MarkCurrentFile(rows, [initialTree]);
        Assert(rows.Single(row => row.IsCurrentFile).Id == "3", "latest content marker selected an unrelated snapshot");
        var notified = 0;
        rows[0].PropertyChanged += (_, e) => { if (e.PropertyName == nameof(VersionItem.IsCurrentFile)) notified++; };

        var unchangedTimestamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllBytes(path, original);
        File.SetLastWriteTimeUtc(path, unchangedTimestamp);
        cache.Invalidate("sample.bin");
        var older = Tree(Read());
        Assert(older is { Presence: TreePresence.OlderRevision, CurrentVersionId: "1", StatusText: "旧版 #1" },
            "same-length same-time content change was not matched to its historical file revision");
        HistoryWorkspace.MarkCurrentFile(rows, [older]);
        Assert(rows.Single(row => row.IsCurrentFile).Id == "1" && notified == 1,
            "current marker was not notified on the existing row object");

        File.WriteAllBytes(path, unsaved);
        File.SetLastWriteTimeUtc(path, unchangedTimestamp);
        cache.Invalidate();
        var modified = Tree(Read());
        Assert(modified is { Presence: TreePresence.Modified, CurrentVersionId: null, StatusText: "已修改 · 未快照" },
            "a watcher gap retained a stale cached match or labeled new content as a saved revision");
        HistoryWorkspace.MarkCurrentFile(rows, [modified]);
        Assert(rows.All(row => !row.IsCurrentFile), "unsaved content kept a historical current marker");

        File.WriteAllBytes(path, original);
        cache.Invalidate();
        var returning = new HistoryWorkspace("内容", workspace.Versions.Select(row => row.Saved)
            .Append(Version(5, oldFile, other)).Append(Version(6, oldFile, other with { Hash = "sixth" })).ToArray());
        var repeated = returning.VisibleTree(expanded, Read()).Single(item => item.Path == "sample.bin");
        Assert(repeated is { Presence: TreePresence.Recorded, CurrentVersionId: "5" },
            "matching repeated content did not choose its most recent actual revision");

        if (OperatingSystem.IsWindows())
        {
            using var locked = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.None);
            var blocked = Read();
            Assert(Tree(blocked) is { Presence: TreePresence.Unavailable, CurrentVersionId: null } && blocked.Warning.Length != 0,
                "a cached match hid an unreadable working file");
        }
        var excluded = WorkingDirectoryListing.Read(root, repositoryId, expanded, ["sample.bin"], CancellationToken.None,
            trackedFiles: workspace.TrackedFiles, cache: cache);
        Assert(Tree(excluded) is { Presence: TreePresence.Excluded, CurrentVersionId: null }, "excluded content was marked as current");
        HistoryWorkspace.MarkCurrentFile(rows, [older, modified with { Path = "other" }]);
        Assert(rows.All(row => !row.IsCurrentFile), "multiple files shared a misleading current-file marker");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            WorkingDirectoryListing.Read(root, repositoryId, expanded, [], cancelled.Token,
                trackedFiles: workspace.TrackedFiles, cache: cache);
            throw new InvalidOperationException("content scan ignored cancellation");
        }
        catch (OperationCanceledException error) when (error.CancellationToken == cancelled.Token) { }

        HistoryEntry Entry(byte[] bytes) => new("sample.bin", false, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
        WorkingDirectoryListing Read() => WorkingDirectoryListing.Read(root, repositoryId, expanded, [], CancellationToken.None,
            trackedFiles: workspace.TrackedFiles, cache: cache);
        TreeItem Tree(WorkingDirectoryListing listing) => workspace.VisibleTree(expanded, listing).Single(item => item.Path == "sample.bin");
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
