using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TractorTool.Contracts;
using TractorTool.Contracts.Packages;
using TractorTool.Contracts.Processes;
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
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSource)), NotifyCanExecuteChangedFor(nameof(ReadCommand))]
    private string _sourcePath = "";
    public bool HasSource => SourcePath.Length != 0;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ReadCommand), nameof(CancelCommand))]
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
        SourcePath = path; Fields = []; Error = ""; Status = "尚未读取媒体信息。";
        if (AutoReadOnSelect) await ReadCommand.ExecuteAsync(null);
    }
    private bool CanRead() => !_stopped && !IsBusy && HasSource;
    private bool CanCancel() => IsBusy && !_stopped;
    [RelayCommand(CanExecute = nameof(CanCancel))] private void Cancel() => _operation?.Cancel();
    [RelayCommand(CanExecute = nameof(CanRead))]
    private async Task ReadAsync()
    {
        if (!CanRead()) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_context.ApplicationStopping);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _operation = operation; _running = completion.Task;
        IsBusy = true; Fields = []; Error = "";
        try
        {
            var path = SourcePath;
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new FileNotFoundException("请选择存在的本地视频文件。");
            var packages = _context.Packages ?? throw new NotSupportedException("宿主未提供依赖服务。");
            var processes = _context.Processes ?? throw new NotSupportedException("宿主未提供工具进程服务。");
            Status = "正在准备媒体工具…";
            using var lease = await packages.TryRequestAsync(MediaPackage, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (lease is null) { Status = "尚未安装媒体工具，可稍后重新读取。"; return; }
            if (!File.Exists(path)) throw new FileNotFoundException("所选视频已不存在，请重新选择。");
            var before = new FileInfo(path); var length = before.Length; var modified = before.LastWriteTimeUtc;
            Status = "正在读取媒体信息…";
            var result = await processes.RunAsync(new ProcessRequest(lease.Tools["ffprobe"],
                ["-v", "error", "-show_format", "-show_streams", "-of", "json", "-i", path])
                { Timeout = TimeSpan.FromSeconds(30), OutputLimit = 2 * 1024 * 1024 }, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (result.ExitCode != 0) throw new InvalidDataException("无法读取此视频：" +
                (result.StandardError.Length == 0 ? $"工具退出码 {result.ExitCode}" : result.StandardError[..Math.Min(result.StandardError.Length, 1000)]));
            if (result.StandardOutputTruncated) throw new InvalidDataException("媒体信息超出读取上限，未使用不完整结果。");
            var fields = await Task.Run(() => MediaProbe.Parse(result.StandardOutput), operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            var after = new FileInfo(path);
            if (!after.Exists || after.Length != length || after.LastWriteTimeUtc != modified)
                throw new IOException("读取期间视频发生变化，请重新读取。");
            Fields = fields; Status = "媒体信息已读取。";
        }
        catch (OperationCanceledException) { Status = "已取消读取。"; }
        catch (Exception ex)
        {
            Status = "读取未完成。"; Error = ex.Message;
            // Do not persist filenames, media tags or ffprobe diagnostics in the host log.
            _context.Log.Write(_context.PluginId, $"媒体信息读取失败（{ex.GetType().Name}）。");
        }
        finally { _operation = null; IsBusy = false; completion.TrySetResult(); }
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
        RetrySettingsCommand.NotifyCanExecuteChanged();
        await Task.WhenAll(_running, _saving);
    }
}
