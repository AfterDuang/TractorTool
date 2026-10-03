using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TractorTool.UI;

namespace TractorTool.Plugins.ClipboardHistory;

public partial class ClipboardHistoryViewModel : ObservableObject, IPluginPageLifetime
{
    private readonly IClipboardHistoryService _service;
    private readonly Action<Action> _dispatch;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationToken _token;
    private bool _left, _initialized, _refreshPending, _reconciling, _retryPreview;
    private int _operations;
    private Task _refreshTask = Task.CompletedTask;
    private CancellationTokenSource? _previewCancellation;

    public ClipboardHistoryViewModel(IClipboardHistoryService service, CancellationToken stopping, Action<Action> dispatch)
    {
        _service = service;
        _dispatch = dispatch;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _token = _lifetime.Token;
    }

    public ObservableCollection<HistoryEntry> Items { get; } = [];
    public Task PreviewCompletion { get; private set; } = Task.CompletedTask;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RefreshCommand))] private bool _isLoading;
    [ObservableProperty] private bool _isPreviewLoading;
    [ObservableProperty] private string _status = "正在读取系统剪贴板历史…";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasWatchError))] private string _watchError = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasCopyStatus))] private string _copyStatus = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelection)), NotifyCanExecuteChangedFor(nameof(CopyCommand))]
    private HistoryEntry? _selectedEntry;
    [ObservableProperty] private HistoryPreview? _preview;
    public bool HasSelection => SelectedEntry is not null;
    public bool HasWatchError => WatchError.Length != 0;
    public bool HasCopyStatus => CopyStatus.Length != 0;
    public bool CanRefresh => !_left && !_token.IsCancellationRequested && !IsLoading;
    public bool CanCopy => !_left && !_token.IsCancellationRequested && HasSelection;

    public Task InitializeAsync()
    {
        if (_initialized || _left || _token.IsCancellationRequested) return _refreshTask;
        _initialized = true;
        _service.HistoryChanged += OnHistoryChanged;
        try { _service.StartWatching(); }
        catch { WatchError = "自动刷新暂时不可用，可手动刷新。"; }
        return RequestRefreshAsync();
    }

    private void OnHistoryChanged(object? sender, EventArgs args) => _dispatch(() =>
    {
        if (!_left) _ = RequestRefreshAsync();
    });

    // Events during an active read collapse into one further snapshot; no polling or history database.
    public Task RequestRefreshAsync()
    {
        if (_left || _token.IsCancellationRequested) return Task.CompletedTask;
        _refreshPending = true;
        return IsLoading ? _refreshTask : _refreshTask = RefreshLoopAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync()
    {
        _retryPreview = true;
        return RequestRefreshAsync();
    }

    private async Task RefreshLoopAsync()
    {
        _operations++;
        IsLoading = true;
        try
        {
            while (_refreshPending && !_left && !_token.IsCancellationRequested)
            {
                _refreshPending = false;
                var retryPreview = _retryPreview;
                _retryPreview = false;
                try
                {
                    var result = await _service.ReadAsync(_token);
                    if (_left || _token.IsCancellationRequested) return;
                    if (result.Status is HistoryStatus.Success or HistoryStatus.Disabled) ApplySnapshot(result.Items, retryPreview);
                    Status = result.Status switch {
                        HistoryStatus.Disabled => "Windows 剪贴板历史尚未开启。按 Win + V 按系统提示开启。",
                        HistoryStatus.Denied => "暂时无法访问系统历史，返回此窗口后将重试。",
                        HistoryStatus.Failed => "无法读取系统剪贴板历史，请稍后刷新。",
                        _ => Items.Count == 0 ? "系统剪贴板历史为空。复制内容后会自动显示在这里。" : $"{Items.Count} 条系统历史"
                    };
                }
                catch (OperationCanceledException) when (_token.IsCancellationRequested) { return; }
                catch (UnauthorizedAccessException) { if (!_left) Status = "Windows 拒绝访问剪贴板历史。请让窗口保持前台后重试。"; }
                catch { if (!_left) Status = "无法读取系统剪贴板历史，请稍后刷新。"; }
            }
        }
        finally { IsLoading = false; FinishOperation(); }
    }

    private void ApplySnapshot(IReadOnlyList<HistoryEntry> snapshot, bool retryPreview)
    {
        var selected = SelectedEntry;
        _reconciling = true;
        try
        {
            var ids = snapshot.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            for (int i = Items.Count - 1; i >= 0; i--) if (!ids.Contains(Items[i].Id)) Items.RemoveAt(i);
            for (int i = 0; i < snapshot.Count; i++)
            {
                var existing = Items.FirstOrDefault(e => e.Id == snapshot[i].Id);
                if (existing is null) Items.Insert(i, snapshot[i]);
                else
                {
                    var index = Items.IndexOf(existing);
                    if (index != i) Items.Move(index, i);
                    if (existing != snapshot[i]) Items[i] = snapshot[i];
                }
            }
            SelectedEntry = Items.FirstOrDefault(e => e.Id == selected?.Id) ?? Items.FirstOrDefault();
        }
        finally { _reconciling = false; }
        if (retryPreview || SelectedEntry != selected || (Preview is null && !IsPreviewLoading)) BeginPreview();
    }

    partial void OnSelectedEntryChanged(HistoryEntry? value)
    {
        if (!_reconciling) BeginPreview();
    }

    private void BeginPreview()
    {
        _previewCancellation?.Cancel();
        _previewCancellation = null;
        ReplacePreview(null);
        CopyStatus = "";
        IsPreviewLoading = false;
        if (_left || _token.IsCancellationRequested || SelectedEntry is not { } entry) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_token);
        _previewCancellation = cancellation;
        PreviewCompletion = LoadPreviewAsync(entry.Id, cancellation);
    }

    private async Task LoadPreviewAsync(string id, CancellationTokenSource cancellation)
    {
        _operations++;
        IsPreviewLoading = true;
        var token = cancellation.Token;
        try
        {
            var result = await _service.PreviewAsync(id, token);
            if (_left || token.IsCancellationRequested || !ReferenceEquals(_previewCancellation, cancellation)) result.Dispose();
            else ReplacePreview(result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch
        {
            if (!_left && !token.IsCancellationRequested && ReferenceEquals(_previewCancellation, cancellation))
                ReplacePreview(new("", "此项目暂时无法预览，可以尝试复制原内容或刷新。"));
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation)) { _previewCancellation = null; IsPreviewLoading = false; }
            cancellation.Dispose();
            FinishOperation();
        }
    }

    private void ReplacePreview(HistoryPreview? value)
    {
        var previous = Preview;
        Preview = value; // Remove the image binding before releasing its native pixels.
        previous?.Dispose();
    }

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private void Copy()
    {
        if (!CanCopy || SelectedEntry is not { } entry) return;
        HistoryCopyStatus result;
        try { result = _service.Copy(entry.Id); }
        catch (UnauthorizedAccessException) { result = HistoryCopyStatus.Denied; }
        catch { result = HistoryCopyStatus.Failed; }
        CopyStatus = result switch {
            HistoryCopyStatus.Success => "已复制，可以粘贴到其他应用。",
            HistoryCopyStatus.Deleted => "此历史项已被系统移除，请刷新后选择其他项目。",
            HistoryCopyStatus.Denied => "Windows 拒绝复制，请让窗口保持前台后重试。",
            _ => "复制失败，请稍后重试。"
        };
    }

    public void OnNavigatedFrom()
    {
        if (_left) return;
        _left = true;
        _service.HistoryChanged -= OnHistoryChanged;
        try { _service.Dispose(); } catch { /* No clipboard contents are logged. */ }
        _lifetime.Cancel();
        _previewCancellation?.Cancel();
        SelectedEntry = null;
        ReplacePreview(null);
        Items.Clear();
        RefreshCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        if (_operations == 0) _lifetime.Dispose();
    }

    private void FinishOperation()
    {
        _operations--;
        if (_left && _operations == 0) _lifetime.Dispose();
    }
}
