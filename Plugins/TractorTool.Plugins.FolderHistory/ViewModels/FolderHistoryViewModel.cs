using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TractorTool.Plugins.FolderHistory.Models;
using TractorTool.Plugins.FolderHistory.Services;

namespace TractorTool.Plugins.FolderHistory.ViewModels;

internal sealed partial class FolderHistoryViewModel(FolderHistoryService service, CancellationToken stopping)
    : ObservableObject
{
    private bool _initialized;
    public ObservableCollection<FolderInfo> Folders { get; } = [];
    public ObservableCollection<SavedVersion> Versions { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasFolder), nameof(FolderSummary), nameof(CanSave))]
    private FolderInfo? _selectedFolder;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasVersion), nameof(PinAction), nameof(CanCompare))]
    private SavedVersion? _selectedVersion;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanCompare))]
    private SavedVersion? _comparisonBase;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelectedEntry))] private HistoryEntry? _selectedEntry;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasEntries))] private IReadOnlyList<HistoryEntry> _entries = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChanges))] private IReadOnlyList<HistoryChange> _changes = [];
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private string _busyMessage = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSave))] private bool _versionsLoaded;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasNotice))] private string _notice = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string _error = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasComparison))] private string _comparisonSummary = "";

    public bool HasFolder => SelectedFolder is not null;
    public bool CanSave => HasFolder && VersionsLoaded;
    public bool HasVersion => SelectedVersion is not null;
    public bool HasEntries => Entries.Count != 0;
    public bool HasSelectedEntry => SelectedEntry is not null;
    public bool HasChanges => Changes.Count != 0;
    public bool HasNotice => Notice.Length != 0;
    public bool HasError => Error.Length != 0;
    public bool HasComparison => ComparisonSummary.Length != 0;
    public bool CanChooseComparison => Versions.Count > 1;
    public bool CanCompare => ComparisonBase is not null && SelectedVersion is not null && ComparisonBase.Id != SelectedVersion.Id;
    public string PinAction => SelectedVersion?.Pinned == true ? "取消重要标记" : "标记为重要";
    public string FolderSummary => SelectedFolder is { } folder
        ? $"{folder.Status} · {folder.VersionCount} 个版本 · 上次保存：{(folder.LastSavedAt is { } time ? time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "尚未保存")}"
        : "";

    partial void OnSelectedFolderChanged(FolderInfo? oldValue, FolderInfo? newValue)
    {
        if (IsBusy || StringComparer.OrdinalIgnoreCase.Equals(oldValue?.Root, newValue?.Root)) return;
        ResetFolderDetails();
        if (newValue is { } folder)
            _ = RunAsync("正在读取版本…", "无法读取版本", () => LoadVersionsAsync(folder.Root));
    }

    partial void OnSelectedVersionChanged(SavedVersion? value)
    {
        SelectedEntry = null;
        Entries = value?.Version.Entries ?? [];
        ResetComparison();
    }

    partial void OnComparisonBaseChanged(SavedVersion? value) => ResetComparison();

    public Task InitializeAsync() => _initialized ? Task.CompletedTask : RefreshAsync();

    [RelayCommand]
    public Task RefreshAsync() => RunAsync("正在读取文件夹和版本…", "无法读取历史", () => ReloadAsync());

    public Task AddFolderAsync(string root) => RunAsync("正在添加文件夹…", "无法添加文件夹", async () =>
    {
        await service.AddFolderAsync(root);
        Notice = "已关联此文件夹。";
        await RefreshAfterChangeAsync(root);
    });

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
                else
                {
                    Note = "";
                    Notice = "已保存新版本。";
                }
            }
        });
    }

    [RelayCommand]
    private Task TogglePinnedAsync()
    {
        if (SelectedFolder is not { } folder || SelectedVersion is not { } version) return Task.CompletedTask;
        return RunAsync("正在更新重要标记…", "无法更新重要标记", async () =>
        {
            await service.SetPinnedAsync(folder.Root, version.Id, !version.Pinned);
            Notice = version.Pinned ? "已取消重要标记。" : "已标记为重要版本。";
            await RefreshAfterChangeAsync(folder.Root);
        });
    }

    [RelayCommand]
    private Task CompareAsync()
    {
        if (!CanCompare || ComparisonBase is not { } before || SelectedVersion is not { } after)
            return Task.CompletedTask;
        return RunAsync("正在比较版本…", "无法比较版本", async () =>
        {
            var (changes, added, removed) = await Task.Run(() =>
            {
                var result = HistoryComparison.Compare(before.Version.Entries, after.Version.Entries);
                return (result, result.Count(change => change.Kind == "新增"), result.Count(change => change.Kind == "删除"));
            }, stopping);
            if (stopping.IsCancellationRequested) return;
            Changes = changes;
            ComparisonSummary = changes.Count == 0 ? "这两个版本没有路径或内容变化。"
                : $"新增 {added} 项 · 修改 {changes.Count - added - removed} 项 · 删除 {removed} 项";
        });
    }

    public Task ExportAsync(string root, string versionId, string parentDirectory, string? path) =>
        RunAsync("正在另存旧版本…", "无法另存旧版本", async () =>
        {
            var output = await service.ExportAsync(root, versionId, parentDirectory, path);
            Notice = $"已另存到：{output}";
        });

    [RelayCommand]
    private Task RemoveFolderAsync()
    {
        if (SelectedFolder is not { } folder) return Task.CompletedTask;
        return RunAsync("正在移除关联…", "无法移除关联", async () =>
        {
            await service.RemoveFolderAsync(folder.Root);
            Notice = "已移除关联。工作文件和历史仍保存在原文件夹中。";
            await RefreshAfterChangeAsync(null);
        });
    }

    public void ReportPickerError(Exception error)
    {
        Notice = "";
        Error = $"无法选择文件夹：{error.Message}";
    }

    private async Task RunAsync(string progress, string failure, Func<Task> action)
    {
        if (IsBusy || stopping.IsCancellationRequested) return;
        BusyMessage = progress;
        Notice = Error = "";
        IsBusy = true;
        try { await action(); }
        catch (OperationCanceledException canceled) { if (!stopping.IsCancellationRequested) Notice = canceled.Message; }
        catch (Exception error) { if (!stopping.IsCancellationRequested) Error = $"{failure}：{error.Message}"; }
        finally { IsBusy = false; }
    }

    private async Task<bool> RefreshAfterChangeAsync(string? preferredRoot, bool selectLatest = false)
    {
        if (stopping.IsCancellationRequested) return false;
        try
        {
            await ReloadAsync(preferredRoot, selectLatest);
            return true;
        }
        catch (Exception error)
        {
            if (!stopping.IsCancellationRequested) Error = $"操作已完成，但列表刷新失败：{error.Message}";
            return false;
        }
    }

    private async Task ReloadAsync(string? preferredRoot = null, bool selectLatest = false)
    {
        var oldRoot = SelectedFolder?.Root;
        IReadOnlyList<FolderInfo> snapshot;
        try { snapshot = await service.GetFoldersAsync(); }
        catch { ClearVersions(); throw; }
        stopping.ThrowIfCancellationRequested();
        _initialized = true;
        var folders = snapshot.OrderBy(folder => folder.Root, StringComparer.OrdinalIgnoreCase).ToArray();
        UpdateItems(Folders, folders, folder => folder.Root, (left, right) => left == right);
        SelectedFolder = Folders.FirstOrDefault(folder => StringComparer.OrdinalIgnoreCase.Equals(folder.Root, preferredRoot ?? oldRoot))
            ?? Folders.FirstOrDefault();
        if (!StringComparer.OrdinalIgnoreCase.Equals(SelectedFolder?.Root, oldRoot)) ResetFolderDetails();
        if (SelectedFolder is { } selected) await LoadVersionsAsync(selected.Root, selectLatest);
    }

    private async Task LoadVersionsAsync(string root, bool selectLatest = false)
    {
        var selectedId = SelectedVersion?.Id;
        var baseId = ComparisonBase?.Id;
        try
        {
            var snapshot = await service.GetVersionsAsync(root);
            stopping.ThrowIfCancellationRequested();
            var existing = Versions.ToDictionary(version => version.Id, StringComparer.Ordinal);
            var versions = await Task.Run(() => snapshot.OrderByDescending(version => version.Version.CreatedAt)
                .ThenBy(version => version.Id, StringComparer.Ordinal)
                .Select(next => existing.TryGetValue(next.Id, out var previous) && previous.Note == next.Note && previous.Pinned == next.Pinned &&
                    previous.Version.SourceRoot == next.Version.SourceRoot && previous.Version.CreatedAt == next.Version.CreatedAt &&
                    previous.Version.Entries.SequenceEqual(next.Version.Entries) ? previous : next).ToArray(), stopping);
            stopping.ThrowIfCancellationRequested();
            UpdateItems(Versions, versions, version => version.Id, ReferenceEquals);
            SelectedVersion = (selectLatest ? null : Versions.FirstOrDefault(version => version.Id == selectedId)) ?? Versions.FirstOrDefault();
            ComparisonBase = Versions.FirstOrDefault(version => version.Id == baseId)
                ?? Versions.FirstOrDefault(version => version.Id != SelectedVersion?.Id);
            VersionsLoaded = true;
            OnPropertyChanged(nameof(CanChooseComparison));
        }
        catch { ClearVersions(); throw; }
    }

    private void ResetFolderDetails()
    {
        Note = "";
        ClearVersions();
    }

    private void ClearVersions()
    {
        VersionsLoaded = false;
        SelectedVersion = ComparisonBase = null;
        Versions.Clear();
        OnPropertyChanged(nameof(CanChooseComparison));
    }

    private void ResetComparison()
    {
        Changes = [];
        ComparisonSummary = "";
    }

    private static void UpdateItems<T>(ObservableCollection<T> items, IReadOnlyList<T> snapshot,
        Func<T, string> key, Func<T, T, bool> unchanged)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
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
