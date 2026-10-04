using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TractorTool.Contracts.Tasks;
using TractorTool.Core.Tasks;

namespace TractorTool.ViewModels;

public partial class TasksViewModel : ViewModelBase, IDisposable
{
    private readonly BackgroundTaskService? _service;
    private readonly Action<string> _openPlugin;
    private readonly Func<string, string> _pluginName;
    private readonly Func<string, bool> _canOpenPlugin;
    private int _refreshQueued;
    private bool _disposed;
    public ObservableCollection<TaskRow> Items { get; } = [];
    [ObservableProperty] private string _summary = "暂无任务";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string _error = "";
    public bool HasError => Error.Length != 0;
    public bool IsEmpty => Items.Count == 0;

    public TasksViewModel(BackgroundTaskService? service, Action<string> openPlugin, Func<string, string> pluginName,
        Func<string, bool> canOpenPlugin)
    {
        _service = service; _openPlugin = openPlugin; _pluginName = pluginName; _canOpenPlugin = canOpenPlugin;
        if (service is not null) service.Changed += OnChanged;
        Refresh();
    }
    private void OnChanged(object? sender, EventArgs args)
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        Dispatcher.UIThread.Post(() => { Interlocked.Exchange(ref _refreshQueued, 0); if (!_disposed) Refresh(); });
    }
    public void Refresh()
    {
        var snapshots = _service?.Snapshot ?? [];
        var ids = snapshots.Select(s => s.Id).ToHashSet();
        foreach (var row in Items.Where(r => !ids.Contains(r.Snapshot.Id)).ToArray()) Items.Remove(row);
        // Preserve row identity during progress so keyboard focus and selection do not jump.
        foreach (var snapshot in snapshots.OrderBy(s => s.CreatedAt))
        {
            var row = Items.FirstOrDefault(r => r.Snapshot.Id == snapshot.Id);
            if (row is null) Items.Insert(0, new TaskRow(snapshot, this));
            else row.Update(snapshot);
        }
        var active = snapshots.Count(s => TaskRow.Active(s.State));
        var interrupted = snapshots.Count(s => s.State == BackgroundTaskState.Interrupted);
        Summary = active > 0 ? $"{active} 个任务进行中" : Items.Count == 0 ? "暂无任务" : $"{Items.Count} 条任务记录";
        if (interrupted > 0) Summary += $" · {interrupted} 个任务中断，返回工具后重新提交";
        Error = _service?.PersistenceError is { } error ? "任务记录读写失败，原记录已保留；新任务在记录恢复前不会执行。" + error : "";
        OnPropertyChanged(nameof(IsEmpty));
    }
    internal string PluginName(string id) => _pluginName(id);
    internal bool CanOpenPlugin(string id) => _canOpenPlugin(id);
    [RelayCommand] private async Task RetrySaveAsync()
    {
        if (_service is null) return;
        try { await _service.FlushAsync(); }
        catch { /* The service retains the error and the original file. */ }
        Refresh();
    }
    internal void Open(string id) => _openPlugin(id);
    internal void Cancel(Guid id) => _service?.Cancel(id);
    internal void CancelBatch(BackgroundTaskSnapshot item)
    {
        foreach (var task in _service?.Snapshot ?? [])
            if (task.PluginId == item.PluginId && task.BatchId == item.BatchId && TaskRow.Active(task.State)) _service!.Cancel(task.Id);
    }
    public void Dispose() { _disposed = true; if (_service is not null) _service.Changed -= OnChanged; }
}

public sealed class TaskRow : ObservableObject
{
    private readonly TasksViewModel _owner;
    public BackgroundTaskSnapshot Snapshot { get; private set; }
    public string PluginName => _owner.PluginName(Snapshot.PluginId);
    public string OpenLabel => _owner.CanOpenPlugin(Snapshot.PluginId) ? "打开" + PluginName : "查看插件";
    public string OpenDescription => _owner.CanOpenPlugin(Snapshot.PluginId)
        ? "打开此任务所属工具：" + PluginName : "此任务所属工具不可用，前往插件管理：" + PluginName;
    public string StateLabel => Snapshot.State switch
    {
        BackgroundTaskState.Queued => "等待中", BackgroundTaskState.Running => "进行中",
        BackgroundTaskState.Cancelling => "正在取消", BackgroundTaskState.Succeeded => "已完成",
        BackgroundTaskState.Failed => "失败", BackgroundTaskState.Cancelled => "已取消", _ => "已中断"
    };
    public static bool Active(BackgroundTaskState state) => state is BackgroundTaskState.Queued or BackgroundTaskState.Running or BackgroundTaskState.Cancelling;
    public bool IsActive => Active(Snapshot.State);
    public bool IsIndeterminate => IsActive && Snapshot.Percent is null;
    public double Progress => Snapshot.Percent ?? 0;
    public bool HasMessage => !string.IsNullOrEmpty(Snapshot.Message);
    public bool HasResult => !string.IsNullOrEmpty(Snapshot.Result);
    public bool HasError => !string.IsNullOrEmpty(Snapshot.Error);
    public string CreatedLabel => Snapshot.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm");
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand CancelBatchCommand { get; }
    public IRelayCommand OpenCommand { get; }
    public TaskRow(BackgroundTaskSnapshot snapshot, TasksViewModel owner)
    {
        Snapshot = snapshot; _owner = owner;
        CancelCommand = new RelayCommand(() => owner.Cancel(Snapshot.Id), () => IsActive);
        CancelBatchCommand = new RelayCommand(() => owner.CancelBatch(Snapshot), () => IsActive);
        OpenCommand = new RelayCommand(() => owner.Open(Snapshot.PluginId));
    }
    public void Update(BackgroundTaskSnapshot snapshot)
    {
        Snapshot = snapshot; OnPropertyChanged(string.Empty);
        CancelCommand.NotifyCanExecuteChanged(); CancelBatchCommand.NotifyCanExecuteChanged();
    }
}
