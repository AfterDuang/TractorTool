using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using FluentAvalonia.UI.Controls;
using TractorTool.Plugins.FolderHistory.Models;
using TractorTool.Plugins.FolderHistory.Storage;
using TractorTool.Plugins.FolderHistory.ViewModels;
using TractorTool.UI;

namespace TractorTool.Plugins.FolderHistory.Views;

public partial class FolderHistoryView : UserControl, IPluginPageLifetime
{
    private bool _interacting;
    private FAContentDialog? _dialog;
    private FAMenuFlyout? _menu;

    public FolderHistoryView()
    {
        InitializeComponent();
        DiffLines.PropertyChanged += (_, change) =>
        {
            if (change.Property == ItemsControl.ItemsSourceProperty) DiffScroll.Offset = default;
        };
        ComparisonPane.SizeChanged += (_, e) => ComparisonPane.Classes.Set("unified", e.NewSize.Width < 560);
        FileTree.SizeChanged += (_, e) => FileTree.Classes.Set("compact", e.NewSize.Width < 200);
        InitializeTreeInput();
        Loaded += async (_, _) =>
        {
            if (DataContext is not FolderHistoryViewModel model) return;
            BindTree(model);
            await model.InitializeAsync();
        };
        Unloaded += (_, _) => Disconnect();
    }

    private void BackToChanges(object? sender, RoutedEventArgs e)
    {
        if (DataContext is FolderHistoryViewModel model) model.SelectedFile = null;
    }

    private async void CreateSnapshot(object? sender, RoutedEventArgs e) => await InteractAsync(async model =>
    {
        if (model.SelectedFolder is not { } folder || !model.CanSave) return;
        var note = await AskSnapshotNoteAsync("创建快照", folder.Root, "", "创建");
        if (note is null || !Current(model) || model.SelectedFolder?.Info.Id != folder.Info.Id ||
            !HistoryPaths.Comparer.Equals(model.SelectedFolder.Root, folder.Root)) return;
        await model.CreateSnapshotAsync(folder.Root, note);
    });

    private Task EditSnapshotNoteAsync(VersionActionContext target, string originalNote) => InteractAsync(async model =>
    {
        var note = await AskSnapshotNoteAsync("编辑快照说明", target.VersionTitle, originalNote, "保存");
        if (note is not null && note != originalNote && Current(model) && model.SelectedFolder?.Root == target.Root)
            await model.SetSnapshotNoteAsync(target.Root, target.VersionId, originalNote, note);
    });

    private async Task<string?> AskSnapshotNoteAsync(string title, string context, string initialNote, string accept)
    {
        var input = new TextBox { Text = initialNote, MaxLength = 500, MinWidth = 220, PlaceholderText = "例如：交付前的最终版" };
        AutomationProperties.SetName(input, "快照说明");
        var result = await ShowAsync(title, Panel(PathText(context), Field("说明（可选）", input)), accept,
            defaultButton: FAContentDialogButton.Primary, initialFocus: input);
        return result == FAContentDialogResult.Primary ? input.Text?.Trim() ?? "" : null;
    }

