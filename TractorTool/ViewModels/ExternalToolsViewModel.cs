using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TractorTool.Contracts.Packages;
using TractorTool.Core.Packages;

namespace TractorTool.ViewModels;

public sealed class ExternalToolsViewModel : ObservableObject, IDisposable
{
    private readonly ExternalPackageService? _service;
    private readonly CancellationTokenSource _stopping = new();
    private int _queued;
    private bool _disposed;
    private bool _cleaning;
    private string _status = "";
    private string _refreshError = "";
    private Func<string, string, CancellationToken, Task<bool>>? _confirm;
    public ObservableCollection<PackageCardViewModel> Cards { get; } = [];
    public bool IsEmpty => Cards.Count == 0;
    public string Summary => $"{Cards.Count(c => c.IsInstalled)} 个已安装 · {Cards.Count(c => c.CanRemove)} 个可清理";
    public string SourceStatus => _service?.SourceStatus ?? "外部依赖服务不可用。";
    public string Error => string.Join("\n", new[] { _service?.SourceError, _service?.ReferenceError, _status, _refreshError }.Where(s => !string.IsNullOrEmpty(s)));
    public bool HasError => Error.Length != 0;
    public IAsyncRelayCommand CleanCommand { get; }
    public IAsyncRelayCommand RefreshSourceCommand { get; }
    public IRelayCommand<PackageCardViewModel> OpenCommand { get; }

