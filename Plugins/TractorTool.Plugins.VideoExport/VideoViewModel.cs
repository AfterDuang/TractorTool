using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TractorTool.Contracts;
using TractorTool.Contracts.Packages;
namespace TractorTool.Plugins.VideoExport;

public sealed record VideoSettings : IVersionedSettings
{
    public int SchemaVersion { get; init; } = 1;
    public bool AutoReadOnSelect { get; init; } = true;
}

// This model belongs to the plugin instance. Navigation only detaches its views.
public partial class VideoViewModel : ObservableObject
{
    private readonly PluginContext _context;
    private static readonly PackageReference MediaPackage = new("FFmpeg", "9.0.2");
    private CancellationTokenSource? _operation;
    private Task _running = Task.CompletedTask;
    private Task _saving = Task.CompletedTask;
    private VideoSettings? _pendingSettings;
    private bool _saveActive;
    private bool _settingsLoaded;
    private bool _stopped;
    private MediaInfo? _media;
    private FileStamp? _readStamp;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSource)), NotifyCanExecuteChangedFor(nameof(ReadCommand), nameof(ExportCommand))]
    private string _sourcePath = "";
    public bool HasSource => SourcePath.Length != 0;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ReadCommand), nameof(CancelCommand), nameof(ExportCommand)), NotifyPropertyChangedFor(nameof(IsIndeterminate))]
    private bool _isBusy;
    [ObservableProperty] private string _status = "选择一个视频以读取媒体信息。";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string _error = "";
    public bool HasError => Error.Length != 0;
    [ObservableProperty] private IReadOnlyList<MediaField> _fields = [];
    [ObservableProperty] private bool _autoReadOnSelect = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSettingsError)), NotifyCanExecuteChangedFor(nameof(RetrySettingsCommand))]
    private string _settingsError = "";
    public bool HasSettingsError => SettingsError.Length != 0;
    public bool CanEditSettings => _settingsLoaded && !_stopped;
    public IReadOnlyList<ExportChoice> Containers => ExportPlan.Containers;
    public IReadOnlyList<ExportChoice> Encoders => ExportPlan.Encoders;
    [ObservableProperty] private ExportChoice _container = ExportPlan.Containers[0];
    [ObservableProperty] private ExportChoice _encoder = ExportPlan.Encoders[0];
    [ObservableProperty] private bool _keepSize = true;
    [ObservableProperty] private decimal? _outputWidth = 1920;
    [ObservableProperty] private decimal? _outputHeight = 1080;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(EstimatedSize))] private decimal? _videoMbps = 8;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(EstimatedSize))] private decimal? _audioKbps = 192;
    [ObservableProperty] private bool _replaceFirstFrame;
    [ObservableProperty] private string _coverPath = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ExportCommand))] private string _outputDirectory = "";
    [ObservableProperty] private string _outputPath = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIndeterminate))] private bool _isEncoding;
    public bool IsIndeterminate => IsBusy && !IsEncoding;
    public string EstimatedSize => VideoMbps is > 0 and <= 200 && AudioKbps is > 0 and <= 512 &&
        ExportPlan.EstimateBytes(_media, (int)(VideoMbps.Value * 1000), (int)AudioKbps.Value) is { } bytes
        ? $"预计 {bytes / 1_000_000:0.##} MB，实际大小可能不同。" : "读取媒体信息后显示预计大小。";

    public VideoViewModel(PluginContext context)
    {
        _context = context;
        try
        {
            var settings = context.Settings.Load<VideoSettings>("preferences");
            if (settings.SchemaVersion != 1) throw new InvalidDataException("不支持此插件设置版本。");
            _autoReadOnSelect = settings.AutoReadOnSelect; _settingsLoaded = true;
        }
        catch (Exception ex)
        {
            SettingsError = "设置读取失败，已保留原文件：" + ex.Message;
            context.Log.Write(context.PluginId, "视频偏好读取失败", ex);
        }
    }
    public async Task SelectSourceAsync(string path)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_stopped || IsBusy) return;
        SourcePath = path; Fields = []; _media = null; _readStamp = null; OutputPath = ""; Error = ""; Status = "尚未读取媒体信息。";
        OnPropertyChanged(nameof(EstimatedSize));
        if (AutoReadOnSelect) await ReadCommand.ExecuteAsync(null);
    }
    private bool CanRead() => !_stopped && !IsBusy && HasSource;
    private bool CanCancel() => IsBusy && !_stopped;
    private bool CanExport() => CanRead() && OutputDirectory.Length != 0;
    [RelayCommand(CanExecute = nameof(CanCancel))] private void Cancel() => _operation?.Cancel();
    [RelayCommand(CanExecute = nameof(CanRead))]
    private Task ReadAsync() => RunOperationAsync(async token =>
    {
        Fields = []; _media = null; _readStamp = null; OnPropertyChanged(nameof(EstimatedSize));
        var source = FileStamp.Read(SourcePath);
        Status = "正在准备媒体工具…";
        using var lease = await Packages.TryRequestAsync(MediaPackage, token);
        token.ThrowIfCancellationRequested();
        if (lease is null) { Status = "尚未安装媒体工具，可稍后重新读取。"; return; }
        Status = "正在读取媒体信息…";
        var info = await Exporter.ProbeAsync(lease.Tools["ffprobe"], source.Path, token);
        token.ThrowIfCancellationRequested(); source.Verify();
        SetMedia(info, source); Status = "媒体信息已读取。";
    });

    private IExternalPackageService Packages => _context.Packages ?? throw new NotSupportedException("宿主未提供依赖服务。");
    private VideoExporter Exporter => new(_context.Processes ?? throw new NotSupportedException("宿主未提供工具进程服务。"),
        ex => _context.Log.Write(_context.PluginId, $"本次导出临时文件清理未完成（{ex.GetType().Name}），未影响其他文件。"));
    private void SetMedia(MediaInfo info, FileStamp source)
    { _media = info; _readStamp = source; Fields = info.Fields; OnPropertyChanged(nameof(EstimatedSize)); }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportAsync() => RunOperationAsync(async token =>
    {
        var source = FileStamp.Read(SourcePath);
        _readStamp?.Verify();
        var options = new ExportOptions(Container.Value, Encoder.Value,
            KeepSize ? 0 : Integer(OutputWidth, "宽度"), KeepSize ? 0 : Integer(OutputHeight, "高度"),
            Integer(VideoMbps * 1000, "视频码率"), Integer(AudioKbps, "音频码率"), ReplaceFirstFrame);
        var cover = CoverPath; var directory = OutputDirectory;
        OutputPath = ""; Progress = 0;
        Status = "正在准备媒体工具…";
        using var lease = await Packages.TryRequestAsync(MediaPackage, token);
        token.ThrowIfCancellationRequested();
        if (lease is null) { Status = "尚未安装媒体工具，可稍后重新导出。"; return; }
        var exporter = Exporter;
        var info = _media ?? await exporter.ProbeAsync(lease.Tools["ffprobe"], source.Path, token);
        token.ThrowIfCancellationRequested(); source.Verify(); SetMedia(info, source);
        var currentOperation = _operation;
        void Update(Action action) => Dispatcher.UIThread.Post(() =>
        { if (ReferenceEquals(_operation, currentOperation) && !token.IsCancellationRequested && !_stopped) action(); });
        OutputPath = await exporter.ExportAsync(lease.Tools, source, info, options, cover, directory,
            value => Update(() => { IsEncoding = true; Progress = value; }),
            state => Update(() => { Status = state; IsEncoding = false; }), token);
        Progress = 100; Status = "导出完成。";
    });

    private static int Integer(decimal? value, string name)
    {
        if (value is null || value < 0 || value > int.MaxValue || value != decimal.Truncate(value.Value))
            throw new InvalidDataException(name + "须为有效数值。");
        return (int)value.Value;
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> action)
    {
        if (_stopped || IsBusy) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_context.ApplicationStopping);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _operation = operation; _running = completion.Task;
        IsBusy = true; IsEncoding = false; Error = "";
        try
        {
            await action(operation.Token);
        }
        catch (OperationCanceledException) { Status = "已取消。"; }
        catch (Exception ex)
        {
            Status = "操作未完成。"; Error = ex.Message;
            // Do not persist filenames, media tags or ffprobe diagnostics in the host log.
            _context.Log.Write(_context.PluginId, $"视频操作失败（{ex.GetType().Name}）。");
        }
        finally { _operation = null; IsBusy = false; IsEncoding = false; completion.TrySetResult(); }
    }
    partial void OnAutoReadOnSelectChanged(bool value)
    { if (CanEditSettings) QueueSettings(); }
    private bool CanRetrySettings() => CanEditSettings && HasSettingsError && !_saveActive;
    [RelayCommand(CanExecute = nameof(CanRetrySettings))] private void RetrySettings() => QueueSettings();
    private void QueueSettings()
    {
        _pendingSettings = new() { AutoReadOnSelect = AutoReadOnSelect }; SettingsError = "";
        if (_saveActive) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _saving = completion.Task; _saveActive = true; RetrySettingsCommand.NotifyCanExecuteChanged();
        _ = SaveAsync(completion);
    }
    private async Task SaveAsync(TaskCompletionSource completion)
    {
        try
        {
            while (_pendingSettings is { } snapshot)
            {
                _pendingSettings = null;
                try { await Task.Run(() => _context.Settings.Save("preferences", snapshot)); }
                catch (Exception ex)
                {
                    _context.Log.Write(_context.PluginId, "视频偏好保存失败", ex);
                    if (_pendingSettings is null) SettingsError = "当前选择未保存：" + ex.Message;
                }
            }
        }
        finally { _saveActive = false; RetrySettingsCommand.NotifyCanExecuteChanged(); completion.TrySetResult(); }
    }
    public async Task StopAsync()
    {
        _stopped = true; _operation?.Cancel();
        OnPropertyChanged(nameof(CanEditSettings)); ReadCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
        RetrySettingsCommand.NotifyCanExecuteChanged();
        await Task.WhenAll(_running, _saving);
    }
}
