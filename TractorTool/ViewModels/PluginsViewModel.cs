using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TractorTool.Contracts;
using TractorTool.Core.Plugins;
using TractorTool.Core.Storage;
using TractorTool.Platform;
using TractorTool.Services;
using TractorTool.UI;

namespace TractorTool.ViewModels;

public partial class PluginsViewModel(AppPaths paths, PluginHost host, IRelayCommand showLogsCommand, PluginManager? manager = null, IAppLog? log = null) : ViewModelBase
{
    private IReadOnlyList<RegisteredPage> _pages = [];
    private IEnumerable<NavigationItem> _navigation = [];
    private bool _stopping;
    private Task _operation = Task.CompletedTask;
    public Func<Task<string?>>? PickArchiveAsync { get; set; }
    public Func<string, Task<bool>>? ConfirmAsync { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanInstall)), NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private bool _managementAvailable = manager is not null;
    public bool CanInstall => ManagementAvailable && !IsBusy && !IsLoading && !_stopping;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanInstall)), NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private bool _isBusy;
    public string PluginRoot => paths.PluginRoot;
    public IRelayCommand ShowLogsCommand { get; } = showLogsCommand;
    public ObservableCollection<PluginCard> Items { get; } = [];
    public ObservableCollection<PluginCard> FilteredItems { get; } = [];
    public bool NoMatches => !IsLoading && !HasDirectoryError && HasItems && !string.IsNullOrWhiteSpace(Search) && !FilteredItems.Any();
    public bool HasItems => Items.Count != 0;
    public bool IsEmpty => !IsLoading && !HasDirectoryError && !HasItems;
    [ObservableProperty] private string _search = "";
    partial void OnSearchChanged(string value) => UpdateFilter();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasOperationStatus))] private string _operationStatus = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasDirectoryError)), NotifyPropertyChangedFor(nameof(IsEmpty)), NotifyPropertyChangedFor(nameof(NoMatches))] private string? _directoryError;
    public bool HasOperationStatus => !string.IsNullOrWhiteSpace(OperationStatus);
    public bool HasDirectoryError => !string.IsNullOrWhiteSpace(DirectoryError);
    [ObservableProperty] private string _summary = "正在检查本地插件…";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsEmpty)), NotifyPropertyChangedFor(nameof(NoMatches)), NotifyPropertyChangedFor(nameof(CanInstall)), NotifyCanExecuteChangedFor(nameof(InstallCommand))] private bool _isLoading = true;
    public void Update(IReadOnlyList<RegisteredPage> pages, IEnumerable<NavigationItem> navigation)
    {
        _pages = pages; _navigation = navigation.ToArray();
        var existing = Items.ToDictionary(p => p.Key, StringComparer.Ordinal);
        List<PluginCard> updated = [];
        void Add(string key, PluginCardState state)
        {
            if (existing.TryGetValue(key, out var card)) card.Apply(state);
            else card = new PluginCard(key, state);
            updated.Add(card);
        }
        var managed = manager?.Snapshot ?? [];
        foreach (var record in host.Records)
        {
            var management = managed.FirstOrDefault(p => p.Id == record.Manifest?.Id ||
                (p.ActiveVersion is not null && string.Equals(record.Directory, Path.Combine(paths.PluginRoot, p.Id, p.ActiveVersion), StringComparison.OrdinalIgnoreCase)));
            var page = pages.FirstOrDefault(p => !p.IsSettings && p.PluginId == record.Manifest?.Id);
            var item = navigation.FirstOrDefault(n => n.Key == page?.PluginId + "/" + page?.Id);
            var state = record.State switch {
                PluginState.Loaded => "已启用", PluginState.Disabled => "已禁用",
                PluginState.Incompatible => "不兼容", PluginState.Invalid => "清单无效",
                PluginState.Duplicate => "版本冲突", PluginState.Failed => "加载失败", _ => "等待加载" };
            var card = new PluginCardState(record.Name, record.Manifest?.Version ?? "未知版本",
                record.Manifest?.Description ?? "无法读取插件信息。", state, record.Detail,
                record.State is not (PluginState.Loaded or PluginState.Disabled),
                page?.Options.Icon ?? ToolIconKind.Tools, item?.OpenCommand) { ImageSource = item?.ImageSource };
            Add("record:" + record.Directory, WithManagement(card, record.Manifest?.Id ?? management?.Id, management, record.State == PluginState.Disabled,
                record.State != PluginState.Duplicate && (record.Manifest is not null || management is not null)));
        }
        foreach (var management in managed.Where(m => !host.Records.Any(r => r.Manifest?.Id == m.Id ||
            (m.ActiveVersion is not null && string.Equals(r.Directory, Path.Combine(paths.PluginRoot, m.Id, m.ActiveVersion), StringComparison.OrdinalIgnoreCase))) &&
            (m.ActiveVersion is not null || m.Pending is not null)))
        {
            Add("managed:" + management.Id, WithManagement(new(management.Name, management.ActiveVersion ?? management.Pending?.Version ?? "",
                "", management.ActiveVersion is null ? "待安装" : "未加载", management.Error,
                management.Error.Length != 0, ToolIconKind.Tools, null), management.Id, management, !management.Enabled, true));
        }
        Synchronize(Items, updated);
        UpdateFilter();
        DirectoryError = host.DirectoryError;
        Summary = $"{Items.Count} 个本地插件 · {host.Records.Count(p => p.State == PluginState.Loaded)} 个已启用";
        IsLoading = false;
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(NoMatches));
    }

    private void UpdateFilter()
    {
        Synchronize(FilteredItems, Items.Where(p => string.IsNullOrWhiteSpace(Search) ||
            p.Name.Contains(Search, StringComparison.CurrentCultureIgnoreCase)).ToArray());
        OnPropertyChanged(nameof(NoMatches));
    }

    private static void Synchronize(ObservableCollection<PluginCard> target, IReadOnlyList<PluginCard> source)
    {
        // Remove only absent rows, then move/insert. Never Reset the ItemsSource on state changes.
        var retained = source.ToHashSet();
        for (var i = target.Count - 1; i >= 0; i--)
            if (!retained.Contains(target[i])) target.RemoveAt(i);
        for (var i = 0; i < source.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], source[i])) continue;
            var current = target.IndexOf(source[i]);
            if (current < 0) target.Insert(i, source[i]);
            else target.Move(current, i);
        }
    }

    private PluginCardState WithManagement(PluginCardState card, string? id, ManagedPlugin? item, bool disabled, bool valid)
    {
        if (!ManagementAvailable || manager is null || id is null || !valid) return card;
        var pending = item?.Pending;
        var enabled = item?.Enabled ?? !disabled;
        var text = pending?.Kind switch {
            PluginChangeKind.Activate => "重启后安装 " + pending.Version, PluginChangeKind.Enable => "重启后启用",
            PluginChangeKind.Disable => "重启后禁用", PluginChangeKind.Uninstall => "重启后卸载（保留数据）",
            PluginChangeKind.Rollback => "重启后回退到 " + item!.Recovery?.Version, _ => "" };
        if (!string.IsNullOrEmpty(item?.Error)) text += (text.Length == 0 ? "" : "\n") + item.Error;
        return card with {
            PendingText = text,
            RequestedEnabled = pending?.Kind == PluginChangeKind.Enable || (pending?.Kind != PluginChangeKind.Disable && enabled),
            ToggleCommand = pending?.Kind is PluginChangeKind.Enable or PluginChangeKind.Disable && item!.Error.Length == 0
                ? new AsyncRelayCommand(() => CancelChangeAsync(id))
                : pending is null && (item is null || item.ActiveVersion is not null)
                    ? new AsyncRelayCommand(() => ChangeAsync(id, enabled ? PluginChangeKind.Disable : PluginChangeKind.Enable)) : null,
            UninstallCommand = pending is null && (item is null || item.ActiveVersion is not null)
                ? new AsyncRelayCommand(() => ChangeAsync(id, PluginChangeKind.Uninstall)) : null,
            RollbackCommand = pending is null && item?.Recovery is not null
                ? new AsyncRelayCommand(() => ChangeAsync(id, PluginChangeKind.Rollback)) : null,
            CancelChangeCommand = pending is not null && (item!.Error.Length == 0 || pending.Kind == PluginChangeKind.Activate)
                ? new AsyncRelayCommand(() => CancelChangeAsync(id)) : null
        };
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private Task InstallAsync() => RunOperationAsync(async () =>
    {
        var file = PickArchiveAsync is null ? null : await PickArchiveAsync();
        if (file is null || _stopping) return;
        await QueueArchiveAsync(file);
    });

    // Picker and native drop share the same trust confirmation, serialization and validation.
    public Task InstallArchiveAsync(string file) => CanInstall
        ? RunOperationAsync(() => QueueArchiveAsync(file)) : Task.CompletedTask;

    private async Task QueueArchiveAsync(string file)
    {
        if (!Path.IsPathFullyQualified(file) || !string.Equals(Path.GetExtension(file), ".zip", StringComparison.OrdinalIgnoreCase))
        { ReportRejection("请选择一个本地 ZIP 插件包。"); return; }
        if (ConfirmAsync is null || !await ConfirmAsync("安装或更新此插件包？插件将在重启后执行，请仅选择可信来源。")) return;
        if (_stopping) return;
        var rejection = await Task.Run(() => manager!.QueueInstall(file, host.Records));
        if (rejection is not null) { ReportRejection(rejection); return; }
        OperationStatus = "插件包已校验，退出并重新打开软件后生效。";
        log?.Write("Plugins", OperationStatus);
    }

    private Task CancelChangeAsync(string id) => RunOperationAsync(async () =>
    {
        await Task.Run(() => manager!.CancelPending(id));
        OperationStatus = "变更已取消；已有版本将在重启后继续使用。";
        log?.Write("Plugins", $"{id}：{OperationStatus}");
    });

    private Task ChangeAsync(string id, PluginChangeKind kind) => RunOperationAsync(async () =>
    {
        var message = kind switch {
            PluginChangeKind.Uninstall => "卸载此插件？重启后移除程序文件，保留插件数据。",
            PluginChangeKind.Rollback => "回退此插件？重启后恢复旧版本及更新前的数据；当前数据另行保留。", _ => "" };
        if (message.Length != 0 && (ConfirmAsync is null || !await ConfirmAsync(message))) return;
        if (_stopping) return;
        var rejection = await Task.Run(() => manager!.QueueChange(id, kind, host.Records));
        if (rejection is not null) { ReportRejection(rejection); return; }
        OperationStatus = "变更已安排，退出并重新打开软件后生效。";
        log?.Write("Plugins", $"{id} · {kind}：{OperationStatus}");
    });

    private void ReportRejection(string reason)
    {
        OperationStatus = reason;
        log?.Write(AppLogLevel.Error, "Plugins", "插件管理请求未执行：" + reason);
    }

    private Task RunOperationAsync(Func<Task> operation)
    {
        if (_stopping || IsBusy || manager is null || !ManagementAvailable) return Task.CompletedTask;
        IsBusy = true;
        return _operation = CompleteOperationAsync(operation);
    }
    private async Task CompleteOperationAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception ex) { log?.Write("Plugins", "插件管理操作失败", ex); OperationStatus = "插件变更失败：" + ex.Message; }
        finally { IsBusy = false; if (!_stopping) Update(_pages, _navigation); }
    }
    public Task BeginShutdown() { _stopping = true; InstallCommand.NotifyCanExecuteChanged(); return _operation; }
    [RelayCommand] private void OpenDirectory()
    {
        try { DesktopActions.OpenDirectory(PluginRoot); OperationStatus = ""; }
        catch (Exception ex) { OperationStatus = "无法打开插件目录：" + ex.Message; }
    }
}
