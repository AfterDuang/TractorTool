using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using TractorTool.Plugins.FolderHistory.Storage;
using TractorTool.Plugins.FolderHistory.ViewModels;
using TractorTool.Plugins.FolderHistory.Views;

internal static partial class InteractionChecks
{
    private static void CheckLargePreview(ServiceChecks.TestHost host, FolderHistoryViewModel model, FolderHistoryView view,
        string root, Action<string> savePreview)
    {
        const string name = "项目记录.md";
        var lines = Enumerable.Range(1, 9500).Select(i => $"记录 {i:D5}  确认当前操作流程和文件清单").ToArray();
        var initial = Encoding.UTF8.GetBytes(string.Join('\n', lines));
        lines[499] += "（补充说明）";
        lines[8999] += "（已完成）";
        var updated = Encoding.UTF8.GetBytes(string.Join('\n', lines));
        var file = Path.Combine(root, name);
        File.WriteAllBytes(file, updated);
        var repository = Directory.CreateDirectory(Path.Combine(HistoryPaths.HistoryDirectory(root), "repository")).FullName;
        var state = HistoryStore.Read(root);
        foreach (var (id, note, bytes) in new[] { (Earlier, "空目录", Array.Empty<byte>()), (Later, "添加文档", initial), (NewSnapshot, "更新两条记录", updated) })
        {
            var entries = new List<object> { new { type = "Folder", path = root + Path.DirectorySeparatorChar } };
            if (bytes.Length > 0) entries.Add(new { type = "File", path = file, size = bytes.Length, hash = Convert.ToBase64String(SHA256.HashData(bytes)) });
            EngineChecks.WriteVolume(Path.Combine(repository, id), true, "SHA256", entries.ToArray());
            state.AddVersion(id, root, note, false);
        }
        HistoryStore.Save(root, state);
        host.ProcessHandler = (request, _) =>
        {
            Require(request.Arguments[0] == "restore", "a preview attempted to write a native snapshot");
            var staging = request.Arguments.Single(a => a.StartsWith("--restore-path=", StringComparison.Ordinal))[15..];
            File.WriteAllBytes(Path.Combine(staging, name), request.Arguments.Contains("--version=0") ? updated : initial);
            return Task.FromResult(host.ProcessResult);
        };
        var refresh = model.RefreshAsync();
        Until(() => refresh.IsCompleted && model.Versions.Count == 3 && model.VisibleTreeItems.Any(item => item.Path == name),
            "large document fixture did not load");
        var tree = view.FindControl<ListBox>("FileTree")!;
        Click((Control)tree.ContainerFromItem(model.VisibleTreeItems.Single(item => item.Path == name))!);
        Until(() => model.SelectedVersion?.Id == NewSnapshot && model.IsSingleFile, "large document history was not selected");
        Click((Control)view.FindControl<Avalonia.Controls.Primitives.TabStrip>("ModeTabs")!.ContainerFromIndex(1)!);
        Until(() => model.TextLines.Any(line => line.AfterLine == 9000) && model.TextLines.Any(line => line.IsGap),
            "large file did not present separated edits in context");
        Require(model.TextLines.Any(line => line.AfterLine == 500 && line.Kind == "修改") && model.TextLines.Count < 40,
            "unchanged lines hid the actual edits");
        savePreview("large-text-changes");
        model.SelectedVersion = model.TimelineVersions.Single(v => v.Id == Later);
        Until(() => model.HasTextPages && model.CanNextTextPage, "large addition was not paged");
        var page = model.TextPageSummary;
        var scroll = view.FindControl<ScrollViewer>("DiffScroll")!;
        scroll.ScrollToEnd();
        Until(() => scroll.Offset.Y > 0, "large addition could not scroll");
        var next = view.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "下一页") && IsPresented(b));
        Click(next);
        Until(() => model.TextPageSummary != page && model.CanPreviousTextPage && scroll.Offset.Y == 0,
            "next page did not start at the first new line");
    }

    private static void CheckRevisionActions(ServiceChecks.TestHost host, FolderHistoryViewModel model, FolderHistoryView view,
        Window window, string root, string repository, byte[] original, byte[] revised, Action<string> savePreview)
    {
        const string published = "duplicati-20261007T150000Z.dlist.zip";
        var path = Path.Combine(root, "sample.txt");
        var other = Path.Combine(root, "独立草稿.txt");
        var tree = view.FindControl<ListBox>("FileTree")!;
        var list = view.FindControl<ListBox>("HistoryList")!;
        TreeItem Leaf() => model.VisibleTreeItems.Single(item => item.Path == "sample.txt");
        VersionItem? Current() => model.TimelineVersions.SingleOrDefault(row => row.IsCurrentFile);
        ListBoxItem Row(string id) => (ListBoxItem)list.ContainerFromItem(model.TimelineVersions.Single(row => row.Id == id))!;
        void OpenAction(string id, string action)
        {
            var row = Row(id);
            Click(row, MouseButton.Right);
            Click(MenuItem(row.ContextFlyout, action));
        }
        FAContentDialog Dialog(string title)
        {
            FAContentDialog? dialog = null;
            Until(() => (dialog = window.GetVisualDescendants().OfType<FAContentDialog>()
                .FirstOrDefault(d => Equals(d.Title, title) && IsPresented(d))) is not null, "dialog missing: " + title);
            return dialog!;
        }

        Click((Control)tree.ContainerFromItem(Leaf())!);
        // #3 recorded the same bytes for this file. Its latest actual revision is #2.
        Until(() => Current()?.Id == Later, "file history did not identify the current revision");
        File.WriteAllText(other, "保留我的草稿");
        var stamp = File.GetLastWriteTimeUtc(path);
        var draft = revised.ToArray();
        draft[Array.IndexOf(draft, (byte)'1')] = (byte)'9';
        File.WriteAllBytes(path, draft);
        File.SetLastWriteTimeUtc(path, stamp);
        Until(() => Leaf().Presence == TreePresence.Modified && Current() is null, "same-length local edit did not clear the current marker");

        host.ProcessHandler = (request, _) =>
        {
            var arguments = request.Arguments;
            if (arguments[0] == "restore")
            {
                var to = arguments.Single(a => a.StartsWith("--restore-path=", StringComparison.Ordinal))[15..];
                File.WriteAllBytes(Path.Combine(to, "sample.txt"), arguments.Contains("--version=2") ? original : revised);
            }
            else
            {
                Require(arguments[0] == "backup", "unexpected native operation during historical publish");
                var source = arguments[2];
                var bytes = File.ReadAllBytes(Path.Combine(source, "sample.txt"));
                Require(bytes.SequenceEqual(original) && !File.Exists(Path.Combine(source, Path.GetFileName(other))),
                    "publish took bytes from the working folder");
                EngineChecks.WriteVolume(Path.Combine(repository, published), true, "SHA256",
                    new { type = "Folder", path = source + Path.DirectorySeparatorChar },
                    new { type = "File", path = Path.Combine(source, "sample.txt"), size = bytes.Length,
                        hash = Convert.ToBase64String(SHA256.HashData(bytes)) });
            }
            return Task.FromResult(host.ProcessResult);
        };

        OpenAction(Earlier, "恢复到此版本…");
        Click(DialogButton(Dialog("恢复到原位置"), "恢复工作文件"));
        Until(() => !model.IsBusy && Leaf().Presence == TreePresence.OlderRevision && Current()?.Id == Earlier,
            "restored file did not report the old revision: " + model.Error);
        Require(File.ReadAllBytes(path).SequenceEqual(original) && HistoryStore.Read(root).Versions.Count == 3,
            "restoring a revision created a snapshot or wrote incorrect bytes");
        Click(Row(Later));
        Require(model.SelectedVersion?.Id == Later && Current()?.Id == Earlier, "browsing a different row moved the working-copy marker");
        window.RequestedThemeVariant = ThemeVariant.Dark;
        Pump();
        savePreview("history-working-revision");

        OpenAction(Earlier, "将此版本提交为最新…");
        var publish = Dialog("将此版本提交为最新");
        Click(NoteInput(publish));
        window.KeyTextInput("采用交付前的版本");
        Click(DialogButton(publish, "提交为最新"));
        Until(() => !model.IsBusy && Current()?.Id == published, "published snapshot was not identified as current: " + model.Error);
        Require(HistoryStore.Read(root).Versions is { Count: 4 } receipts && receipts[^1].Note == "采用交付前的版本" &&
            File.ReadAllText(other) == "保留我的草稿", "publishing changed the draft or recorded an unexpected snapshot");
    }
}