    private void MenuOpening(object? sender, EventArgs args)
    {
        // PopupFlyoutBase exposes EventHandler and supplies CancelEventArgs when opening.
        var e = (CancelEventArgs)args;
        if (sender is not FAMenuFlyout menu || !IsLoaded ||
            DataContext is not FolderHistoryViewModel { IsBusy: false, SelectedFolder: { } folder } model)
        {
            e.Cancel = true;
            return;
        }
        bool SameFolder() => model.SelectedFolder is { } current &&
            current.Info.Id == folder.Info.Id && HistoryPaths.Comparer.Equals(current.Root, folder.Root);
        FAMenuFlyoutItem Action(string text, EventHandler<RoutedEventArgs> click, bool enabled = true)
        {
            var item = new HistoryMenuItem(menu) { Text = text, IsEnabled = enabled };
            item.Click += (s, args) =>
            {
                if (Current(model) && !model.IsBusy && SameFolder()) click(s, args);
            };
            return item;
        }
        if (ReferenceEquals(menu.Target, MoreButton))
        {
            menu.ItemsSource = new Control[] {
                Action(model.ProtectionAction, ToggleProtection, model.CanChangeProtection),
                Action("文件夹设置…", EditSettings), Action("管理历史…", ManageHistory),
                new FAMenuFlyoutSeparator(), Action("刷新", (_, _) => model.RefreshCommand.Execute(null))
            };
        }
        else if (menu.Target?.DataContext is VersionItem row && model.CaptureVersionAction(row) is { } target)
        {
            model.SelectedVersion = row;
            var note = row.Note;
            menu.ItemsSource = new Control[] {
                Action("恢复到此版本…", async (_, _) => await RestoreAsync(target), model.CanSave),
                Action("将此版本提交为最新…", async (_, _) => await PublishAsync(target),
                    model.CanSave && target.UnavailableReason is null && target.VersionId != model.Versions.FirstOrDefault()?.Id),
                Action("另存一份…", async (_, _) => await ExportAsync(target)),
                new FAMenuFlyoutSeparator(),
                Action(note.Length == 0 ? "添加说明…" : "编辑说明…", async (_, _) => await EditSnapshotNoteAsync(target, note)),
                Action(target.Pinned ? "取消重要标记" : "标为重要版本", async (_, _) => await InteractAsync(m => m.TogglePinnedAsync(target)))
            };
        }
        else
        {
            e.Cancel = true;
            return;
        }
        if (!ReferenceEquals(_menu, menu)) _menu?.Hide();
        _menu = menu;
        model.PropertyChanged += FolderChanged;
        menu.Closed += Closed;

        void FolderChanged(object? _, PropertyChangedEventArgs change)
        {
            if (change.PropertyName == nameof(model.SelectedFolder) && !SameFolder()) menu.Hide();
        }
        void Closed(object? _, EventArgs eventArgs)
        {
            model.PropertyChanged -= FolderChanged;
            menu.Closed -= Closed;
            menu.ItemsSource = Array.Empty<Control>();
            if (ReferenceEquals(_menu, menu)) _menu = null;
        }
    }

