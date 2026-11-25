using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TractorTool.Contracts;
using TractorTool.Plugins.MediaCompare.Media;

namespace TractorTool.Plugins.MediaCompare.ViewModels;

/// <summary>The plugin owns playback; navigation pages only subscribe to its presentation.</summary>
public sealed partial class CompareViewModel(PluginContext context) : ObservableObject, IAsyncDisposable
{
    private readonly CancellationTokenSource _stopping = CancellationTokenSource.CreateLinkedTokenSource(context.ApplicationStopping);
    private readonly SemaphoreSlim _commands = new(1, 1);
    private ComparisonSession? _session;
    private CancellationTokenSource? _seekCancellation;
    private Task _seekWorker = Task.CompletedTask;
    private Task _navigationPause = Task.CompletedTask;
    private Task? _disposeTask;
    private double? _pendingSeek;
    private int _seekVersion;
    private bool _resumeAfterScrub;
    private long _reportedPosition;
    private int _positionQueued;
    private volatile bool _stopped;

    public event Action<FramePair>? FrameReady;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanOperate))] private bool _isBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PlayLabel))] private bool _isPlaying;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanPlay), nameof(CanSeek))] private bool _hasMedia;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string _error = "";
    [ObservableProperty] private string _status = "选择或拖入 A、B 素材。滚轮缩放，拖动画面联动平移。";
    [ObservableProperty] private string _aName = "A · 未选择素材";
    [ObservableProperty] private string _bName = "B · 未选择素材";
    [ObservableProperty] private string _aDetails = "视频、图片或序列帧";
    [ObservableProperty] private string _bDetails = "视频、图片或序列帧";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PositionText))] private double _positionSeconds;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DurationText), nameof(TimelineMaximum), nameof(CanSeek), nameof(CanPlay))]
    private double _durationSeconds;
    [ObservableProperty] private double _sequenceFrameRate = 24;
    [ObservableProperty] private int _selectedAudio;
    [ObservableProperty] private string _rendererStatus = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanPlay), nameof(CanSeek))] private bool _rendererAvailable;
    public bool IsScrubbing { get; private set; }
    public bool HasError => Error.Length != 0;
    public bool CanOperate => !IsBusy && !_stopped;
    public bool CanPlay => CanOperate && HasMedia && DurationSeconds > 0 && RendererAvailable;
    public bool CanSeek => CanPlay;
    public string PlayLabel => IsPlaying ? "暂停" : "播放";
    public double TimelineMaximum => Math.Max(DurationSeconds, 0.001);
    public string PositionText => FormatTime(PositionSeconds);
    public string DurationText => FormatTime(DurationSeconds);

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPlay)); OnPropertyChanged(nameof(CanSeek));
        TogglePlayCommand.NotifyCanExecuteChanged();
    }
    partial void OnHasMediaChanged(bool value) => TogglePlayCommand.NotifyCanExecuteChanged();
    partial void OnDurationSecondsChanged(double value) => TogglePlayCommand.NotifyCanExecuteChanged();
    partial void OnRendererAvailableChanged(bool value) => TogglePlayCommand.NotifyCanExecuteChanged();

    public async Task OpenAsync(MediaSide side, string path, bool sequence)
    {
        if (!CanOperate) return;
        if (sequence && (!double.IsFinite(SequenceFrameRate) || SequenceFrameRate is < 1 or > 240))
        { ReportError("序列帧帧率必须在 1–240 之间。"); return; }
        IsBusy = true;
        CancelPendingSeek();
        try
        {
            await ExecuteAsync(async (session, token) =>
            {
                Status = "正在打开素材…";
                await session.OpenAsync(side, new(path, sequence, SequenceFrameRate), token);
                await session.SetAudioAsync((AudioSource)SelectedAudio, token);
                RefreshDescription(session);
                Status = "滚轮缩放 · 拖动联动平移 · 空格播放/暂停";
            }, create: true);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private async Task TogglePlayAsync()
    {
        CancelPendingSeek();
        await _seekWorker;
        await ExecuteAsync((session, token) => session.SetPlayingAsync(RendererAvailable && CanOperate && !session.IsPlaying, token));
    }

    public async Task SetAudioAsync(int selected)
    {
        if (selected is < 0 or > 2 || _stopped) return;
        SelectedAudio = selected;
        await ExecuteAsync((session, token) => session.SetAudioAsync((AudioSource)selected, token));
    }

    public void BeginScrub()
    {
        if (!CanSeek || IsScrubbing) return;
        IsScrubbing = true;
        _resumeAfterScrub = IsPlaying;
        _ = ExecuteAsync((session, token) => session.SetPlayingAsync(false, token));
    }

    public async Task EndScrubAsync(double seconds)
    {
        if (!IsScrubbing) return;
        _seekCancellation?.Cancel();
        RequestSeek(seconds);
        IsScrubbing = false;
        var version = _seekVersion;
        await _seekWorker;
        if (_resumeAfterScrub && !_stopped && !IsScrubbing && version == _seekVersion && !HasError)
            await ExecuteAsync((session, token) => session.SetPlayingAsync(RendererAvailable && !IsScrubbing && version == _seekVersion, token));
    }

    public void RequestSeek(double seconds)
    {
        if (_stopped || IsBusy || !RendererAvailable || !HasMedia || !double.IsFinite(seconds)) return;
        PositionSeconds = Math.Clamp(seconds, 0, DurationSeconds);
        _pendingSeek = PositionSeconds;
        _seekVersion++;
        if (_seekWorker.IsCompleted) _seekWorker = ProcessSeeksAsync();
    }

    private async Task ProcessSeeksAsync()
    {
        while (!_stopped && _pendingSeek is not null)
        {
            // Throttle, rather than debounce: continuous pointer movement must still
            // produce previews. Keep one decode in flight and only the latest request.
            try { await Task.Delay(35, _stopping.Token); }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { break; }
            if (_pendingSeek is not { } seconds) break;
            _pendingSeek = null;
            var version = _seekVersion;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            _seekCancellation = cancellation;
            try
            {
                await _commands.WaitAsync(cancellation.Token);
                try
                {
                    if (_session is { } session)
                        await session.SeekAsync(TimeSpan.FromSeconds(seconds), cancellation.Token);
                }
                finally { _commands.Release(); }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex) { if (version == _seekVersion) ReportError("定位失败：" + ex.Message, ex); }
            finally { if (ReferenceEquals(_seekCancellation, cancellation)) _seekCancellation = null; }
        }
    }

    public void LeaveView()
    {
        RendererAvailable = false;
        _navigationPause = PauseForNavigationAsync();
    }

    public void ReportRendererError(string message)
    {
        RendererAvailable = false;
        ReportError(message);
        IsScrubbing = false; _resumeAfterScrub = false;
        CancelPendingSeek();
        _ = ExecuteAsync((session, token) => session.SetPlayingAsync(false, token), clearError: false);
    }

    public async Task EnterViewAsync()
    {
        await _navigationPause;
        await ExecuteAsync((session, token) => session.SeekAsync(session.Position, token));
    }

    private async Task PauseForNavigationAsync()
    {
        IsScrubbing = false; _resumeAfterScrub = false;
        CancelPendingSeek();
        await _seekWorker;
        await ExecuteAsync((session, token) => session.SetPlayingAsync(false, token));
    }

    private void CancelPendingSeek()
    {
        _pendingSeek = null; _seekVersion++;
        _seekCancellation?.Cancel();
    }

    private async Task ExecuteAsync(Func<ComparisonSession, CancellationToken, Task> action, bool create = false, bool clearError = true)
    {
        if (_stopped || (!create && _session is null)) return;
        try
        {
            await _commands.WaitAsync(_stopping.Token);
            try
            {
                if (_stopped) return;
                if (clearError) Error = "";
                if (_session is null)
                {
                    var runtime = Path.Combine(Path.GetDirectoryName(typeof(CompareViewModel).Assembly.Location)!, "Native", "GStreamer");
                    var cache = Path.Combine(context.Paths.GetPluginDataDirectory(context.PluginId), "Cache");
                    _session = await Task.Run(() => new ComparisonSession(runtime, cache), _stopping.Token);
                    _session.FrameReady += ReceiveFrame;
                    _session.Error += OnSessionError;
                    _session.PlayingChanged += OnPlayingChanged;
                }
                await action(_session, _stopping.Token);
            }
            finally { _commands.Release(); }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        catch (Exception ex) { ReportError(ex.Message, ex); }
    }

    private void RefreshDescription(ComparisonSession session)
    {
        AName = session.A?.Name ?? "A · 未选择素材";
        BName = session.B?.Name ?? "B · 未选择素材";
        ADetails = Describe(session.A); BDetails = Describe(session.B);
        DurationSeconds = Math.Max(0, session.Duration.TotalSeconds);
        PositionSeconds = Math.Clamp(session.Position.TotalSeconds, 0, DurationSeconds);
        HasMedia = session.A is not null || session.B is not null;
        IsPlaying = session.IsPlaying;
    }

    private void ReceiveFrame(FramePair pair)
    {
        if (_stopped) { pair.Dispose(); return; }
        Interlocked.Exchange(ref _reportedPosition, pair.Position.Ticks);
        if (Interlocked.Exchange(ref _positionQueued, 1) == 0)
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref _positionQueued, 0);
                if (!_stopped && !IsScrubbing)
                    PositionSeconds = Math.Clamp(TimeSpan.FromTicks(Interlocked.Read(ref _reportedPosition)).TotalSeconds, 0, DurationSeconds);
            });
        var receiver = FrameReady;
        if (receiver is null) pair.Dispose();
        else
        {
            try { receiver(pair); }
            catch (Exception ex) { pair.Dispose(); OnSessionError("预览失败：" + ex.Message); }
        }
    }

    private void OnSessionError(string error) => Dispatcher.UIThread.Post(() => { if (!_stopped) ReportError(error); });
    private void OnPlayingChanged(bool playing) => Dispatcher.UIThread.Post(() => { if (!_stopped) IsPlaying = playing; });
    public void ReportError(string message, Exception? error = null)
    {
        if (_stopped) return;
        Error = message;
        Status = "操作未完成。请查看错误提示，可更换素材后重试。";
        context.Log.Write(AppLogLevel.Error, context.PluginId, message, error);
    }

    public ValueTask DisposeAsync() => new(_disposeTask ??= StopCoreAsync());

    private async Task StopCoreAsync()
    {
        _stopped = true;
        _stopping.Cancel();
        CancelPendingSeek();
        await _seekWorker;
        await _commands.WaitAsync();
        try
        {
            if (_session is { } session)
            {
                session.FrameReady -= ReceiveFrame;
                session.Error -= OnSessionError;
                session.PlayingChanged -= OnPlayingChanged;
                await session.DisposeAsync();
                _session = null;
            }
        }
        finally { _commands.Release(); _stopping.Dispose(); }
    }

    private static string Describe(MediaDescription? media) => media is null ? "视频、图片或序列帧"
        : $"{media.Width} × {media.Height} · {(media.IsStill ? "图片" : $"{media.FrameRate:0.###} fps · {FormatTime(media.Duration.TotalSeconds)}")} · {media.Format}";
    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}");
    }
}
