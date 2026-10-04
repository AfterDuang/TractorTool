using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TractorTool.Contracts.Packages;
using TractorTool.Core.Packages;

namespace TractorTool.ViewModels;

public sealed class ExternalToolsViewModel : IDisposable
{
    private readonly ExternalPackageService? _service;
    private int _queued;
    private bool _disposed;
    public IReadOnlyList<PackageCardViewModel> Cards { get; }
    public bool IsUnsupported => Cards.Count == 0;
    public ExternalToolsViewModel(ExternalPackageService? service)
    {
        _service = service;
        Cards = service?.Definitions.Select(d => new PackageCardViewModel(service, d)).ToArray() ?? [];
        if (service is not null) service.Changed += OnChanged;
    }
    private void OnChanged()
    {
        if (Interlocked.Exchange(ref _queued, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _queued, 0);
            if (_disposed) return;
            foreach (var card in Cards) card.Refresh();
        });
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_service is not null) _service.Changed -= OnChanged;
        foreach (var card in Cards) card.Stop();
    }
}

public sealed class PackageCardViewModel : ObservableObject
{
    private readonly ExternalPackageService _service;
    private readonly PackageDefinition _definition;
    private PackageStatus _state;
    private string _operationError = "";
    private bool _operating;
    private bool _stopping;
    public string Name => _definition.Name;
    public string Description => _definition.Description;
    public string Directory => _state.Directory;
    public string Source => _definition.SourceUri.AbsoluteUri;
    public bool HasSize => _state.InstalledBytes is not null;
    public string SizeText => _state.InstalledBytes is { } size ? $"工具文件 {size / 1048576d:0.0} MiB" : "";
    public string Attribution => $"版本 {_definition.Package.Version} · {_definition.Rid} · {_definition.DownloadUri.Host} · {_definition.License}";
    public string StatusText => _state.Phase switch
    {
        PackagePhase.Missing => "未安装",
        PackagePhase.Downloading => $"正在下载 {_state.DownloadedBytes / 1048576d:0.0} MiB" + (_state.TotalBytes is { } total ? $" / {total / 1048576d:0.0} MiB" : ""),
        PackagePhase.Verifying => "正在校验下载内容",
        PackagePhase.Installing => "正在处理工具文件",
        PackagePhase.Ready => "已安装",
        _ => "需要处理"
    };
    public string Users => _state.Users.Count == 0 ? "" : "正在使用：" + string.Join("、", _state.Users);
    public bool HasUsers => _state.Users.Count != 0;
    public string Error => _operationError.Length != 0 ? _operationError : _state.Error;
    public bool HasError => Error.Length != 0;
    public bool IsBusy => _state.Phase is PackagePhase.Downloading or PackagePhase.Verifying or PackagePhase.Installing;
    public bool IsIndeterminate => _state.Phase != PackagePhase.Downloading || _state.TotalBytes is not > 0;
    public double Progress => _state.TotalBytes is > 0 ? Math.Clamp(100d * _state.DownloadedBytes / _state.TotalBytes.Value, 0, 100) : 0;
    public bool CanInstall => !_stopping && !_operating && _state.Phase is PackagePhase.Missing or PackagePhase.Failed;
    public bool CanRemove => !_stopping && !_operating && !HasUsers && _state.Phase is PackagePhase.Ready or PackagePhase.Failed;
    public bool CanCancel => !_stopping && IsBusy && _state.CanCancel;
    public string InstallLabel => _state.Phase == PackagePhase.Failed ? "重试安装" : "下载安装";
    public IAsyncRelayCommand InstallCommand { get; }
    public IAsyncRelayCommand RemoveCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public PackageCardViewModel(ExternalPackageService service, PackageDefinition definition)
    {
        _service = service; _definition = definition; _state = service.GetStatus(definition.Package);
        InstallCommand = new AsyncRelayCommand(() => ExecuteAsync(() => service.InstallAsync(definition.Package)), () => CanInstall);
        RemoveCommand = new AsyncRelayCommand(() => ExecuteAsync(() => service.RemoveAsync(definition.Package)), () => CanRemove);
        CancelCommand = new RelayCommand(() => service.CancelInstall(definition.Package), () => CanCancel);
    }
    private async Task ExecuteAsync(Func<Task> operation)
    {
        _operating = true; _operationError = ""; Refresh();
        try { await operation(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _operationError = ex.Message; }
        finally { _operating = false; Refresh(); }
    }
    public void Refresh()
    {
        _state = _service.GetStatus(_definition.Package);
        OnPropertyChanged(string.Empty);
        InstallCommand.NotifyCanExecuteChanged(); RemoveCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
    }
    public void Stop() { _stopping = true; Refresh(); }
}
