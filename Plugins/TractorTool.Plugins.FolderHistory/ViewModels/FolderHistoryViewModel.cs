using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TractorTool.Plugins.FolderHistory.Models;
using TractorTool.Plugins.FolderHistory.Services;
using TractorTool.Plugins.FolderHistory.Storage;

namespace TractorTool.Plugins.FolderHistory.ViewModels;

internal sealed partial class FolderHistoryViewModel(FolderHistoryService service, CancellationToken stopping) : ObservableObject
{
    private bool _attached;
    private bool _reloadRequested;
    private bool _refreshing;
    private bool _settingSelection;
    private int _interactionDepth;
    private CancellationTokenSource? _folderLoad;
    private CancellationTokenSource? _versionLoad;
    private HistoryWorkspace? _workspace;

    [ObservableProperty] private IReadOnlyList<FolderItem> _folders = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasFolder), nameof(FolderSummary), nameof(CanSave), nameof(CanChangeProtection), nameof(ProtectionAction), nameof(HasPendingRestore), nameof(HasPendingCleanup))]
    private FolderItem? _selectedFolder;
    [ObservableProperty] private string _busyMessage = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSave))] private bool _versionsLoaded;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasNotice))] private string _notice = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string _error = "";

    public bool HasFolder => SelectedFolder is not null;
    public bool CanSave => HasFolder && VersionsLoaded && !HasPendingRestore && !HasPendingCleanup;
    public bool CanChangeProtection => HasFolder && !HasPendingRestore && !HasPendingCleanup;
    public bool HasNotice => Notice.Length != 0;
    public bool HasError => Error.Length != 0;
    public bool HasPendingRestore => SelectedFolder?.Info.HasPendingRestore == true;
    public bool HasPendingCleanup => SelectedFolder?.Info.HasPendingCleanup == true;
    public string ProtectionAction => SelectedFolder?.Info.NeedsAttention == true ? "重新开启自动保存…"
        : SelectedFolder?.Info.ProtectionEnabled == true ? "暂停自动保存" : "开启自动保存…";
    public string FolderSummary => SelectedFolder is { } folder
        ? $"{folder.Info.Status} · {folder.Info.VersionCount} 个版本 · 上次保存 {folder.LastSaved} · {FormatSize(folder.Info.HistoryBytes)}" : "";

    public async Task InitializeAsync()
    {
        if (!_attached)
        {
            _attached = true;
            service.Changed += ServiceChanged;
            _resumeBrowse?.Invoke();
        }
        await RefreshAsync();
        if (_attached) UpdateFileDetail();
    }
    public void Detach()
    {
        _attached = false;
        service.Changed -= ServiceChanged;
        _folderLoad?.Cancel();
        _versionLoad?.Cancel();
        StopDirectoryTracking();
        CancelBrowse(keepForReattach: true);
        CancelPreview();
    }
    public void DeferRefresh(bool defer)
    {
        _interactionDepth += defer ? 1 : -1;
        Debug.Assert(_interactionDepth >= 0);
        if (_interactionDepth == 0) StartPendingRefresh();
    }
    private void ServiceChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (!_attached || stopping.IsCancellationRequested) return;
        _reloadRequested = true;
        StartPendingRefresh();
    });
    private void StartPendingRefresh()
    {
        if (_attached && _reloadRequested && !IsBusy && !_refreshing && _interactionDepth == 0 && !stopping.IsCancellationRequested)
            _ = RefreshAsync();
    }
    partial void OnSelectedFolderChanged(FolderItem? oldValue, FolderItem? newValue)
    {
        if (_settingSelection || oldValue?.Info.Id == newValue?.Info.Id && HistoryPaths.Comparer.Equals(oldValue?.Root, newValue?.Root)) return;
        _folderLoad?.Cancel();
        Notice = Error = "";
        ResetFolderDetails();
        if (newValue is not null) _ = LoadVersionsAsync(newValue);
    }
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_refreshing) { _reloadRequested = true; return; }
        _refreshing = true;
        _reloadRequested = false;
        try { if (await ReloadAsync()) UpdateFileDetail(); }
        catch (Exception error) { if (_attached && !stopping.IsCancellationRequested) ReportError("无法读取历史", error); }
        finally { _refreshing = false; StartPendingRefresh(); }
    }
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

    public Task CreateSnapshotAsync(string root, string note) =>
        RunAsync("正在创建快照…", "无法创建快照", async () =>
        {
            var created = await service.SaveAsync(root, note);
            await RefreshAfterChangeAsync(root, selectLatest: created);
            Notice = created ? "已为整个文件夹创建快照。" : "文件没有变化，已保留现有快照。";
        });

    public Task SetSnapshotNoteAsync(string root, string id, string originalNote, string note) =>
        ChangeAsync("正在保存说明…", "无法保存说明", root, () => service.SetSnapshotNoteAsync(root, id, originalNote, note), "已保存快照说明。");
    public Task TogglePinnedAsync(VersionActionContext context) => ChangeAsync("正在更新重要标记…", "无法更新重要标记", context.Root,
        () => service.SetPinnedAsync(context.Root, context.VersionId, !context.Pinned), context.Pinned ? "已取消重要标记。" : "已标记为重要版本。");

    public Task ExportAsync(VersionActionContext context, string destination) =>
        RunAsync("正在另存历史内容…", "无法另存历史内容", async () =>
        {
            var result = context.SingleFilePath is { } file
                ? await service.ExportFileAsync(context.Root, context.VersionId, destination, file)
                : await service.ExportSelectionAsync(context.Root, context.VersionId, destination, context.RelativePaths);
            Notice = "已另存到：" + result;
        });
    public Task<RestorePreview?> PreviewRestoreAsync(VersionActionContext context) =>
        ReadAsync("正在核对恢复范围…", "无法准备恢复", () =>
            service.PreviewRestoreSelectionAsync(context.Root, context.VersionId, context.RelativePaths));
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
        try { await action(); }
        finally { await RefreshAfterChangeAsync(root); }
        Notice = success;
    });
    private async Task<T?> ReadAsync<T>(string progress, string failure, Func<Task<T>> read) where T : class?
    {
        T? value = null;
        await RunAsync(progress, failure, async () => { value = await read(); });
        return value;
    }
    private async Task RunAsync(string progress, string failure, Func<Task> action)
    {
        if (IsBusy || stopping.IsCancellationRequested) return;
        BusyMessage = progress;
        Notice = Error = "";
        IsBusy = true;
        try { await action(); }
        catch (OperationCanceledException canceled) { if (!stopping.IsCancellationRequested) Notice = canceled.Message; }
        catch (Exception error) { if (!stopping.IsCancellationRequested) Error = $"{failure}：{error.GetBaseException().Message}"; }
        finally
        {
            IsBusy = false;
            StartPendingRefresh();
        }
    }
    private async Task<bool> RefreshAfterChangeAsync(string? preferredRoot, bool selectLatest = false)
    {
        if (stopping.IsCancellationRequested) return false;
        try { return await ReloadAsync(preferredRoot, selectLatest); }
        catch (Exception error)
        {
            if (!stopping.IsCancellationRequested) Error = $"列表刷新失败，请重试：{error.GetBaseException().Message}";
            return false;
        }
    }
    private async Task<bool> ReloadAsync(string? preferredRoot = null, bool selectLatest = false)
    {
        if (!_attached) return false;
        _folderLoad?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _folderLoad = request;
        try
        {
            var snapshot = await service.GetFoldersAsync(request.Token);
            request.Token.ThrowIfCancellationRequested();
            var currentRoot = SelectedFolder?.Root;
            var currentId = SelectedFolder?.Info.Id;
            var wanted = preferredRoot ?? currentRoot;
            var folders = snapshot.OrderBy(folder => folder.Root, HistoryPaths.Comparer).Select(folder => new FolderItem(folder)).ToArray();
            _settingSelection = true;
            try
            {
                if (!Folders.SequenceEqual(folders)) Folders = folders;
                SelectedFolder = Folders.FirstOrDefault(folder => HistoryPaths.Comparer.Equals(folder.Root, wanted)) ?? Folders.FirstOrDefault();
            }
            finally { _settingSelection = false; }
            if (SelectedFolder?.Info.Id != currentId || !HistoryPaths.Comparer.Equals(SelectedFolder?.Root, currentRoot))
            {
                Notice = Error = "";
                ResetFolderDetails();
            }
            return SelectedFolder is not { } selected || await LoadVersionsAsync(selected,
                selectLatest && HistoryPaths.Comparer.Equals(selected.Root, preferredRoot), request.Token);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { return false; }
        finally { if (ReferenceEquals(_folderLoad, request)) _folderLoad = null; }
    }

    private async Task<bool> LoadVersionsAsync(FolderItem folder, bool selectLatest = false, CancellationToken cancellationToken = default)
    {
        _versionLoad?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(stopping, cancellationToken);
        _versionLoad = request;
        try
        {
            var (snapshot, exclusions) = await service.GetBrowserDataAsync(folder.Root, request.Token);
            request.Token.ThrowIfCancellationRequested();
            var previous = _workspace;
            var workspace = await Task.Run(() =>
            {
                // A status-only notification need not replace rows or re-extract unchanged content.
                var byId = snapshot.ToDictionary(item => item.Id, StringComparer.Ordinal);
                if (previous is not null && previous.Versions.Length == snapshot.Count &&
                    previous.Versions.All(row => byId.TryGetValue(row.Id, out var saved) &&
                        saved.Number == row.Saved.Number && saved.Note == row.Note && saved.Pinned == row.Pinned && saved.IsRestoreProtection == row.IsRestoreProtection &&
                        saved.Version.CreatedAt == row.Saved.Version.CreatedAt && saved.Version.SourceRoot == row.Saved.Version.SourceRoot &&
                        !request.IsCancellationRequested && saved.Version.Entries.SequenceEqual(row.Saved.Version.Entries))) return previous;
                return new HistoryWorkspace(folder.Name, snapshot, request.Token);
            }, request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(previous, workspace) || selectLatest)
                ApplyWorkspace(workspace, selectLatest);
            TrackDirectory(folder, exclusions);
            VersionsLoaded = true;
            return true;
        }
        catch (Exception error)
        {
            if (request.IsCancellationRequested || !_attached) return false;
            ResetFolderDetails();
            EmptyTimelineMessage = "无法读取此目录的历史。";
            ReportError("无法读取版本", error);
            return false;
        }
        finally { if (ReferenceEquals(_versionLoad, request)) _versionLoad = null; }
    }

    private void ResetFolderDetails()
    {
        _versionLoad?.Cancel();
        StopDirectoryTracking();
        _directoryListing = null;
        VersionsLoaded = false;
        _expanded.Clear();
        _expanded.Add("");
        ApplyWorkspace(null, false);
    }

    public static string FormatSize(long bytes) => bytes < 1024 ? $"{bytes:N0} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:N1} KiB"
        : bytes < 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024):N1} MiB" : $"{bytes / (1024d * 1024 * 1024):N2} GiB";
}

internal sealed record FolderItem(FolderInfo Info)
{
    public string Root => Info.Root;
    public string Name => Path.GetFileName(Root.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : Root;
    public string LastSaved => Info.LastSavedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "尚未保存";
    public string Detail => $"{Info.VersionCount} 个版本 · {FolderHistoryViewModel.FormatSize(Info.HistoryBytes)}";
}