    private async void AddFolder(object? sender, RoutedEventArgs e) => await InteractAsync(async model =>
    {
        var root = await PickFolderAsync("选择要保护的本地文件夹");
        if (root is null || !Current(model)) return;
        var exclusions = new TextBox { AcceptsReturn = true, MinHeight = 84, MaxHeight = 160, MaxLength = 16000,
            PlaceholderText = "每行一个相对路径，例如 cache 或 build/output", TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(exclusions, "首次保护排除的相对路径，每行一项");
        var scanError = Text("");
        scanError.IsVisible = false;
        var preparationContent = Panel(PathText(root),
            Text("先核对文件数量和空间，再决定是否开启自动保存。默认纳入普通文件和目录，历史专用子目录始终排除。"),
            new Expander { Header = "调整首次保护的排除规则（可选）", HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                Content = Panel(exclusions, Text("使用相对路径，不使用通配符；文件夹包含全部子项。此处仅设置新库，已有历史沿用自己的规则。", true)) }, scanError);
        FolderPreparation? preparation;
        FolderSettings initialSettings;
        do
        {
            if (await ShowAsync("准备历史保护", preparationContent, "扫描并查看概览") != FAContentDialogResult.Primary || !Current(model)) return;
            initialSettings = new() { ExcludedPaths = (exclusions.Text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) };
            preparation = await model.InspectFolderAsync(root, initialSettings);
            if (!Current(model)) return;
            if (preparation is null)
            {
                scanError.Text = model.Error.Length > 0 ? model.Error + "\n可调整排除规则后重试，或取消。" : "扫描已取消，可重试或取消。";
                scanError.IsVisible = true;
            }
        } while (preparation is null);
        var enable = new CheckBox
        {
            Content = "完整保存后开启自动保护（软件运行时记录变化）",
            IsChecked = !preparation.ExistingHistory
        };
        var content = Panel(
            PathText(preparation.Root),
            Text(preparation.WorkingTreeScanned ? $"预计纳入 {preparation.FileCount:N0} 个文件、{preparation.DirectoryCount:N0} 个文件夹，共 {FolderHistoryViewModel.FormatSize(preparation.TotalBytes)}。"
                : "已识别现有历史库；尚未核对当前工作文件。关联后可浏览历史，开启保护时再完整核对当前内容。"),
            Text("历史位置"), PathText(preparation.HistoryDirectory),
            Text($"磁盘可用空间：{(preparation.AvailableBytes is { } available ? FolderHistoryViewModel.FormatSize(available) : "无法读取")}。实际历史和临时空间占用取决于文件内容。", true),
            Text(preparation.ExistingHistory ? "历史库原有排除规则保持不变，首次准备中填写的规则不会覆盖已有设置。"
                : initialSettings.ExcludedPaths.Length == 0 ? "除历史专用子目录外，没有额外排除项。"
                : "排除项：" + string.Join("、", initialSettings.ExcludedPaths), true),
            Text(preparation.ExistingHistory ? "发现已有历史，将核对身份后关联。默认保持手动保存，请明确选择是否开启自动保护。"
                : "首次保存成功后才会显示已保护。请先停止持续写入，所需工具通过宿主确认后准备。"),
            enable,
            Text("新功能首次写入会升级历史库格式，旧版插件将无法读取。历史和当前文件在同一磁盘，删除整目录或磁盘损坏时会一起丢失。", true));
        if (await ShowAsync("准备文件夹历史", content, "确认并添加") == FAContentDialogResult.Primary && Current(model))
            await model.AddFolderAsync(preparation.Root, enable.IsChecked == true, initialSettings);
    });

    private async void ToggleProtection(object? sender, RoutedEventArgs e) => await InteractAsync(async model =>
    {
        if (model.SelectedFolder is not { } folder) return;
        var enabled = !folder.Info.ProtectionEnabled || folder.Info.NeedsAttention;
        var message = enabled
            ? "将核对目录身份和当前文件，完整保存成功后开启自动保护。软件退出期间不会记录每次编辑；重新启动后会核对最终状态。首次写入会升级库格式，旧版插件将无法读取。"
            : "暂停后仍可手动创建快照、查看历史或找回文件。";
        if (await ConfirmAsync(enabled ? "开启自动保存" : "暂停自动保存", message,
            enabled ? "检查并开启" : "确认暂停") && Current(model))
            await model.SetProtectionAsync(folder.Root, enabled);
    });

    private async void EditSettings(object? sender, RoutedEventArgs e) => await InteractAsync(async model =>
    {
        if (model.SelectedFolder is not { } folder) return;
        var settings = await model.GetSettingsAsync(folder.Root);
        if (settings is null || !Current(model)) return;
        var exclusions = new TextBox
        {
            Text = string.Join(Environment.NewLine, settings.ExcludedPaths), AcceptsReturn = true,
            MinHeight = 88, MaxHeight = 160, MaxLength = 16000,
            PlaceholderText = "每行一个相对路径，例如：cache 或 build/output",
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetName(exclusions, "此文件夹排除的相对路径，每行一项");
        var keepLatest = Number(settings.KeepLatestVersions, 0, 100000, "至少保留最近多少个版本");
        var keepDays = Number(settings.KeepDays, 0, 36500, "至少保留最近多少天的版本");
        var stable = Number(settings.StableSeconds, 1, 3600, "连续稳定多少秒后保存");
        var interval = Number(settings.MinimumIntervalSeconds, 1, 86400, "两次自动保存最少间隔秒数");
        var content = Panel(
            Text(folder.Name),
            Text("排除哪些内容"), exclusions,
            Text("填写从当前文件夹出发的路径，不使用通配符。排除文件夹时包含其全部子项；不删除当前文件或过去的历史。", true),
            Text("自动保留规则"),
            Field("至少保留最近的版本数（0 = 不使用此项）", keepLatest),
            Field("至少保留最近的天数（0 = 不使用此项）", keepDays),
            Text("两项都为 0 时关闭自动清理、保留全部。设置两项时，只要符合其中一项就保留；重要版本、恢复前版本和最新版本始终保留。开启后在自动保存成功时清理，也可在“管理历史”预览后手动清理。", true),
            new Expander
            {
                Header = "自动保存节奏", HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                Content = Panel(Field("文件停止变化后等待（秒）", stable), Field("两次自动保存至少间隔（秒）", interval),
                    Text("持续编辑时会等待，不承诺记录每一次写入。后台无需重复确认安装工具。", true))
            });
        if (await ShowAsync("文件夹设置", content, "保存规则", defaultButton: FAContentDialogButton.Primary) != FAContentDialogResult.Primary || !Current(model)) return;
        if (keepLatest.Value is null || keepDays.Value is null || stable.Value is null || interval.Value is null)
            throw new ArgumentException("数字设置不能为空，请重新填写。");
        if ((keepLatest.Value > 0 || keepDays.Value > 0) &&
            (!await ConfirmAsync("确认自动清理旧版本", $"以后自动保存成功后，将永久删除不满足保留规则的普通旧版本。\n\n保留最近 {keepLatest.Value:0} 个版本，保留最近 {keepDays.Value:0} 天；0 表示不使用该项规则，有效规则满足任一项即保留。重要版本、恢复前版本和最新版本始终保留。\n\n{folder.Root}", "确认规则并允许自动清理") || !Current(model))) return;
        await model.SaveSettingsAsync(folder.Root, settings with
        {
            ExcludedPaths = (exclusions.Text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            KeepLatestVersions = decimal.ToInt32(keepLatest.Value.Value), KeepDays = decimal.ToInt32(keepDays.Value.Value),
            StableSeconds = decimal.ToInt32(stable.Value.Value), MinimumIntervalSeconds = decimal.ToInt32(interval.Value.Value)
        });
    });

    private Task ExportAsync(VersionActionContext target) => InteractAsync(async model =>
    {
        if (target.UnavailableReason is { } reason) { model.SetNotice(reason); return; }
        var destination = target.SingleFilePath is { } file
            ? await PickFileAsync(target.VersionTitle, Path.GetFileName(file))
            : await PickFolderAsync($"选择另存位置 · {target.ScopeTitle} · {target.VersionTitle}");
        if (destination is not null && Current(model)) await model.ExportAsync(target, destination);
    });
    private Task RestoreAsync(VersionActionContext target) => InteractAsync(async model =>
    {
        if (target.UnavailableReason is { } reason) { model.SetNotice(reason); return; }
        var preview = await model.PreviewRestoreAsync(target);
        if (preview is null || !Current(model)) return;
        if (preview.Changes.Count == 0) { model.SetNotice("选定范围已经与目标版本相同，无需恢复。"); return; }
        var body = Panel(
            Text(target.VersionTitle + " · " + target.ScopeTitle), PathText(target.Root),
            Text(RestoreSummary(preview.Changes)),
            Items(preview.Changes.Select(change => $"{(change.Kind == "修改" ? "覆盖" : change.Kind)}   {change.Path}")),
            Text("将替换上面列出的工作文件并暂停自动保存。此操作不会创建快照，中断后可继续或撤销。", true));
        if (await ShowAsync("恢复到原位置", body, "恢复工作文件") == FAContentDialogResult.Primary && Current(model))
            await model.RestoreAsync(preview);
    });

    private Task PublishAsync(VersionActionContext target) => InteractAsync(async model =>
    {
        var folder = model.SelectedFolder;
        if (folder is null || !HistoryPaths.Comparer.Equals(folder.Root, target.Root)) return;
        var note = await AskSnapshotNoteAsync("将此版本提交为最新",
            $"{target.ScopeTitle} · {target.VersionTitle}\n新快照采用这份历史中的所选内容，其余文件沿用最新快照。磁盘上的文件保持原样，提交后自动保存会暂停。",
            "", "提交为最新");
        if (note is not null && Current(model) && model.SelectedFolder?.Info.Id == folder.Info.Id &&
            HistoryPaths.Comparer.Equals(model.SelectedFolder.Root, target.Root))
            await model.PublishSelectionAsync(target, note);
    });

    private async void HandlePendingRestore(object? sender, RoutedEventArgs e) => await InteractAsync(async model =>
    {
        if (model.SelectedFolder is not { } folder) return;
        var pending = await model.GetPendingRestoreAsync(folder.Root);
        if (pending is null || !Current(model)) return;
        var target = model.Versions.FirstOrDefault(version => version.Id == pending.TargetVersionId)?.Title ?? "记录中的目标版本";
        var protection = model.Versions.FirstOrDefault(version => version.Id == pending.ProtectionVersionId)?.Title;
        var preparing = pending.Status == "Preparing";
        var reverting = pending.Status == "Reverting";
        var rollback = preparing ? "恢复准备尚未完成，未开始覆盖工作文件。撤销只结束这次准备，保留当前文件及之后的修改。"
            : "撤销会回到本次操作前的内容，并核对恢复之后的新修改；发现冲突时停止，不会直接覆盖。";
        var choice = await ShowAsync("处理未完成恢复", Panel(
            PathText(folder.Root), Text("目标：" + target),
            Text(protection is null ? "恢复前的文件保存在此次操作的撤销记录中。" : "恢复前保护快照：" + protection),
            Text(preparing ? rollback : reverting ? "本次恢复的撤销尚未完成，请继续撤销。" : "继续恢复会核对已完成步骤后继续原计划。" + rollback),
            Text("处理期间自动保存和历史清理继续暂停。", true)),
            preparing || reverting ? "" : "继续本次恢复", reverting ? "继续撤销" : "撤销本次恢复");
        if (!Current(model)) return;
        if (choice == FAContentDialogResult.Primary)
        {
            if (await ConfirmAsync("继续本次恢复", "继续此前确认的目标版本和范围。将保留恢复前材料并校验现有文件；发现新的冲突会停止。", "确认继续") && Current(model))
                await model.ResumeRestoreAsync(folder.Root);
        }
        else if (choice == FAContentDialogResult.Secondary &&
            await ConfirmAsync("撤销本次恢复", rollback + "处理完成后自动保存保持暂停。", "确认撤销") && Current(model))
            await model.AbandonRestoreAsync(folder.Root);
    });

    private async void ManageHistory(object? sender, RoutedEventArgs e) => await InteractAsync(async model =>
    {
        if (model.SelectedFolder is not { } folder) return;
        var action = Items(["按保留策略清理旧版本", "删除全部版本（不可撤销）", "移除列表关联（保留文件和历史）"]);
        action.SelectedIndex = 0;
        if (await ShowAsync("管理历史", Panel(PathText(folder.Root),
            Text($"{folder.Info.VersionCount} 个版本 · 历史占用 {FolderHistoryViewModel.FormatSize(folder.Info.HistoryBytes)}"), action,
            Text("清理只删除历史版本，工作文件保留。恢复未完成或存在活动操作时不可清理；重要版本须先取消重要标记才能删除。", true)), "继续") != FAContentDialogResult.Primary || !Current(model)) return;
        if (action.SelectedIndex == 2)
        {
            if (await ConfirmAsync("移除列表关联", $"停止此目录的自动保护并从列表移除：\n{folder.Root}\n\n当前文件和历史仍留在原目录，重新添加即可再次查看。", "确认移除关联") && Current(model))
                await model.RemoveFolderAsync(folder.Root);
            return;
        }
        var all = action.SelectedIndex == 1;
        var versions = await model.GetCleanupPreviewAsync(folder.Root, all);
        if (versions is null || !Current(model)) return;
        if (versions.Count == 0)
        { model.SetNotice(all ? "没有可删除的版本。" : "没有符合当前清理策略的版本。两项保留设置都为 0 时保留全部；重要版本也会保留。"); return; }
        if (await ShowAsync(all ? "确认删除全部版本" : "确认清理旧版本", Panel(
            PathText(folder.Root), Text($"将永久删除以下 {versions.Count} 个版本，无法撤销。"),
            Items(versions.Select(version => version.Title + (version.Note.Length > 0 ? "  " + version.Note : ""))),
            Text("当前工作文件、历史库身份和规则保留。各版本共享存储内容，释放空间不能直接按版本文件大小相加。", true),
            Text(all ? "删除后将无法从这些版本找回文件。自动保存保持暂停，需要时请明确重新开启。" : "未列出的版本和重要版本继续保留。")),
            all ? "永久删除全部版本" : "永久删除列出的版本") == FAContentDialogResult.Primary && Current(model))
            await model.DeleteVersionsAsync(folder.Root, versions.Select(version => version.Id).ToArray(), all);
    });

    private async void ResumeCleanup(object? sender, RoutedEventArgs e) => await InteractAsync(async model =>
    {
        if (model.SelectedFolder is not { } folder) return;
        if (await ConfirmAsync("继续未完成清理", $"继续处理此前已确认删除的版本，不扩大范围。工作文件保持原位。\n\n{folder.Root}", "继续清理") && Current(model))
            await model.ResumeCleanupAsync(folder.Root);
    });

    public void OnNavigatedFrom()
    {
        Disconnect();
        DataContext = null;
    }
    private void Disconnect()
    {
        _menu?.Hide();
        _dialog?.Hide();
        UnbindTree();
        if (DataContext is FolderHistoryViewModel model) model.Detach();
    }
    private bool Current(FolderHistoryViewModel model) => IsLoaded && ReferenceEquals(DataContext, model);
    private async Task InteractAsync(Func<FolderHistoryViewModel, Task> action)
    {
        if (_interacting || DataContext is not FolderHistoryViewModel { IsBusy: false } model) return;
        _interacting = true;
        model.DeferRefresh(true);
        try { await action(model); }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (Current(model)) model.ReportError("操作未完成", error); }
        finally { _interacting = false; model.DeferRefresh(false); }
    }
    private async Task<string?> PickFolderAsync(string title)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null || !storage.CanPickFolder) throw new NotSupportedException("当前窗口无法打开文件夹选择器。");
        var folders = await storage.OpenFolderPickerAsync(new() { Title = title, AllowMultiple = false });
        try { return folders.Count == 0 ? null : folders[0].TryGetLocalPath() ?? throw new NotSupportedException("请选择本地文件夹。"); }
        finally { foreach (var folder in folders) folder.Dispose(); }
    }
    private async Task<string?> PickFileAsync(string version, string name)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage?.CanSave != true) throw new NotSupportedException("当前窗口无法打开文件选择器。");
        using var file = await storage.SaveFilePickerAsync(new()
        {
            Title = "另存为新文件 · " + version, SuggestedFileName = name, ShowOverwritePrompt = false
        });
        return file is null ? null : file.TryGetLocalPath() ?? throw new NotSupportedException("请选择本地文件。");
    }
    private async Task<bool> ConfirmAsync(string title, string message, string accept) =>
        await ShowAsync(title, Text(message), accept) == FAContentDialogResult.Primary;
    private async Task<FAContentDialogResult> ShowAsync(string title, Control content, string accept, string? alternative = null,
        FAContentDialogButton defaultButton = FAContentDialogButton.None, Control? initialFocus = null)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner || !IsLoaded) return FAContentDialogResult.None;
        var scroll = new ScrollViewer { MaxHeight = Math.Clamp(owner.Bounds.Height - 260, 160, 500),
            MaxWidth = Math.Clamp(owner.Bounds.Width - 96, 200, 620), Content = content };
        var dialog = new FAContentDialog
        {
            Title = title,
            Content = scroll,
            PrimaryButtonText = accept, SecondaryButtonText = alternative ?? "", CloseButtonText = "取消",
            DefaultButton = defaultButton
        };
        if (initialFocus is not null) dialog.Opened += (_, _) => initialFocus.Focus();
        _dialog = dialog;
        try { return await dialog.ShowAsync(owner); }
        finally { scroll.Content = null; if (ReferenceEquals(_dialog, dialog)) _dialog = null; }
    }
    private static StackPanel Panel(params Control[] controls)
    {
        var panel = new StackPanel { Spacing = 10 };
        foreach (var control in controls) panel.Children.Add(control);
        return panel;
    }
    private static TextBlock Text(string text, bool caption = false)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        if (caption) block.Classes.Add("caption");
        return block;
    }
    private static SelectableTextBlock PathText(string path) => new() { Text = path, TextWrapping = TextWrapping.Wrap };
    private static StackPanel Field(string label, Control control) => Panel(Text(label), control);
    private static NumericUpDown Number(int value, int minimum, int maximum, string name)
    {
        var control = new NumericUpDown { Value = value, Minimum = minimum, Maximum = maximum, Increment = 1, FormatString = "0" };
        AutomationProperties.SetName(control, name);
        return control;
    }
    private static ListBox Items(IEnumerable<string> items) => new()
    {
        ItemsSource = items.ToArray(), MaxHeight = 240,
        ItemTemplate = new FuncDataTemplate<string>((item, _) => Text(item ?? ""))
    };
    private static string RestoreSummary(IReadOnlyList<HistoryChange> changes) =>
        $"新增 {changes.Count(change => change.Kind == "新增")} 项 · 覆盖 {changes.Count(change => change.Kind == "修改")} 项 · 删除 {changes.Count(change => change.Kind == "删除")} 项";
}
