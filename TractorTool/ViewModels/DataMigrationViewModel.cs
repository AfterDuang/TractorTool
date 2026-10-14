using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TractorTool.Contracts;
using TractorTool.Core.Logging;
using TractorTool.Core.Plugins;
using TractorTool.Core.Storage;

namespace TractorTool.ViewModels;

public partial class DataMigrationViewModel : ViewModelBase
{
    private readonly DataRootMigration _migration;
    private readonly PluginManager? _manager;
    private readonly AppLog _log;
    private bool _stopping;
    private Task _operation = Task.CompletedTask;
    public Func<Task<string?>>? PickDirectoryAsync { get; set; }
    public Func<string, Task<bool>>? ConfirmAsync { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanChoose)), NotifyPropertyChangedFor(nameof(CanChangeRequest))]
    private bool _isBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPending)), NotifyPropertyChangedFor(nameof(PendingTarget)), NotifyPropertyChangedFor(nameof(CanRetry)), NotifyPropertyChangedFor(nameof(CanChoose)), NotifyPropertyChangedFor(nameof(CanChangeRequest))]
    private DataMigrationRequest? _pending;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string? _previousDataRoot;
    private readonly string _source;
    public string PendingTarget => Pending?.Target ?? "";
    public bool CanRetry => Pending?.Error is not null;
    public bool HasPending => Pending is not null;
    public bool CanChoose => !_stopping && !IsBusy && !HasPending && _manager is not null;
    public bool CanChangeRequest => !_stopping && !IsBusy && HasPending;

    public DataMigrationViewModel(AppPaths paths, PluginManager? manager, AppLog log)
    {
        _migration = new(paths); _source = paths.DataRoot; _manager = manager; _log = log;
        try { Refresh(); }
        catch (Exception ex) { Status = "无法读取迁移记录：" + ex.Message; }
    }

    [RelayCommand] private Task Choose() => RunAsync(async () =>
    {
        if (_manager is null || HasPending || PickDirectoryAsync is null || ConfirmAsync is null) return;
        var target = await PickDirectoryAsync();
        if (target is null || _stopping) return;
        if (!await ConfirmAsync($"迁移应用数据？\n\n从：{_source}\n到：{target}\n\n下次启动时复制并校验设置、插件数据、依赖包与日志，成功后清理旧业务数据和缓存。目标需为空目录；插件程序位置不变。请从托盘或关于页显式退出后重新打开软件。") || _stopping) return;
        var rejection = await Task.Run(() => _migration.Queue(target, _manager));
        Status = rejection ?? "迁移已安排，下次启动时执行。当前任务可以继续，退出前的最新数据也会迁移。";
        _log.Write(rejection is null ? AppLogLevel.Info : AppLogLevel.Warning, "Storage", Status);
    });
    [RelayCommand] private Task Retry() => RunAsync(async () =>
    {
        await Task.Run(_migration.Retry);
        Status = "下次启动时重试迁移；目标已有不同内容时不会覆盖，请取消后选择新的空目录。";
        _log.Write("Storage", Status);
    });
    [RelayCommand] private Task Cancel() => RunAsync(async () =>
    {
        var target = Pending?.Target;
        await Task.Run(_migration.Cancel);
        Status = "迁移已取消，继续使用当前目录。已经复制到目标的文件仍保留：" + target;
        _log.Write("Storage", Status);
    });

    private Task RunAsync(Func<Task> action)
    {
        if (_stopping || IsBusy) return Task.CompletedTask;
        IsBusy = true;
        return _operation = CompleteAsync(action);
    }
    private async Task CompleteAsync(Func<Task> action)
    {
        try { await action(); Refresh(); }
        catch (Exception ex) { Status = "数据迁移请求未完成：" + ex.Message; _log.Write("Storage", Status, ex); }
        finally { IsBusy = false; }
    }
    private void Refresh()
    {
        var state = _migration.Snapshot;
        Pending = state.Migration;
        PreviousDataRoot = state.PreviousDataRoot;
        if (Pending?.Error is { } error) Status = error;
    }
    public Task BeginShutdown()
    {
        _stopping = true;
        OnPropertyChanged(nameof(CanChoose)); OnPropertyChanged(nameof(CanChangeRequest));
        return _operation;
    }
}
