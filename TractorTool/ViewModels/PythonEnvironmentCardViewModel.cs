using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TractorTool.Contracts.Packages;
using TractorTool.Core.Packages.Python;

namespace TractorTool.ViewModels;

/// <summary>A whole plugin environment is the unit of maintenance, never its individual wheels.</summary>
public sealed class PythonEnvironmentCardViewModel : ObservableObject
{
    private readonly PythonEnvironmentService _service;
    private readonly CancellationToken _stopping;
    private readonly Func<string, string, CancellationToken, Task<bool>> _confirm;
    private PythonEnvironmentListItem _item;
    private string _actionError = "";
    private bool _available = true;
    private bool _acting;
    public string Identity => _item.Definition.Identity;
    public string Name => $"{_item.Definition.PluginName} · Python 环境";
    public string Description => _item.Definition.Description;
    public string Attribution => $"Python {_item.Definition.PythonVersion} · uv {_item.Definition.UvVersion} · {_item.Definition.Rid}";
    public string Source => _item.Definition.Source;
    public string Directory => _item.Directory;
    public string SearchText => $"{Name} {_item.Definition.PluginId} {_item.Definition.Id} {_item.Definition.PluginVersion} {Attribution}";
    public string RequiredBy => "引用插件：" + (_item.RequiredBy.Count == 0 ? "无" : string.Join("、", _item.RequiredBy));
    public string Users => "正在使用：" + (_item.Users.Count == 0 ? "无" : string.Join("、", _item.Users));
    public string IdentityText => $"插件 {_item.Definition.PluginId} {_item.Definition.PluginVersion}\n环境 {_item.Definition.Id}\n安装身份 {Identity}";
    public string SizeText => _item.InstalledBytes is { } size ? $"占用 {size / 1048576d:0.0} MiB（含独立解释器）" : "";
    public string StatusText => _item.Phase switch
    {
        PackagePhase.Ready => "已就绪",
        PackagePhase.Downloading => "正在下载工具链",
        PackagePhase.Verifying => "正在校验",
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

    public PythonEnvironmentCardViewModel(PythonEnvironmentService service, PythonEnvironmentListItem item,
        CancellationToken stopping, Func<string, string, CancellationToken, Task<bool>> confirm)
    {
        _service = service; _item = item; _stopping = stopping; _confirm = confirm;
        InstallCommand = new AsyncRelayCommand(() => ActAsync(false), () => CanInstall);
        RemoveCommand = new AsyncRelayCommand(() => ActAsync(true), () => CanRemove);
        CancelCommand = new RelayCommand(() => _service.Cancel(Identity), () => CanCancel);
    }

    public void Refresh(PythonEnvironmentListItem item) { _item = item; Notify(); }
    public void MarkUnavailable() { _available = false; Notify(); }
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
            var title = remove ? "清理 Python 环境" : "准备 Python 环境";
            var message = remove ? $"将清理 {Name} 及它的工具链，保留插件业务数据。是否继续？" :
                $"将按已保存的锁文件准备 {Name}。\n{Attribution}\n来源：{Source}\n失败时保留原安装记录。是否继续？";
            if (!await _confirm(title, message, _stopping)) return;
            _stopping.ThrowIfCancellationRequested();
            if (remove) await _service.RemoveAsync(Identity);
            else await _service.InstallAsync(Identity, _stopping);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _actionError = ex.Message; }
        finally { _acting = false; Notify(); }
    }
}
