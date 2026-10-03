using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TractorTool.UI;

namespace TractorTool.Plugins.ClipboardHistory;

public partial class ClipboardHistoryViewModel(IClipboardHistoryReader reader, CancellationToken stopping) : ObservableObject, IPluginPageLifetime
{
    private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(stopping);
    private bool _left;
    private Task? _initialLoad;
    public Task InitializeAsync() => _initialLoad ??= RefreshCommand.ExecuteAsync(null);
    [ObservableProperty] private IReadOnlyList<HistoryEntry> _items = [];
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RefreshCommand))] private bool _isLoading;
    [ObservableProperty] private string _status = "正在读取系统剪贴板历史…";
    public bool CanRefresh => !_left && !IsLoading && !stopping.IsCancellationRequested;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (!CanRefresh) return;
        IsLoading = true;
        Items = [];
        Status = "正在读取系统剪贴板历史…";
        try
        {
            var result = await reader.ReadAsync(_lifetime.Token);
            if (_left || _lifetime.IsCancellationRequested) return;
            Items = result.Items;
            Status = result.Status switch {
                HistoryStatus.Disabled => "Windows 剪贴板历史尚未开启。可按 Win + V 按系统提示开启，再返回刷新。",
                HistoryStatus.Denied => "Windows 拒绝访问剪贴板历史。请让此窗口保持前台后重试，并检查系统设置或组织策略。",
                HistoryStatus.Failed => "无法读取系统剪贴板历史，请稍后刷新。",
                _ => Items.Count == 0 ? "系统剪贴板历史为空。" : $"{Items.Count} 条系统历史"
            };
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (UnauthorizedAccessException) { if (!_left) Status = "Windows 拒绝访问剪贴板历史。请让窗口保持前台后重试。"; }
        catch { if (!_left) Status = "无法读取系统剪贴板历史，请稍后刷新。"; }
        finally
        {
            IsLoading = false;
            if (_left) _lifetime.Dispose();
        }
    }

    public void OnNavigatedFrom()
    {
        if (_left) return;
        _left = true;
        _lifetime.Cancel();
        Items = [];
        RefreshCommand.NotifyCanExecuteChanged();
        if (!IsLoading) _lifetime.Dispose();
    }
}