    public ExternalToolsViewModel(ExternalPackageService? service, Action<PackageCardViewModel> open)
    {
        _service = service;
        OpenCommand = new RelayCommand<PackageCardViewModel>(card => { if (!_disposed && card is not null) open(card); });
        CleanCommand = new AsyncRelayCommand(CleanAsync, () => !_disposed && !_cleaning && Cards.Any(c => c.CanRemove));
        RefreshSourceCommand = new AsyncRelayCommand(RefreshSourceAsync, CanRefreshSource);
        if (service is not null) service.Changed += OnChanged;
        Refresh();
    }
    public void ConnectConfirmation(Func<string, string, CancellationToken, Task<bool>>? confirm)
    {
        _confirm = confirm;
        if (_service is not null) _service.ConfirmInstallAsync = confirm is null ? null : (prompt, token) => confirm(
            prompt.IsRepair ? "重新安装依赖" : "安装依赖",
            $"{prompt.RequestedBy} 需要 {prompt.Definition.Name} {prompt.Definition.Package.Version}。\n{prompt.Definition.Description}\n\n" +
            $"来源：{prompt.Definition.DownloadUri.Host}\n许可：{prompt.Definition.License}\n" +
            (prompt.IsRepair ? "将替换此版本损坏的工具文件。\n" : "") +
            "安装后供插件共用，卸载插件时会保留。是否继续？", token);
    }
    private void OnChanged()
    {
        if (Interlocked.Exchange(ref _queued, 1) != 0) return;
        Dispatcher.UIThread.Post(() => { Interlocked.Exchange(ref _queued, 0); if (!_disposed) Refresh(); });
    }
    private void Refresh()
    {
        var snapshots = _service?.GetPackages() ?? [];
        foreach (var stale in Cards.Where(c => !snapshots.Any(s => s.Package == c.Key)).ToArray()) Cards.Remove(stale);
        foreach (var snapshot in snapshots)
        {
            var card = Cards.FirstOrDefault(c => c.Key == snapshot.Package);
            if (card is null) Cards.Add(new(_service!, snapshot, _stopping.Token));
            else card.Refresh(snapshot);
        }
        OnPropertyChanged(string.Empty);
        CleanCommand.NotifyCanExecuteChanged(); RefreshSourceCommand.NotifyCanExecuteChanged();
    }
    private bool CanRefreshSource() => !_disposed && _service is { CanRefreshSource: true, IsRefreshingSource: false };
    private async Task RefreshSourceAsync()
    {
        if (!CanRefreshSource()) return;
        _refreshError = ""; Refresh();
        try { await _service!.RefreshSourceAsync(_stopping.Token); }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        catch (Exception ex) { _refreshError = "安装源刷新失败：" + ex.Message; }
        finally { if (!_disposed) Refresh(); }
    }
    private async Task CleanAsync()
    {
        if (_service is null || _confirm is null || _disposed) return;
        var candidates = _service.GetPackages().Where(p => p.CanRemove).ToArray();
        if (candidates.Length == 0) return;
        _cleaning = true; _status = ""; Refresh();
        try
        {
            var size = candidates.Sum(p => p.Status.InstalledBytes ?? 0) / 1048576d;
            if (!await _confirm("清理未引用的依赖", string.Join("\n", candidates.Select(p => $"{p.Definition?.Name ?? p.Package.Id} {p.Package.Version}")) +
                $"\n\n共 {candidates.Length} 个，约 {size:0.0} MiB。以后需要时可以重新下载。", _stopping.Token)) return;
            foreach (var item in candidates)
            {
                _stopping.Token.ThrowIfCancellationRequested();
                // The service rechecks references and leases atomically before each removal.
                if (_service.GetPackages().Any(p => p.Package == item.Package && p.CanRemove))
                    await _service.RemoveAsync(item.Package);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status = "清理未完成：" + ex.Message; }
        finally { _cleaning = false; if (!_disposed) Refresh(); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ConnectConfirmation(null);
        if (_service is not null) _service.Changed -= OnChanged;
        _stopping.Cancel();
        foreach (var card in Cards) card.RefreshStopped();
        CleanCommand.NotifyCanExecuteChanged(); RefreshSourceCommand.NotifyCanExecuteChanged();
    }
}

public sealed class PackageCardViewModel : ObservableObject
{
    private readonly ExternalPackageService _service;
    private readonly CancellationToken _stopping;
    private PackageListItem _item;
    private bool _operating;
    private string _operationError = "";
    public PackageReference Key => _item.Package;
    public string Name => _item.Definition?.Name ?? Key.Id;
    public string Attribution => $"版本 {Key.Version} · {_item.Status.Rid}" + (_item.Definition is { } d ? $" · {d.License}" : "");
    public string Directory => _item.Status.Directory;
    public string Source => _item.Definition?.SourceUri.AbsoluteUri ?? "可信安装源尚未提供此版本。";
    public bool IsInstalled => _item.IsInstalled;
    public string SizeText => _item.Status.InstalledBytes is { } size ? $"{size / 1048576d:0.0} MiB" : "";
    public string RequiredBy => _item.RequiredBy.Count == 0 ? "没有插件引用" : "引用插件：" + string.Join("、", _item.RequiredBy);
    public string Users => _item.Status.Users.Count == 0 ? "" : "正在使用：" + string.Join("、", _item.Status.Users);
    public bool HasUsers => Users.Length != 0;
    public string Error => _operationError.Length != 0 ? _operationError : _item.Status.Error;
    public bool HasError => Error.Length != 0;
    public bool CanRemove => !_stopping.IsCancellationRequested && _item.CanRemove;
    public bool CanInstall => !_stopping.IsCancellationRequested && !_operating && _item.CanInstall;
    public bool CanCancel => !_stopping.IsCancellationRequested && _item.Status.CanCancel;
    public bool IsBusy => _item.Status.Phase is PackagePhase.Downloading or PackagePhase.Verifying or PackagePhase.Installing;
    public bool IsIndeterminate => _item.Status.Phase != PackagePhase.Downloading || _item.Status.TotalBytes is not > 0;
    public double Progress => _item.Status.TotalBytes is > 0 ? Math.Clamp(100d * _item.Status.DownloadedBytes / _item.Status.TotalBytes.Value, 0, 100) : 0;
    public string InstallLabel => IsInstalled ? "重新安装" : "安装";
    public string StatusText => _item.Status.Phase switch
    {
        PackagePhase.Missing => "未安装",
        PackagePhase.Downloading => $"正在下载 {_item.Status.DownloadedBytes / 1048576d:0.0} MiB",
        PackagePhase.Verifying => "正在校验",
        PackagePhase.Installing => "正在处理工具文件",
        PackagePhase.Ready => "已安装",
        _ => "需要处理"
    };
    public IAsyncRelayCommand InstallCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public PackageCardViewModel(ExternalPackageService service, PackageListItem item, CancellationToken stopping)
    {
        _service = service; _item = item; _stopping = stopping;
        InstallCommand = new AsyncRelayCommand(InstallAsync, () => CanInstall);
        CancelCommand = new RelayCommand(() => service.CancelInstall(Key), () => CanCancel);
    }
    private async Task InstallAsync()
    {
        _operating = true; _operationError = ""; RefreshStopped();
        try { using var lease = await _service.TryRequestAsync(Key, "软件设置", _stopping); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _operationError = ex.Message; }
        finally { _operating = false; RefreshStopped(); }
    }
    public void Refresh(PackageListItem item) { _item = item; RefreshStopped(); }
    public void RefreshStopped()
    {
        OnPropertyChanged(string.Empty);
        InstallCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
    }
}
