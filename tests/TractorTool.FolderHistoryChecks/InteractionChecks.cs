using System.Diagnostics;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.Styling;
using FluentAvalonia.UI.Controls;
using TractorTool.Plugins.FolderHistory.Services;
using TractorTool.Plugins.FolderHistory.Storage;
using TractorTool.Plugins.FolderHistory.ViewModels;
using TractorTool.Plugins.FolderHistory.Views;

internal static class InteractionChecks
{
    private const string Earlier = "duplicati-20261007T120000Z.dlist.zip";
    private const string Later = "duplicati-20261007T130000Z.dlist.zip";
    private const string NewSnapshot = "duplicati-20261007T140000Z.dlist.zip";

    public static void Run(string fixture)
    {
        var host = new ServiceChecks.TestHost(fixture);
        using var service = new FolderHistoryService(new("checks", host, host, host, CancellationToken.None)
            { Packages = host, Processes = host, Tasks = host });
        var root = Directory.CreateDirectory(Path.Combine(fixture, "work")).FullName;
        File.WriteAllBytes(Path.Combine(root, "sample.txt"), [66]);
        service.AddFolderAsync(root).GetAwaiter().GetResult();
        var repository = Directory.CreateDirectory(Path.Combine(HistoryPaths.HistoryDirectory(root), "repository")).FullName;
        var state = HistoryStore.Read(root);
        foreach (var (id, note, bytes) in new[] { (Earlier, "初版", new byte[] { 65 }), (Later, "", new byte[] { 66 }) })
        {
            EngineChecks.WriteVolume(Path.Combine(repository, id), true, "SHA256",
                new { type = "Folder", path = root + Path.DirectorySeparatorChar },
                new { type = "File", path = Path.Combine(root, "sample.txt"), size = bytes.Length, hash = Convert.ToBase64String(SHA256.HashData(bytes)) });
            state.Versions.Add(new(id, root, note, false));
        }
        HistoryStore.Save(root, state);

        AppBuilder.Configure<Application>().UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        var app = Application.Current!;
        app.Styles.Add(new FluentAvaloniaTheme { PreferSystemTheme = false, PreferUserAccentColor = false });
        var ui = new Uri("avares://TractorTool.UI/");
        app.Resources.MergedDictionaries.Add(new ResourceInclude(ui) { Source = new Uri(ui, "Styles/ThemeResources.axaml") });
        app.Styles.Add(new StyleInclude(ui) { Source = new Uri(ui, "Styles/SharedStyles.axaml") });
        var model = new FolderHistoryViewModel(service, CancellationToken.None);
        var view = new FolderHistoryView { DataContext = model };
        var window = new Window { Width = 1000, Height = 680, Content = view };
        try
        {
            window.Show();
            Until(() => model.CanSave && model.TimelineVersions.Count == 2, "fixture did not load two versions");
            var more = view.FindControl<Button>("MoreButton")!;
            Click(more);
            MenuItem(more.Flyout, "文件夹设置…");
            // Exercise the same entry point a screen reader uses.
            MenuAction(MenuItem(more.Flyout, "刷新")).Invoke();
            Until(() => more.Flyout?.IsOpen == false && model.CanSave, "folder menu stayed open");

            var tree = view.FindControl<ListBox>("FileTree")!;
            var file = tree.Items.OfType<TreeItem>().Single(entry => entry.Path == "sample.txt");
            Click((Control)tree.ContainerFromItem(file)!);
            Until(() => model.IsSingleFile && model.TimelineVersions.Count == 2, "file click did not load its history");
            var history = view.FindControl<ListBox>("HistoryList")!;
            var comparison = view.FindControl<Grid>("ComparisonPane")!;
            VerifyHistory();
            Require(model.SelectedVersion?.Id == Later, "fixture did not start at the latest version");
            var earlier = Row(Earlier);
            Click(earlier, MouseButton.Right);
            var edit = MenuItem(earlier.ContextFlyout, "编辑说明…");
            Require(model.SelectedVersion?.Id == Earlier, "right-click acted on the previously selected version");
            Click(edit);
            var editor = NoteDialog("编辑快照说明");
            var input = NoteInput(editor);
            Require(input.Text == "初版", "right-click opened another version's note");
            Click(input);
            input.SelectAll();
            window.KeyTextInput("交付前");
            Click(DialogButton(editor, "保存"));
            Until(() => model.TimelineVersions.Any(v => v.Id == Earlier && v.Note == "交付前"), "edited note was not saved to its row");
            Require(HistoryStore.Read(root).Versions.Single(v => v.Id == Later).Note == "", "editing a note changed the other version");

            earlier = Row(Earlier);
            Click(earlier);
            Require(earlier.IsKeyboardFocusWithin, "clicking the history row did not focus it for keyboard actions");
            // Apps is the default Avalonia context-menu key used by the headless platform.
            window.KeyPressQwerty(PhysicalKey.ContextMenu, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.ContextMenu, RawInputModifiers.None);
            var keyboardItem = MenuItem(earlier.ContextFlyout, "编辑说明…");
            Require(model.SelectedVersion?.Id == Earlier, "keyboard context menu lost the selected version");
            var popup = TopLevel.GetTopLevel(keyboardItem)!;
            popup.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            popup.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Until(() => earlier.ContextFlyout?.IsOpen == false, "Escape did not close the history menu");

            var ellipsis = Row(Later).GetVisualDescendants().OfType<Button>().Single();
            Click(ellipsis);
            var add = MenuItem(ellipsis.Flyout, "添加说明…");
            Require(model.SelectedVersion?.Id == Later, "ellipsis targeted the previously selected version");
            Click(add);
            var addNote = NoteDialog("编辑快照说明");
            Require(string.IsNullOrEmpty(NoteInput(addNote).Text), "ellipsis opened another version's note");
            CancelDialog(addNote);

            var create = view.FindControl<Button>("CreateSnapshotButton")!;
            Click(create);
            var snapshot = NoteDialog("创建快照");
            var description = NoteInput(snapshot);
            window.KeyTextInput("用于验收");
            Require(description.Text == "用于验收" && DialogButton(snapshot, "创建").IsEnabled, "snapshot dialog cannot accept a note");
            CancelDialog(snapshot);
            Require(host.PackageRequests == 0 && host.Processes == 0, "browsing history or cancelling creation started the engine");

            var tabs = view.FindControl<TabStrip>("ModeTabs")!;
            Click((Control)tabs.ContainerFromIndex(1)!);
            Until(() => comparison.IsEffectivelyVisible && !history.IsEffectivelyVisible && model.SelectedFile?.Path == file.Path,
                "comparison tab did not show the selected file");
            Click((Control)tabs.ContainerFromIndex(0)!);
            VerifyHistory();

            Click((Control)tree.ContainerFromItem(tree.Items.OfType<TreeItem>().Single(entry => entry.Path == ""))!);
            Until(() => model.SelectedTreeItems is [{ Path.Length: 0 }] && model.CanSave, "folder selection was lost");
            // Simulate the one new native file list produced by an unchanged backup, without spawning a process.
            File.WriteAllText(Path.Combine(HistoryPaths.HistoryDirectory(root), "index.sqlite"), "fixture index");
            host.AllowPackage = true;
            host.ProcessHandler = (request, _) =>
            {
                Require(request.Arguments[0] == "backup", $"unexpected native command: {request.Arguments[0]}");
                File.Copy(Path.Combine(repository, Later), Path.Combine(repository, NewSnapshot));
                return Task.FromResult(host.ProcessResult);
            };
            Click(create);
            snapshot = NoteDialog("创建快照");
            Click(NoteInput(snapshot));
            window.KeyTextInput("交付版本");
            Click(DialogButton(snapshot, "创建"));
            Until(() => !model.IsBusy && model.Versions.Any(v => v.Id == NewSnapshot && v.Note == "交付版本"), "named snapshot was not created");
            var notes = HistoryStore.Read(root).Versions.ToDictionary(version => version.Id, version => version.Note);
            Require(notes.Count == 3 && notes[NewSnapshot] == "交付版本" && notes[Later] == "" && notes[Earlier] == "交付前",
                "creating a named snapshot with unchanged files altered previous receipts");

            ListBoxItem Row(string id) => (ListBoxItem)history.ContainerFromItem(model.TimelineVersions.Single(row => row.Id == id))!;
            void VerifyHistory() => Require(history.IsEffectivelyVisible && !comparison.IsEffectivelyVisible &&
                model.SelectedFile is null && model.Files.Count == 0 && model.TextLines.Count == 0, "history tab loaded or displayed comparison details");
            FAContentDialog NoteDialog(string title)
            {
                FAContentDialog? dialog = null;
                Until(() => (dialog = window.GetVisualDescendants().OfType<FAContentDialog>()
                    .FirstOrDefault(d => Equals(d.Title, title) && IsPresented(d))) is not null, $"'{title}' dialog did not open");
                return dialog!;
            }
            void CancelDialog(FAContentDialog dialog)
            {
                Click(DialogButton(dialog, "取消"));
                Until(() => !IsPresented(dialog), "cancel did not close the dialog");
            }
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    private static TextBox NoteInput(FAContentDialog dialog) => dialog.GetVisualDescendants().OfType<TextBox>()
        .Single(control => AutomationProperties.GetName(control) == "快照说明" && IsPresented(control));

    private static Button DialogButton(FAContentDialog dialog, string text) => dialog.GetVisualDescendants().OfType<Button>()
        .Single(button => Equals(button.Content, text) && IsPresented(button));

    private static FAMenuFlyoutItem MenuItem(FlyoutBase? flyout, string text)
    {
        FAMenuFlyoutItem? item = null;
        Until(() => flyout is FAMenuFlyout { IsOpen: true } menu &&
            (item = menu.ItemsSource?.OfType<FAMenuFlyoutItem>().FirstOrDefault(i => i.Text == text)) is not null && IsPresented(item) &&
            item.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text && IsPresented(t)),
            $"'{text}' was not rendered in the open menu");
        _ = MenuAction(item!);
        return item!;
    }

    private static IInvokeProvider MenuAction(FAMenuFlyoutItem item)
    {
        var peer = ControlAutomationPeer.CreatePeerForElement(item);
        Require(peer?.GetAutomationControlType() == AutomationControlType.MenuItem && peer.GetName() == item.Text,
            $"'{item.Text}' is missing its accessible name or menu item role");
        return peer!.GetProvider<IInvokeProvider>() ?? throw new InvalidOperationException($"'{item.Text}' has no Invoke action");
    }

    private static bool IsPresented(Control control) => control.IsEffectivelyVisible && control.Bounds is { Width: > 0, Height: > 0 } &&
        TopLevel.GetTopLevel(control) is not null;

    private static void Click(Control target, MouseButton button = MouseButton.Left)
    {
        Require(IsPresented(target), $"cannot click {target.GetType().Name} before layout");
        var top = TopLevel.GetTopLevel(target)!;
        var center = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), top)!.Value;
        top.MouseMove(center);
        top.MouseDown(center, button);
        top.MouseUp(center, button);
        Pump();
    }

    private static void Until(Func<bool> condition, string failure)
    {
        var elapsed = Stopwatch.StartNew();
        do
        {
            Pump();
            if (condition()) return;
            Thread.Sleep(5);
        } while (elapsed.Elapsed < TimeSpan.FromSeconds(5));
        throw new InvalidOperationException(failure);
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
