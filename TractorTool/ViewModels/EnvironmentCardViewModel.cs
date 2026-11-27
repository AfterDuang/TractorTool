using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TractorTool.Contracts.Packages;
using TractorTool.Core.Packages.Node;
using TractorTool.Core.Packages.Python;

namespace TractorTool.ViewModels;

internal sealed record EnvironmentCardSnapshot(string Identity, string Runtime, string RuntimeVersion,
    string Tool, string ToolVersion, string PluginName, string PluginId, string PluginVersion, string EnvironmentId,
    string Rid, string Description, string Source, string Directory, long? InstalledBytes, PackagePhase Phase,
    string Error, IReadOnlyList<string> RequiredBy, IReadOnlyList<string> Users, bool CanInstall, bool CanRemove, bool CanCancel)
{
    internal static EnvironmentCardSnapshot From(PythonEnvironmentListItem item) => new(item.Identity, "Python", item.Definition.PythonVersion,
        "uv", item.Definition.UvVersion, item.Definition.PluginName, item.Definition.PluginId, item.Definition.PluginVersion,
        item.Definition.Id, item.Definition.Rid, item.Definition.Description, item.Definition.Source, item.Directory, item.InstalledBytes,
        item.Phase, item.Error, item.RequiredBy, item.Users, item.CanInstall, item.CanRemove, item.CanCancel);
    internal static EnvironmentCardSnapshot From(NodeEnvironmentListItem item) => new(item.Identity, "Node", item.Definition.NodeVersion,
        "pnpm", item.Definition.PnpmVersion, item.Definition.PluginName, item.Definition.PluginId, item.Definition.PluginVersion,
        item.Definition.Id, item.Definition.Rid, item.Definition.Description, item.Definition.Source, item.Directory, item.InstalledBytes,
        item.Phase, item.Error, item.RequiredBy, item.Users, item.CanInstall, item.CanRemove, item.CanCancel);
}

/// <summary>Language environments share one maintenance UI; the service keeps ownership and lifecycle checks.</summary>
public sealed class EnvironmentCardViewModel : ObservableObject
{
    private readonly Func<string, CancellationToken, Task> _install;
    private readonly Func<string, Task> _remove;
    private readonly Action<string> _cancel;
    private readonly CancellationToken _stopping;
    private readonly Func<string, string, CancellationToken, Task<bool>> _confirm;
    private EnvironmentCardSnapshot _item;
    private string _actionError = "";
    private bool _available = true;
    private bool _acting;
    public string Identity => _item.Identity;
    public int CategoryIndex => _item.Runtime == "Python" ? 2 : 3;
    public string Name => $"{_item.PluginName} · {_item.Runtime} 环境";
    public string Description => _item.Description;
    public string Attribution => $"{_item.Runtime} {_item.RuntimeVersion} · {_item.Tool} {_item.ToolVersion} · {_item.Rid}";
    public string Source => _item.Source;
    public string Directory => _item.Directory;
    public string SearchText => $"{Name} {_item.PluginId} {_item.EnvironmentId} {_item.PluginVersion} {Attribution}";
    public string RequiredBy => "引用插件：" + (_item.RequiredBy.Count == 0 ? "无" : string.Join("、", _item.RequiredBy));
    public string Users => "正在使用：" + (_item.Users.Count == 0 ? "无" : string.Join("、", _item.Users));
    public string IdentityText => $"插件 {_item.PluginId} {_item.PluginVersion}\n环境 {_item.EnvironmentId}\n安装身份 {Identity}";
    public string CleanupName => $"{_item.PluginName} {_item.PluginVersion} · 环境 {_item.EnvironmentId} · {_item.Runtime} {_item.RuntimeVersion}";
    public long InstalledBytes => _item.InstalledBytes ?? 0;
    public string SizeText => _item.InstalledBytes is { } size ? $"占用 {size / 1048576d:0.0} MiB（含独立运行时）" : "";
    public string StatusText => _item.Phase switch
    {
        PackagePhase.Ready => "已就绪",
        PackagePhase.Installing => "正在准备环境",
        PackagePhase.Failed => "准备失败",
        _ => "尚未准备"
    };
    public string Error => string.Join("\n", new[] { _item.Error, _actionError }.Where(p => p.Length != 0));
    public bool HasError => Error.Length != 0;
    public bool IsInstalled => _item.InstalledBytes is not null;
    public bool IsBusy => _item.CanCancel || _acting;
    public bool CanInstall => _available && !_acting && _item.CanInstall;
    public bool CanRemove => _available && !_acting && _item.CanRemove;
    public bool CanCancel => _available && _item.CanCancel;
    public string InstallLabel => IsInstalled ? "修复环境" : "重试准备";
    public IAsyncRelayCommand InstallCommand { get; }
    public IAsyncRelayCommand RemoveCommand { get; }
    public IRelayCommand CancelCommand { get; }

    internal EnvironmentCardViewModel(EnvironmentCardSnapshot item, Func<string, CancellationToken, Task> install,
        Func<string, Task> remove, Action<string> cancel, CancellationToken stopping,
        Func<string, string, CancellationToken, Task<bool>> confirm)
    {
        _item = item; _install = install; _remove = remove; _cancel = cancel; _stopping = stopping; _confirm = confirm;
        InstallCommand = new AsyncRelayCommand(() => ActAsync(false), () => CanInstall);
        RemoveCommand = new AsyncRelayCommand(() => ActAsync(true), () => CanRemove);
        CancelCommand = new RelayCommand(() => _cancel(Identity), () => CanCancel);
    }

    internal void Refresh(EnvironmentCardSnapshot item) { _item = item; Notify(); }
    public void MarkUnavailable() { _available = false; Notify(); }
    // Batch cleanup reuses the exact confirmed card; the service rechecks references and leases atomically.
    internal Task RemoveAsync() => _remove(Identity);
    private void Notify()
    {
        OnPropertyChanged(string.Empty);
        InstallCommand.NotifyCanExecuteChanged(); RemoveCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
    }
    private async Task ActAsync(bool remove)
    {
        if (!(remove ? CanRemove : CanInstall)) return;
        _acting = true; _actionError = ""; Notify();
        try
        {
            var title = remove ? $"清理 {_item.Runtime} 环境" : $"准备 {_item.Runtime} 环境";
            var message = remove ? $"将清理 {Name} 及它的工具链，保留插件业务数据。是否继续？" :
                $"将按已保存的锁文件准备 {Name}。\n{Attribution}\n来源：{Source}\n失败时保留原安装记录。是否继续？";
            if (!await _confirm(title, message, _stopping)) return;
            _stopping.ThrowIfCancellationRequested();
            if (remove) await RemoveAsync();
            else await _install(Identity, _stopping);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _actionError = ex.Message; }
        finally { _acting = false; Notify(); }
    }
}
