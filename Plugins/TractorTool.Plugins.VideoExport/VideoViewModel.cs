using System.Collections.ObjectModel;
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
    // Additive field: older preferences acquire one default preset; no input/output paths are persisted.
    public ExportPresetData[] Presets { get; init; } = [new(Guid.NewGuid(), "预设 1", new("mp4", "libx264", 0, 0, 8000, 192, false))];
}

// The plugin instance owns work and settings; pages only display this model.
public partial class VideoViewModel : ObservableObject
{
    private readonly PluginContext _context;
    private static readonly PackageReference MediaPackage = new("FFmpeg", "9.0.2");
    private CancellationTokenSource? _operation, _itemOperation;
    private ExportPreset? _activePreset, _editingPreset;
    private Task _running = Task.CompletedTask, _saving = Task.CompletedTask;
    private VideoSettings? _pendingSettings;
    private bool _saveActive, _settingsLoaded, _stopped;
    private MediaInfo? _media;
    private FileStamp? _readStamp;
    public ObservableCollection<ExportPreset> Presets { get; } = [];
    [ObservableProperty] private bool _isInformation;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSource), nameof(SourceName)), NotifyCanExecuteChangedFor(nameof(ReadCommand), nameof(ExportCommand))]
    private string _sourcePath = "";
    public bool HasSource => SourcePath.Length != 0;
    public string SourceName => HasSource ? Path.GetFileName(SourcePath) : "拖入一个视频文件";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ReadCommand), nameof(CancelCommand), nameof(ExportCommand), nameof(SavePresetCommand)), NotifyPropertyChangedFor(nameof(CanEditPresets))]
    private bool _isBusy;
    public bool CanEditPresets => CanEditSettings && !IsBusy;
    partial void OnIsBusyChanged(bool value) { foreach (var preset in Presets) preset.NotifyEditing(); }
    [ObservableProperty] private string _status = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string _error = "";
    public bool HasError => Error.Length != 0;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasMediaReport))] private string _mediaReport = "";
    public bool HasMediaReport => MediaReport.Length != 0;
    [ObservableProperty] private string _mediaSummary = "";
    [ObservableProperty] private bool _autoReadOnSelect = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSettingsError)), NotifyCanExecuteChangedFor(nameof(RetrySettingsCommand))]
    private string _settingsError = "";
    public bool HasSettingsError => SettingsError.Length != 0;
    public bool CanEditSettings => _settingsLoaded && !_stopped;
    public IReadOnlyList<ExportChoice> Containers => ExportPlan.Containers;
    public IReadOnlyList<ExportChoice> Encoders => ExportPlan.Encoders;
    [ObservableProperty] private ExportChoice _container = ExportPlan.Containers[0];
    [ObservableProperty] private ExportChoice _encoder = ExportPlan.Encoders[0];
    [ObservableProperty] private string _presetName = "预设 2";
    [ObservableProperty] private bool _keepSize = true;
    [ObservableProperty] private decimal? _outputWidth = 1920;
    [ObservableProperty] private decimal? _outputHeight = 1080;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(EstimatedSize))] private decimal? _videoMbps = 8;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(EstimatedSize))] private decimal? _audioKbps = 192;
    [ObservableProperty] private bool _replaceFirstFrame;
    [ObservableProperty] private string _coverPath = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ExportCommand))] private string _outputDirectory = "";
    [ObservableProperty] private string _editorAction = "添加预设";
    [ObservableProperty] private bool _isEditingPreset;
    [ObservableProperty] private bool _isReading;
    public bool HasPresets => Presets.Count > 0;
    public string ExportLabel => $"导出选中版本（{Presets.Count(p => p.Selected)}）";
    public string EstimatedSize => VideoMbps is > 0 and <= 200 && AudioKbps is > 0 and <= 512
        ? Estimate((int)(VideoMbps.Value * 1000), (int)AudioKbps.Value) : "";
    private string Estimate(int video, int audio) => ExportPlan.EstimateBytes(_media, video, audio) is { } bytes
        ? $"预计 {bytes / 1_000_000:0.##} MB" : "读取视频后显示大小预估";

    public VideoViewModel(PluginContext context)
    {
        _context = context;
        try
        {
            var settings = context.Settings.Load<VideoSettings>("preferences");
            if (settings.SchemaVersion != 1 || settings.Presets is null) throw new InvalidDataException("不支持此插件设置格式。");
            var ids = new HashSet<Guid>();
            foreach (var data in settings.Presets)
            {
                if (data is null || data.Id == Guid.Empty || !ids.Add(data.Id) || string.IsNullOrWhiteSpace(data.Name) || data.Name.Length > 80 || data.Options is null)
                    throw new InvalidDataException("预设配置无效，已保留原文件。");
                ExportPlan.ValidateOptions(data.Options);
            }
            foreach (var data in settings.Presets) Presets.Add(new(data, this));
            _autoReadOnSelect = settings.AutoReadOnSelect; _settingsLoaded = true;
            ResetEditor(); UpdateEstimates();
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
        SourcePath = path; MediaReport = ""; MediaSummary = ""; _media = null; _readStamp = null; Error = ""; Status = "尚未读取媒体信息。";
        foreach (var preset in Presets) { preset.OutputPath = ""; preset.Error = ""; preset.Status = "待导出"; preset.Progress = 0; }
        UpdateEstimates();
        if (AutoReadOnSelect || IsInformation) await ReadCommand.ExecuteAsync(null);
    }
    private bool CanRead() => !_stopped && !IsBusy && HasSource;
    private bool CanCancel() => IsBusy && !_stopped;
    private bool CanExport() => CanRead() && OutputDirectory.Length != 0 && Presets.Any(p => p.Selected);
    [RelayCommand(CanExecute = nameof(CanCancel))] private void Cancel() => _operation?.Cancel();
    public void CancelPreset(ExportPreset preset) { if (ReferenceEquals(_activePreset, preset)) _itemOperation?.Cancel(); }
    [RelayCommand(CanExecute = nameof(CanRead))]
    private Task ReadAsync() => RunOperationAsync(async token =>
    {
        IsReading = true; MediaReport = ""; MediaSummary = ""; _media = null; _readStamp = null; UpdateEstimates();
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
        ex => _context.Log.Write(_context.PluginId, $"导出临时文件清理未完成（{ex.GetType().Name}）。"));
    private void SetMedia(MediaInfo info, FileStamp source)
    {
        _media = info; _readStamp = source;
        MediaSummary = string.Join(" · ", info.Fields.Where(f => f.Name is "时长" or "编码尺寸" or "视频编码").Select(f => f.Value));
        MediaReport = string.Join(Environment.NewLine, info.Fields.Select(f => f.Name + " : " + f.Value)) + Environment.NewLine + Environment.NewLine + info.Report;
        UpdateEstimates();
    }
    private void UpdateEstimates()
    {
        OnPropertyChanged(nameof(EstimatedSize));
        foreach (var p in Presets) p.EstimatedSize = Estimate(p.Data.Options.VideoKbps, p.Data.Options.AudioKbps);
    }
    public void PresetsChanged()
    {
        OnPropertyChanged(nameof(ExportLabel)); OnPropertyChanged(nameof(HasPresets)); ExportCommand.NotifyCanExecuteChanged();
        UpdateEstimates(); if (CanEditSettings) QueueSettings();
    }
    [RelayCommand(CanExecute = nameof(CanEditPresets))]
    private void SavePreset()
    {
        try
        {
            var name = PresetName.Trim();
            if (name.Length is 0 or > 80) throw new InvalidDataException("预设名称须为 1–80 个字符。");
            var options = new ExportOptions(Container.Value, Encoder.Value, KeepSize ? 0 : Integer(OutputWidth, "宽度"),
                KeepSize ? 0 : Integer(OutputHeight, "高度"), Integer(VideoMbps * 1000, "视频码率"), Integer(AudioKbps, "音频码率"), ReplaceFirstFrame);
            ExportPlan.ValidateOptions(options);
            if (_editingPreset is { } editing) editing.Update(editing.Snapshot() with { Name = name, Options = options });
            else Presets.Add(new(new(Guid.NewGuid(), name, options), this));
            Error = ""; PresetsChanged(); ResetEditor();
        }
        catch (Exception ex) { Error = ex.Message; }
    }
    public void EditPreset(ExportPreset preset)
    {
        if (!CanEditPresets || !Presets.Contains(preset)) return;
        _editingPreset = preset; var data = preset.Data; PresetName = data.Name;
        Container = Containers.First(c => c.Value == data.Options.Container); Encoder = Encoders.First(c => c.Value == data.Options.Encoder);
        KeepSize = data.Options.Width == 0; OutputWidth = KeepSize ? 1920 : data.Options.Width; OutputHeight = KeepSize ? 1080 : data.Options.Height;
        VideoMbps = data.Options.VideoKbps / 1000m; AudioKbps = data.Options.AudioKbps; ReplaceFirstFrame = data.Options.UseCover;
        IsEditingPreset = true; EditorAction = "保存修改";
    }
    public void CopyPreset(ExportPreset preset)
    {
        if (!CanEditPresets || !Presets.Contains(preset)) return;
        var name = preset.Name.Length > 75 ? preset.Name[..75] : preset.Name;
        Presets.Add(new(preset.Snapshot() with { Id = Guid.NewGuid(), Name = name + " 副本" }, this)); PresetsChanged();
    }
    public void DeletePreset(ExportPreset preset)
    {
        if (!CanEditPresets || !Presets.Remove(preset)) return;
        if (ReferenceEquals(_editingPreset, preset)) ResetEditor(); PresetsChanged();
    }
    [RelayCommand]
    private void ResetEditor()
    { _editingPreset = null; IsEditingPreset = false; EditorAction = "添加预设"; PresetName = $"预设 {Presets.Count + 1}"; }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportAsync() => RunOperationAsync(async token =>
    {
        var source = FileStamp.Read(SourcePath); _readStamp?.Verify();
        var jobs = Presets.Where(p => p.Selected).Select(p => (Card: p, Data: p.Snapshot())).ToArray();
        var cover = CoverPath; var directory = OutputDirectory;
        var coverStamp = jobs.Any(j => j.Data.Options.UseCover) ? FileStamp.Read(cover) : null;
        Status = "正在准备媒体工具…";
        using var lease = await Packages.TryRequestAsync(MediaPackage, token);
        token.ThrowIfCancellationRequested();
        if (lease is null) { Status = "尚未安装媒体工具，可稍后重新导出。"; return; }
        var exporter = Exporter;
        var info = _media ?? await exporter.ProbeAsync(lease.Tools["ffprobe"], source.Path, token);
        token.ThrowIfCancellationRequested(); source.Verify(); SetMedia(info, source);
        foreach (var job in jobs) ExportPlan.Validate(info, job.Data.Options, cover);
        // Keep the shared cover stable across versions. The exporter still owns each temporary copy.
        using var coverLock = coverStamp is null ? null : new FileStream(coverStamp.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        coverStamp?.Verify();
        foreach (var job in jobs) job.Card.Prepare();
        var succeeded = 0; var failed = 0;
        try
        {
            foreach (var job in jobs)
            {
                token.ThrowIfCancellationRequested();
                if (job.Card.CancelRequested) continue;
                using var item = CancellationTokenSource.CreateLinkedTokenSource(token);
                _itemOperation = item; _activePreset = job.Card;
                var card = job.Card;
                void Update(Action action) => Dispatcher.UIThread.Post(() =>
                { if (ReferenceEquals(_itemOperation, item) && !item.IsCancellationRequested && !_stopped) action(); });
                try
                {
                    if (job.Data.Options.UseCover) coverStamp!.Verify();
                    card.Status = "准备导出"; card.IsIndeterminate = true; Status = "正在导出：" + job.Data.Name;
                    card.OutputPath = await exporter.ExportAsync(lease.Tools, source, info, job.Data.Options, cover, directory,
                        value => Update(() => { card.IsIndeterminate = false; card.Progress = value; }),
                        state => Update(() => { card.Status = state; card.IsIndeterminate = true; }), item.Token);
                    card.Progress = 100; card.Status = "已完成"; succeeded++;
                }
                catch (OperationCanceledException) { card.Status = "已取消"; if (token.IsCancellationRequested) throw; }
                catch (Exception ex) { card.Status = "导出失败"; card.Error = ex.Message; failed++; }
                finally { card.CanCancel = false; card.IsIndeterminate = false; _activePreset = null; _itemOperation = null; }
            }
            Status = $"导出结束：{succeeded} 个完成，{failed} 个失败，{jobs.Length - succeeded - failed} 个取消。";
        }
        finally
        {
            foreach (var job in jobs) if (job.Card.CanCancel) { job.Card.CanCancel = false; job.Card.Status = "已取消"; }
        }
    });
    private static int Integer(decimal? value, string name)
    {
        if (value is null || value < 0 || value > int.MaxValue || value != decimal.Truncate(value.Value)) throw new InvalidDataException(name + "须为有效数值。");
        return (int)value.Value;
    }
    private async Task RunOperationAsync(Func<CancellationToken, Task> action)
    {
        if (_stopped || IsBusy) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_context.ApplicationStopping);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _operation = operation; _running = completion.Task; IsBusy = true; Error = "";
        try { await action(operation.Token); }
        catch (OperationCanceledException) { Status = "已取消。"; }
        catch (Exception ex) { Status = "操作未完成。"; Error = ex.Message; _context.Log.Write(_context.PluginId, $"视频操作失败（{ex.GetType().Name}）。"); }
        finally { _operation = null; IsBusy = false; IsReading = false; completion.TrySetResult(); }
    }
    partial void OnAutoReadOnSelectChanged(bool value) { if (CanEditSettings) QueueSettings(); }
    private bool CanRetrySettings() => CanEditSettings && HasSettingsError && !_saveActive;
    [RelayCommand(CanExecute = nameof(CanRetrySettings))] private void RetrySettings() => QueueSettings();
    private void QueueSettings()
    {
        _pendingSettings = new() { AutoReadOnSelect = AutoReadOnSelect, Presets = Presets.Select(p => p.Snapshot()).ToArray() }; SettingsError = "";
        if (_saveActive) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _saving = completion.Task; _saveActive = true; RetrySettingsCommand.NotifyCanExecuteChanged(); _ = SaveAsync(completion);
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
        _stopped = true; _operation?.Cancel(); OnPropertyChanged(nameof(CanEditSettings)); OnPropertyChanged(nameof(CanEditPresets));
        ReadCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged(); ExportCommand.NotifyCanExecuteChanged();
        SavePresetCommand.NotifyCanExecuteChanged(); RetrySettingsCommand.NotifyCanExecuteChanged();
        await Task.WhenAll(_running, _saving);
    }
}
