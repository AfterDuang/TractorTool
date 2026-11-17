using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TractorTool.Plugins.FolderHistory.Models;
using TractorTool.Plugins.FolderHistory.Processing;
using TractorTool.Plugins.FolderHistory.Services;

namespace TractorTool.Plugins.FolderHistory.ViewModels;

internal sealed partial class FolderHistoryViewModel(FolderHistoryService service, CancellationToken stopping) : ObservableObject
{
    private bool _attached;
    private bool _reloadRequested;
    private bool _settingSelection;
    private int _interactionDepth;
    private IReadOnlyList<HistoryChange> _comparison = [];
    public ObservableCollection<FolderItem> Folders { get; } = [];
    public ObservableCollection<VersionItem> Versions { get; } = [];
    public ObservableCollection<VersionItem> TimelineVersions { get; } = [];
    public ObservableCollection<FileItem> Files { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasFolder), nameof(FolderSummary), nameof(CanSave), nameof(CanRestoreFile), nameof(CanChangeProtection), nameof(ProtectionAction), nameof(HasPendingRestore), nameof(HasPendingCleanup))]
    private FolderItem? _selectedFolder;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasVersion), nameof(PinAction), nameof(CanCompare))]
    private VersionItem? _selectedVersion;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanCompare), nameof(ComparisonDirection))]
    private VersionItem? _comparisonBase;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelectedFile), nameof(CanExportFile), nameof(CanRestoreFile), nameof(CanPreviewText), nameof(SelectedFileSummary))]
    private FileItem? _selectedFile;
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private string _fileSearch = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasFileHistory))] private string _fileHistoryPath = "";
    [ObservableProperty] private bool _showAllFiles;
    [ObservableProperty] private string _busyMessage = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSave), nameof(CanRestoreFile))] private bool _versionsLoaded;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasNotice))] private string _notice = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string _error = "";
    [ObservableProperty] private string _comparisonSummary = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasTextPreview))] private string _textSummary = "";
    [ObservableProperty] private IReadOnlyList<TextDiffLine> _textLines = [];

    public bool HasFolder => SelectedFolder is not null;
    public bool CanSave => HasFolder && VersionsLoaded && !HasPendingRestore && !HasPendingCleanup;
    public bool HasVersion => SelectedVersion is not null;
    public bool HasSelectedFile => SelectedFile is not null;
    public bool CanExportFile => SelectedFile?.ExistsInVersion == true;
    public bool CanRestoreFile => CanExportFile && CanSave;
    public bool CanChangeProtection => HasFolder && !HasPendingRestore && !HasPendingCleanup;
    public bool CanPreviewText => SelectedFile is { IsDirectory: false };
    public bool HasFileHistory => FileHistoryPath.Length != 0;
    public bool HasFiles => Files.Count != 0;
    public bool HasNotice => Notice.Length != 0;
    public bool HasError => Error.Length != 0;
    public bool HasTextPreview => TextSummary.Length != 0;
    public bool HasPendingRestore => SelectedFolder?.Info.HasPendingRestore == true;
    public bool HasPendingCleanup => SelectedFolder?.Info.HasPendingCleanup == true;
    public bool CanChooseComparison => Versions.Count > 1;
    public bool CanCompare => ComparisonBase is not null && SelectedVersion is not null && ComparisonBase.Id != SelectedVersion.Id;
    public string PinAction => SelectedVersion?.Pinned == true ? "取消重要标记" : "保留这个版本";
    public string ProtectionAction => SelectedFolder?.Info.NeedsAttention == true ? "重新开启自动保存…"
        : SelectedFolder?.Info.ProtectionEnabled == true ? "暂停自动保存" : "开启自动保存…";
    public string FolderSummary => SelectedFolder is { } folder
        ? $"{folder.Info.Status} · {folder.Info.VersionCount} 个版本 · 历史占用 {FormatSize(folder.Info.HistoryBytes)}\n上次完整保存：{folder.LastSaved}" : "";
    public string ComparisonDirection => SelectedVersion is not { } selected ? ""
        : ComparisonBase is { } before ? $"{before.Title} → {selected.Title}" : $"第一份历史 · {selected.Title}";
    public string SelectedFileSummary => SelectedFile is not { } file ? ""
        : file.IsDirectory ? "文件夹 · 操作包含其中的内容" : $"{file.SizeLabel} · 另存和恢复均保留原始内容";

    public async Task InitializeAsync()
    {
        if (!_attached) { _attached = true; service.Changed += ServiceChanged; }
        await RefreshAsync();
    }
    public void Detach() { _attached = false; service.Changed -= ServiceChanged; }
    public void DeferRefresh(bool defer)
    {
        _interactionDepth += defer ? 1 : -1;
        Debug.Assert(_interactionDepth >= 0);
        if (_interactionDepth == 0 && _attached && _reloadRequested && !IsBusy) _ = RefreshPendingAsync();
    }
    private void ServiceChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (!_attached || stopping.IsCancellationRequested) return;
        _reloadRequested = true;
        if (!IsBusy && _interactionDepth == 0) _ = RefreshPendingAsync();
    });
    private Task RefreshPendingAsync()
    {
        _reloadRequested = false;
        return RunAsync("正在更新历史状态…", "无法更新历史状态", () => ReloadAsync(), clearMessages: false);
    }

    partial void OnSelectedFolderChanged(FolderItem? oldValue, FolderItem? newValue)
    {
        if (_settingSelection || StringComparer.OrdinalIgnoreCase.Equals(oldValue?.Root, newValue?.Root)) return;
        ResetFolderDetails();
        if (newValue is { } folder) _ = RunAsync("正在读取版本…", "无法读取版本", () => LoadVersionsAsync(folder.Root));
    }
    partial void OnSelectedVersionChanged(VersionItem? value)
    {
        if (_settingSelection) return;
        var index = value is null ? -1 : Versions.IndexOf(value);
        _settingSelection = true;
        ComparisonBase = index >= 0 && index + 1 < Versions.Count ? Versions[index + 1] : null;
        _settingSelection = false;
        ApplyComparison(value?.Changes ?? []);
        OnPropertyChanged(nameof(ComparisonDirection));
    }
    partial void OnComparisonBaseChanged(VersionItem? value) { if (!_settingSelection) _ = CompareAsync(); }
    partial void OnSelectedFileChanged(FileItem? value) => ClearTextPreview();
    partial void OnFileSearchChanged(string value) => RebuildFiles();
    partial void OnShowAllFilesChanged(bool value) => RebuildFiles();

    [RelayCommand]
    public Task RefreshAsync() => RunAsync("正在读取文件夹和版本…", "无法读取历史", () => ReloadAsync());
    public Task<FolderPreparation?> InspectFolderAsync(string root, FolderSettings? initialSettings = null) => ReadAsync("正在扫描文件夹…", "无法准备文件夹", () => service.InspectFolderAsync(root, initialSettings));
    public Task AddFolderAsync(string root, bool enableProtection, FolderSettings? initialSettings = null) => RunAsync("正在准备文件夹历史…", "无法开启文件夹历史", async () =>
    {
        await service.AddFolderAsync(root, initialSettings);
        await RefreshAfterChangeAsync(root);
        if (enableProtection)
        {
            await service.SetProtectionEnabledAsync(root, true);
            Notice = "已完整保存并开启自动保护。只有软件运行时才会记录后续更改。";
        }
        else Notice = "已关联此文件夹。自动保存保持关闭，可随时明确开启。";
        await RefreshAfterChangeAsync(root, selectLatest: true);
    });
    public Task SetProtectionAsync(string root, bool enabled) => ChangeAsync(
        enabled ? "正在检查并开启自动保存…" : "正在暂停自动保存…", "无法更新自动保存", root,
        () => service.SetProtectionEnabledAsync(root, enabled), enabled ? "已完整核对并开启自动保存。" : "已暂停自动保存，当前文件和全部历史仍保留。");
    public Task<FolderSettings?> GetSettingsAsync(string root) => ReadAsync("正在读取文件夹设置…", "无法读取设置", () => service.GetSettingsAsync(root));
    public Task SaveSettingsAsync(string root, FolderSettings settings) => ChangeAsync("正在保存文件夹设置…", "无法保存设置", root,
        () => service.SaveSettingsAsync(root, settings), "已保存此文件夹的规则。排除仅影响后续保存，已有历史仍保留。");

    [RelayCommand]
    private Task SaveAsync()
    {
        if (!CanSave || SelectedFolder is not { } folder) return Task.CompletedTask;
        var note = Note.Trim();
        var latestId = Versions.FirstOrDefault()?.Id;
        return RunAsync("正在保存版本…", "无法保存版本", async () =>
        {
            await service.SaveAsync(folder.Root, note);
            Notice = "保存操作已完成。";
            if (await RefreshAfterChangeAsync(folder.Root, selectLatest: true))
            {
                if (Versions.FirstOrDefault()?.Id == latestId)
                    Notice = note.Length == 0 ? "内容没有变化，未新增版本。" : "内容没有变化，未新增版本；说明未写入历史。";
                else { Note = ""; Notice = "已保存并校验新版本。"; }
            }
        });
    }
    [RelayCommand]
    private Task TogglePinnedAsync()
    {
        if (SelectedFolder is not { } folder || SelectedVersion is not { } version) return Task.CompletedTask;
        return ChangeAsync("正在更新重要标记…", "无法更新重要标记", folder.Root,
            () => service.SetPinnedAsync(folder.Root, version.Id, !version.Pinned),
            version.Pinned ? "已取消重要标记。" : "已标记为重要版本，历史清理会保留它。");
    }
    private Task CompareAsync()
    {
        if (_settingSelection || SelectedVersion is not { } after) return Task.CompletedTask;
        var before = ComparisonBase;
        return RunAsync("正在比较版本…", "无法比较版本", async () =>
        {
            var changes = await Task.Run(() => HistoryComparison.Compare(before?.Saved.Version.Entries ?? [], after.Saved.Version.Entries), stopping);
            stopping.ThrowIfCancellationRequested();
            ApplyComparison(changes);
        });
    }
    [RelayCommand]
    private void ShowFileHistory()
    {
        if (SelectedFile is not { } file) return;
        FileHistoryPath = file.Path;
        ShowAllFiles = false;
        RebuildTimeline();
        RebuildFiles();
    }
    [RelayCommand]
    private void ClearFileHistory() { FileHistoryPath = ""; RebuildTimeline(); RebuildFiles(); }
    [RelayCommand]
    private Task PreviewTextAsync()
    {
        if (SelectedFolder is not { } folder || SelectedVersion is not { } selected || SelectedFile is not { IsDirectory: false } file)
            return Task.CompletedTask;
        var before = ComparisonBase;
        return RunAsync("正在提取只读文本预览…", "无法预览文本", async () =>
        {
            ClearTextPreview();
            var oldEntry = before?.Saved.Version.Entries.FirstOrDefault(entry => entry.Path == file.Path && !entry.IsDirectory);
            var newEntry = selected.Saved.Version.Entries.FirstOrDefault(entry => entry.Path == file.Path && !entry.IsDirectory);
            if (oldEntry?.Length > TextComparison.MaxBytes || newEntry?.Length > TextComparison.MaxBytes)
            { TextSummary = "文件超过 256 KiB，未加载文本。可另存原始文件查看。"; return; }
            var oldBytes = oldEntry is null ? [] : await service.ReadFileAsync(folder.Root, before!.Id, file.Path, TextComparison.MaxBytes);
            var newBytes = newEntry is null ? [] : await service.ReadFileAsync(folder.Root, selected.Id, file.Path, TextComparison.MaxBytes);
            var result = await Task.Run(() => TextComparison.Compare(oldBytes, newBytes), stopping);
            stopping.ThrowIfCancellationRequested();
            TextLines = result.Lines;
            TextSummary = result.Summary;
        });
    }
    public Task ExportAsync(string root, string versionId, string parentDirectory, string? path) =>
        RunAsync("正在另存历史内容…", "无法另存历史内容", async () => { Notice = "已另存到：" + await service.ExportAsync(root, versionId, parentDirectory, path); });
    public Task<RestorePreview?> PreviewRestoreAsync(string root, string versionId, string? path) =>
        ReadAsync("正在核对恢复范围…", "无法准备恢复", () => service.PreviewRestoreAsync(root, versionId, path));
    public Task RestoreAsync(RestorePreview preview) => ChangeAsync("正在保护当前内容并恢复…", "恢复未完成", preview.Root,
        () => service.RestoreInPlaceAsync(preview), "已校验并恢复到原位置；恢复前版本和较新历史仍保留。");
    public Task<PendingRestoreInfo?> GetPendingRestoreAsync(string root) => ReadAsync("正在检查未完成恢复…", "无法读取恢复记录", () => service.GetPendingRestoreAsync(root));
    public Task ResumeRestoreAsync(string root) => ChangeAsync("正在继续恢复并校验…", "恢复仍未完成", root,
        () => service.ResumeRestoreAsync(root), "已完成恢复并校验。自动保存状态请查看目录概览。");
    public Task AbandonRestoreAsync(string root) => ChangeAsync("正在撤销本次恢复…", "无法撤销恢复", root,
        () => service.AbandonRestoreAsync(root), "已撤销本次恢复；当前文件处理结果见任务记录，自动保存仍暂停。");
    public Task<IReadOnlyList<SavedVersion>?> GetCleanupPreviewAsync(string root, bool all) => ReadAsync(
        "正在计算历史清理范围…", "无法准备清理", () => service.GetCleanupPreviewAsync(root, all));
    public Task DeleteVersionsAsync(string root, IReadOnlyList<string> ids, bool deleteAll = false) => ChangeAsync("正在清理选定历史…", "历史清理未完成", root,
        () => service.DeleteVersionsAsync(root, ids, deleteAll), "选定历史已清理，当前工作文件未改动。");
    public Task ResumeCleanupAsync(string root) => ChangeAsync("正在继续未完成清理…", "清理仍未完成", root,
        () => service.ResumeCleanupAsync(root), "已完成此前确认的历史清理，当前工作文件未改动。");
    public Task RemoveFolderAsync(string root) => ChangeAsync("正在移除关联…", "无法移除关联", null,
        () => service.RemoveFolderAsync(root), "已移除关联。当前文件和历史仍在原目录，重新添加可以继续查看。");
    public void ReportPickerError(Exception error) => ReportError("无法选择文件夹", error);
    public void ReportError(string operation, Exception error) { Notice = ""; Error = $"{operation}：{error.GetBaseException().Message}"; }
    public void SetNotice(string message) { Error = ""; Notice = message; }

    private Task ChangeAsync(string progress, string failure, string? root, Func<Task> action, string success) => RunAsync(progress, failure, async () =>
    {
        try { await action(); Notice = success; }
        finally { await RefreshAfterChangeAsync(root); }
    });
    private async Task<T?> ReadAsync<T>(string progress, string failure, Func<Task<T>> read) where T : class?
    {
        T? value = null;
        await RunAsync(progress, failure, async () => { value = await read(); });
        return value;
    }
    private async Task RunAsync(string progress, string failure, Func<Task> action, bool clearMessages = true)
    {
        if (IsBusy || stopping.IsCancellationRequested) return;
        BusyMessage = progress;
        if (clearMessages) Notice = Error = "";
        IsBusy = true;
        try { await action(); }
        catch (OperationCanceledException canceled) { if (!stopping.IsCancellationRequested) Notice = canceled.Message; }
        catch (Exception error) { if (!stopping.IsCancellationRequested) Error = $"{failure}：{error.GetBaseException().Message}"; }
        finally
        {
            IsBusy = false;
            if (_attached && _interactionDepth == 0 && _reloadRequested && !stopping.IsCancellationRequested) _ = RefreshPendingAsync();
        }
    }
    private async Task<bool> RefreshAfterChangeAsync(string? preferredRoot, bool selectLatest = false)
    {
        if (stopping.IsCancellationRequested) return false;
        try { await ReloadAsync(preferredRoot, selectLatest); return true; }
        catch (Exception error)
        {
            if (!stopping.IsCancellationRequested) Error = $"列表刷新失败，请重试：{error.GetBaseException().Message}";
            return false;
        }
    }
    private async Task ReloadAsync(string? preferredRoot = null, bool selectLatest = false)
    {
        var oldRoot = SelectedFolder?.Root;
        var snapshot = await service.GetFoldersAsync();
        stopping.ThrowIfCancellationRequested();
        var folders = snapshot.OrderBy(folder => folder.Root, StringComparer.OrdinalIgnoreCase).Select(folder => new FolderItem(folder)).ToArray();
        _settingSelection = true;
        try
        {
            UpdateItems(Folders, folders, folder => folder.Root, (left, right) => left == right);
            SelectedFolder = Folders.FirstOrDefault(folder => StringComparer.OrdinalIgnoreCase.Equals(folder.Root, preferredRoot ?? oldRoot)) ?? Folders.FirstOrDefault();
        }
        finally { _settingSelection = false; }
        if (!StringComparer.OrdinalIgnoreCase.Equals(SelectedFolder?.Root, oldRoot)) ResetFolderDetails();
        if (SelectedFolder is { } selected) await LoadVersionsAsync(selected.Root, selectLatest);
    }
    private async Task LoadVersionsAsync(string root, bool selectLatest = false)
    {
        var selectedId = selectLatest ? null : SelectedVersion?.Id;
        var baseId = selectLatest ? null : ComparisonBase?.Id;
        var filePath = SelectedFile?.Path;
        var previousSelection = SelectedVersion;
        var previousBase = ComparisonBase;
        var textSummary = TextSummary;
        var textLines = TextLines;
        try
        {
            var snapshot = await service.GetVersionsAsync(root);
            var versions = await Task.Run(() =>
            {
                var ordered = snapshot.OrderByDescending(version => version.Version.CreatedAt).ThenBy(version => version.Id, StringComparer.Ordinal).ToArray();
                return ordered.Select((version, index) => new VersionItem(version,
                    HistoryComparison.Compare(index + 1 < ordered.Length ? ordered[index + 1].Version.Entries : [], version.Version.Entries))).ToArray();
            }, stopping);
            stopping.ThrowIfCancellationRequested();
            _settingSelection = true;
            try
            {
                UpdateItems(Versions, versions, version => version.Id, (left, right) =>
                    left.Note == right.Note && left.Pinned == right.Pinned && left.IsRestoreProtection == right.IsRestoreProtection &&
                    left.Saved.Version.SourceRoot == right.Saved.Version.SourceRoot && left.Saved.Version.CreatedAt == right.Saved.Version.CreatedAt &&
                    left.Saved.Version.Entries.SequenceEqual(right.Saved.Version.Entries) && left.Changes.SequenceEqual(right.Changes));
                RebuildTimeline();
                SelectedVersion = TimelineVersions.FirstOrDefault(version => version.Id == selectedId) ?? TimelineVersions.FirstOrDefault();
                var index = SelectedVersion is null ? -1 : Versions.IndexOf(SelectedVersion);
                ComparisonBase = Versions.FirstOrDefault(version => version.Id == baseId && version.Id != SelectedVersion?.Id)
                    ?? (index >= 0 && index + 1 < Versions.Count ? Versions[index + 1] : null);
            }
            finally { _settingSelection = false; }
            var changes = await Task.Run(() => HistoryComparison.Compare(ComparisonBase?.Saved.Version.Entries ?? [], SelectedVersion?.Saved.Version.Entries ?? []), stopping);
            ApplyComparison(changes);
            SelectedFile = Files.FirstOrDefault(file => file.Path == filePath);
            if (ReferenceEquals(previousSelection, SelectedVersion) && ReferenceEquals(previousBase, ComparisonBase) && SelectedFile?.Path == filePath)
            { TextSummary = textSummary; TextLines = textLines; }
            VersionsLoaded = true;
            OnPropertyChanged(nameof(CanChooseComparison));
            OnPropertyChanged(nameof(ComparisonDirection));
        }
        catch { ClearVersions(); throw; }
    }
    private void RebuildTimeline()
    {
        var selected = SelectedVersion?.Id;
        var rows = Versions.Where(version => !HasFileHistory || version.Changes.Any(change =>
            change.Path == FileHistoryPath || change.Path.StartsWith(FileHistoryPath + "/", StringComparison.Ordinal))).ToArray();
        UpdateItems(TimelineVersions, rows, item => item.Id, ReferenceEquals);
        if (!_settingSelection) SelectedVersion = TimelineVersions.FirstOrDefault(version => version.Id == selected) ?? TimelineVersions.FirstOrDefault();
    }
    private void ApplyComparison(IReadOnlyList<HistoryChange> changes)
    {
        _comparison = changes;
        ComparisonSummary = changes.Count == 0 ? "没有路径或内容变化。" : SummarizeChanges(changes);
        ClearTextPreview();
        RebuildFiles();
    }
    private void RebuildFiles()
    {
        var selected = SelectedFile?.Path;
        var entries = SelectedVersion?.Saved.Version.Entries ?? [];
        var byPath = entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        var old = (ComparisonBase?.Saved.Version.Entries ?? []).ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        var rows = ShowAllFiles
            ? entries.Select(entry => new FileItem(entry.Path, entry.IsDirectory, entry.IsDirectory ? "文件夹" : "文件", null,
                entry.IsDirectory ? null : entry.Length, true))
            : _comparison.Select(change => new FileItem(change.Path,
                (byPath.GetValueOrDefault(change.Path) ?? old.GetValueOrDefault(change.Path))?.IsDirectory == true,
                change.Kind, change.BeforeLength, change.AfterLength, byPath.ContainsKey(change.Path)));
        var filtered = rows.Where(file => file.Path.Contains(FileSearch.Trim(), StringComparison.OrdinalIgnoreCase) &&
            (!HasFileHistory || file.Path == FileHistoryPath || file.Path.StartsWith(FileHistoryPath + "/", StringComparison.Ordinal))).ToArray();
        UpdateItems(Files, filtered, file => file.Path, (left, right) => left == right);
        SelectedFile = Files.FirstOrDefault(file => file.Path == selected);
        OnPropertyChanged(nameof(HasFiles));
    }
    private void ClearTextPreview() { TextSummary = ""; TextLines = []; }
    private void ResetFolderDetails() { Note = FileHistoryPath = FileSearch = ""; ClearVersions(); }
    private void ClearVersions()
    {
        VersionsLoaded = false;
        _settingSelection = true;
        try { SelectedVersion = ComparisonBase = null; Versions.Clear(); TimelineVersions.Clear(); }
        finally { _settingSelection = false; }
        SelectedFile = null;
        ApplyComparison([]);
        OnPropertyChanged(nameof(CanChooseComparison));
    }
    internal static string SummarizeChanges(IReadOnlyList<HistoryChange> changes) =>
        $"新增 {changes.Count(change => change.Kind == "新增")} 项 · 修改 {changes.Count(change => change.Kind == "修改")} 项 · 删除 {changes.Count(change => change.Kind == "删除")} 项";
    public static string FormatSize(long bytes) => bytes < 1024 ? $"{bytes:N0} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:N1} KiB"
        : bytes < 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024):N1} MiB" : $"{bytes / (1024d * 1024 * 1024):N2} GiB";
    private static void UpdateItems<T>(ObservableCollection<T> items, IReadOnlyList<T> snapshot, Func<T, string> key, Func<T, T, bool> unchanged)
    {
        var comparer = StringComparer.Ordinal;
        var keys = snapshot.Select(key).ToHashSet(comparer);
        for (var index = items.Count - 1; index >= 0; index--)
            if (!keys.Contains(key(items[index]))) items.RemoveAt(index);
        var existing = items.ToDictionary(key, comparer);
        for (var index = 0; index < snapshot.Count; index++)
        {
            var next = snapshot[index];
            if (!existing.TryGetValue(key(next), out var previous)) { items.Insert(index, next); continue; }
            if (!comparer.Equals(key(items[index]), key(next))) items.Move(items.IndexOf(previous), index);
            if (!unchanged(previous, next)) items[index] = next;
        }
        Debug.Assert(items.Select(key).SequenceEqual(snapshot.Select(key), comparer));
    }
}

internal sealed record FolderItem(FolderInfo Info)
{
    public string Root => Info.Root;
    public string Name => Path.GetFileName(Root.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : Root;
    public string LastSaved => Info.LastSavedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "尚未保存";
    public string Detail => $"{Info.VersionCount} 个版本 · {FolderHistoryViewModel.FormatSize(Info.HistoryBytes)}";
}
internal sealed record VersionItem(SavedVersion Saved, IReadOnlyList<HistoryChange> Changes)
{
    public string Id => Saved.Id;
    public string Title => Saved.Title;
    public string Note => Saved.Note;
    public bool HasNote => Note.Length > 0;
    public bool Pinned => Saved.Pinned;
    public bool IsRestoreProtection => Saved.IsRestoreProtection;
    public string Summary => FolderHistoryViewModel.SummarizeChanges(Changes);
}
internal sealed record FileItem(string Path, bool IsDirectory, string Kind, long? BeforeLength, long? AfterLength, bool ExistsInVersion)
{
    public string SizeLabel => IsDirectory ? "文件夹" :
        $"{(BeforeLength is { } before ? FolderHistoryViewModel.FormatSize(before) : "—")} → {(AfterLength is { } after ? FolderHistoryViewModel.FormatSize(after) : "—")}";
}
