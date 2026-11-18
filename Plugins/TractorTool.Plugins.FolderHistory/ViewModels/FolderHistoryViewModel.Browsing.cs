using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TractorTool.Plugins.FolderHistory.Models;
using TractorTool.Plugins.FolderHistory.Processing;
using TractorTool.Plugins.FolderHistory.Storage;

namespace TractorTool.Plugins.FolderHistory.ViewModels;

internal sealed partial class FolderHistoryViewModel
{
    private readonly HashSet<string> _expanded = new(HistoryPaths.Comparer) { "" };
    private Dictionary<string, TreeItem> _visibleByPath = new(HistoryPaths.Comparer);
    private HashSet<string> _scope = new(HistoryPaths.Comparer);
    private CancellationTokenSource? _preview;
    private CancellationTokenSource? _browse;
    private Action? _resumeBrowse;
    private (string Root, string? Before, string After, FileItem File)? _previewKey;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsComparing))] private int _selectedTab;
    [ObservableProperty] private IReadOnlyList<TreeItem> _visibleTreeItems = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ScopeTitle), nameof(IsSingleFile))]
    private IReadOnlyList<TreeItem> _selectedTreeItems = [];
    [ObservableProperty] private IReadOnlyList<VersionItem> _versions = [];
    [ObservableProperty] private IReadOnlyList<VersionItem> _timelineVersions = [];
    [ObservableProperty] private string _emptyTimelineMessage = "正在读取历史…";
    [ObservableProperty] private IReadOnlyList<VersionItem> _comparisonBases = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasVersion), nameof(AfterTitle))]
    private VersionItem? _selectedVersion;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(BeforeTitle))] private VersionItem? _comparisonBase;
    [ObservableProperty] private IReadOnlyList<FileItem> _files = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasFileDetail), nameof(ShowChangeList),
        nameof(CanReturnToChanges), nameof(SelectedFileSummary), nameof(DetailPath), nameof(BeforeFileInfo), nameof(AfterFileInfo))]
    private FileItem? _selectedFile;
    [ObservableProperty] private string _comparisonSummary = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasTextPreview))] private string _textSummary = "";
    [ObservableProperty] private IReadOnlyList<TextDiffLine> _textLines = [];

    public string ScopeTitle => SelectedTreeItems.Count switch
    {
        0 => "尚未选择文件",
        1 => SelectedTreeItems[0].Path is { Length: > 0 } path ? path : SelectedFolder?.Name ?? "",
        _ => $"已选 {SelectedTreeItems.Count} 项"
    };
    public bool IsSingleFile => SelectedTreeItems is [{ Path.Length: > 0 } item] &&
        !(SelectedVersion?.Entries.GetValueOrDefault(item.Path)?.IsDirectory ??
          ComparisonBase?.Entries.GetValueOrDefault(item.Path)?.IsDirectory ?? item.IsDirectory);
    public bool IsComparing => SelectedTab == 1;
    public bool HasVersion => SelectedVersion is not null;
    public bool HasFileDetail => SelectedFile is not null;
    public bool ShowChangeList => HasVersion && !IsSingleFile && !HasFileDetail;
    public bool CanReturnToChanges => !IsSingleFile && HasFileDetail;
    public bool HasTextPreview => TextSummary.Length != 0;
    public string DetailPath => SelectedFile?.Path ?? "";
    public string SelectedFileSummary => SelectedFile is not { } file ? "" :
        file.Before is { IsDirectory: false } old && file.After is { IsDirectory: false } next &&
        old.Length == next.Length && old.Hash != next.Hash ? "内容已修改，文件大小相同" : file.Kind;
    public string BeforeFileInfo => FileItem.Describe(SelectedFile?.Before);
    public string AfterFileInfo => FileItem.Describe(SelectedFile?.After);
    public string BeforeTitle => ComparisonBase?.Title ?? "首次保存前";
    public string AfterTitle => SelectedVersion?.Title ?? "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanGoToLastPresentVersion))]
    private VersionItem? _lastPresentVersion;
    public bool CanGoToLastPresentVersion => LastPresentVersion is not null;
    [RelayCommand]
    private void GoToLastPresentVersion()
    {
        if (LastPresentVersion is { } previous) SelectedVersion = previous;
    }

    private void ApplyWorkspace(HistoryWorkspace? workspace, bool selectLatest)
    {
        var initial = _workspace is null;
        var selectedId = selectLatest ? null : SelectedVersion?.Id;
        var baseId = selectLatest ? null : ComparisonBase?.Id;
        var detailPath = selectLatest ? null : SelectedFile?.Path;
        _workspace = workspace;
        _settingSelection = true;
        try
        {
            Versions = workspace?.Versions ?? [];
            if (initial) _scope = new(HistoryPaths.Comparer) { "" };
            RebuildTree();
        }
        finally { _settingSelection = false; }
        UpdateTimeline(selectedId, baseId, detailPath);
    }

    public void ToggleExpanded(TreeItem item)
    {
        if (_settingSelection || !item.HasChildren || _workspace is null) return;
        if (!_expanded.Add(item.Path)) _expanded.Remove(item.Path);
        _settingSelection = true;
        var oldScope = _scope;
        try { RebuildTree(); }
        finally { _settingSelection = false; }
        if (!oldScope.SetEquals(_scope)) UpdateTimeline(SelectedVersion?.Id, null, null);
    }

    private void RebuildTree()
    {
        VisibleTreeItems = _workspace?.VisibleTree(_expanded) ?? [];
        _visibleByPath = VisibleTreeItems.ToDictionary(item => item.Path, HistoryPaths.Comparer);
        SelectedTreeItems = VisibleTreeItems.Where(item => _scope.Contains(item.Path)).ToArray();
        _scope = SelectedTreeItems.Select(item => item.Path).ToHashSet(HistoryPaths.Comparer);
    }

    public void SetSelection(IEnumerable<TreeItem> selection)
    {
        if (_settingSelection) return;
        var visible = TreeItem.ResolveSelection(selection, _visibleByPath);
        var paths = visible.Select(item => item.Path).ToHashSet(HistoryPaths.Comparer);
        if (_scope.SetEquals(paths)) return;
        _scope = paths;
        _settingSelection = true;
        try { SelectedTreeItems = visible; }
        finally { _settingSelection = false; }
        UpdateTimeline(SelectedVersion?.Id, null, null);
    }

    private void UpdateTimeline(string? selectedId, string? baseId, string? detailPath)
    {
        CancelBrowse();
        var workspace = _workspace;
        var scope = _scope;
        if (workspace is null || scope.Count == 0) { ShowTimeline([]); return; }
        if (scope.Contains(HistorySelection.Root)) { ShowTimeline(workspace.Versions); return; }
        ShowTimeline([], loading: true);
        Browse(token => workspace.Timeline(scope, token), rows => ShowTimeline(rows), () => UpdateTimeline(selectedId, baseId, detailPath));

        void ShowTimeline(IReadOnlyList<VersionItem> rows, bool loading = false)
        {
            EmptyTimelineMessage = workspace is null || loading ? "正在读取历史…" :
                scope.Count == 0 ? "请在左侧选择文件或文件夹。" :
                workspace.Versions.Length == 0 ? "这个文件夹尚未保存任何版本。" : "所选范围还没有历史记录。";
            _settingSelection = true;
            try
            {
                TimelineVersions = rows;
                SelectedVersion = rows.FirstOrDefault(row => row.Id == selectedId) ?? rows.FirstOrDefault();
                SelectComparisonBase(SelectedVersion?.Id == selectedId ? baseId : null);
            }
            finally { _settingSelection = false; }
            UpdateComparison(detailPath);
        }
    }

    private void SelectComparisonBase(string? preferred)
    {
        ComparisonBases = SelectedVersion is not { } selected ? [] :
            Versions.SkipWhile(version => version.Id != selected.Id).Skip(1).ToArray();
        ComparisonBase = ComparisonBases.FirstOrDefault(version => version.Id == preferred) ?? ComparisonBases.FirstOrDefault();
        LastPresentVersion = SelectedTreeItems is [{ Path.Length: > 0 } item] && SelectedVersion is { } row &&
            !row.Entries.ContainsKey(item.Path)
            ? TimelineVersions.SkipWhile(version => version.Id != row.Id).Skip(1)
                .FirstOrDefault(version => version.Entries.ContainsKey(item.Path)) : null;
    }

    partial void OnSelectedVersionChanged(VersionItem? value)
    {
        if (_settingSelection) return;
        _settingSelection = true;
        try { SelectComparisonBase(null); }
        finally { _settingSelection = false; }
        UpdateComparison(null);
    }

    partial void OnSelectedTabChanged(int value)
    {
        // A scope change clears the current row while its timeline loads. That read serves both tabs.
        if (HasVersion) UpdateComparison(SelectedFile?.Path);
    }
    partial void OnComparisonBaseChanged(VersionItem? value)
    {
        if (!_settingSelection && IsComparing && HasVersion) UpdateComparison(SelectedFile?.Path);
    }
    partial void OnSelectedFileChanged(FileItem? value)
    {
        if (!_settingSelection) UpdateFileDetail();
    }

    private void UpdateComparison(string? detailPath)
    {
        CancelBrowse();
        var before = ComparisonBase;
        if (!IsComparing || SelectedVersion is not { } after) { ShowFiles([], null, ""); return; }
        if (IsSingleFile)
        {
            var file = FileItem.ForFile(SelectedTreeItems[0].Path, before, after);
            ShowFiles([file], file, file.Kind);
            return;
        }
        var scope = _scope;
        var previous = before?.Id == ComparisonBases.FirstOrDefault()?.Id;
        ShowFiles([], null, "正在比较…");
        Browse(token =>
        {
            bool Includes(string path)
            {
                token.ThrowIfCancellationRequested();
                return HistorySelection.Contains(scope, path);
            }
            var changes = previous ? after.Changes.Where(change => Includes(change.Path)).ToArray() :
                HistoryComparison.Compare(before?.Saved.Version.Entries.Where(entry => Includes(entry.Path)).ToArray() ?? [],
                    after.Saved.Version.Entries.Where(entry => Includes(entry.Path)).ToArray());
            var files = changes.Select(change =>
            {
                token.ThrowIfCancellationRequested();
                return FileItem.ForChange(change, before, after);
            }).ToArray();
            return (Files: files, Detail: files.FirstOrDefault(file => file.Path == detailPath), Summary: HistoryWorkspace.Summarize(changes));
        }, result => ShowFiles(result.Files, result.Detail, result.Summary), () => UpdateComparison(detailPath));
    }

    private void ShowFiles(IReadOnlyList<FileItem> files, FileItem? detail, string summary)
    {
        _settingSelection = true;
        try { Files = files; SelectedFile = detail; ComparisonSummary = summary; }
        finally { _settingSelection = false; }
        OnPropertyChanged(nameof(IsSingleFile));
        OnPropertyChanged(nameof(ShowChangeList));
        OnPropertyChanged(nameof(CanReturnToChanges));
        UpdateFileDetail();
    }

    private void CancelBrowse(bool keepForReattach = false)
    {
        _browse?.Cancel();
        _browse = null;
        if (!keepForReattach) _resumeBrowse = null;
    }

    private void Browse<T>(Func<CancellationToken, T> read, Action<T> show, Action resume)
    {
        _resumeBrowse = resume;
        if (!_attached) return;
        var request = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _browse = request;
        _ = ReadAsync();

        async Task ReadAsync()
        {
            try
            {
                var result = await Task.Run(() => read(request.Token), request.Token);
                if (!request.IsCancellationRequested) show(result);
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (!request.IsCancellationRequested)
                {
                    if (!HasVersion) EmptyTimelineMessage = "无法读取所选范围的历史。";
                    ReportError("无法读取历史变化", error);
                }
            }
            finally
            {
                if (ReferenceEquals(_browse, request)) { _browse = null; _resumeBrowse = null; }
                request.Dispose();
            }
        }
    }

    private void CancelPreview()
    {
        _preview?.Cancel();
        _preview = null;
        _previewKey = null;
    }

    private void UpdateFileDetail()
    {
        var file = SelectedFile;
        var before = ComparisonBase?.Id;
        var after = SelectedVersion?.Id;
        var root = SelectedFolder?.Root;
        if (_attached && IsComparing && root is not null && after is not null && file is not null &&
            _previewKey == (root, before, after, file)) return;
        CancelPreview();
        TextSummary = "";
        TextLines = [];
        if (!_attached || !IsComparing || file is null || root is null || after is null) return;
        _previewKey = (root, before, after, file);
        if (file.Before?.IsDirectory == true && file.After?.IsDirectory == false ||
            file.Before?.IsDirectory == false && file.After?.IsDirectory == true)
        { TextSummary = "项目类型发生变化。"; return; }
        if (file.IsDirectory || file.Before is null && file.After is null) return;
        if (file.Before?.Length > TextComparison.MaxBytes || file.After?.Length > TextComparison.MaxBytes)
        { TextSummary = "文件超过 256 KiB，可另存一份查看。"; return; }
        var request = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _preview = request;
        TextSummary = "正在读取内容…";
        _ = LoadTextAsync();

        async Task LoadTextAsync()
        {
            try
            {
                var oldBytes = file.Before is null ? [] : await service.ReadFileAsync(root, before!, file.Before.Path, cancellationToken: request.Token);
                var newBytes = file.After is null ? [] : file.Before?.Hash == file.After.Hash ? oldBytes :
                    await service.ReadFileAsync(root, after, file.After.Path, cancellationToken: request.Token);
                var result = await Task.Run(() => TextComparison.Compare(oldBytes, newBytes), request.Token);
                if (!request.IsCancellationRequested)
                {
                    TextSummary = result.Summary;
                    TextLines = result.Lines;
                }
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (!request.IsCancellationRequested)
                {
                    TextSummary = "无法读取内容：" + error.GetBaseException().Message;
                    _previewKey = null;
                }
            }
            finally
            {
                if (ReferenceEquals(_preview, request)) _preview = null;
                request.Dispose();
            }
        }
    }

    public VersionActionContext? CaptureVersionAction(VersionItem row)
    {
        if (SelectedFolder is not { } folder || !VersionsLoaded || _scope.Count == 0 || !TimelineVersions.Contains(row)) return null;
        return VersionActionContext.Capture(folder.Root, row, SelectedTreeItems.Select(item => item.Path), ScopeTitle);
    }
}
