using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TractorTool.Contracts;
using TractorTool.Contracts.Packages;
using TractorTool.Plugins.MediaCompare.Media;
using TractorTool.Plugins.MediaCompare.Native.Interop;

namespace TractorTool.Plugins.MediaCompare.ViewModels;

/// <summary>The plugin owns playback; navigation pages only subscribe to its presentation.</summary>
public sealed partial class CompareViewModel(PluginContext context) : ObservableObject, IAsyncDisposable
{
    private static readonly PackageReference RuntimePackage = new("TractorTool.MediaCompare.GStreamer", "1.28.9");
    private readonly CancellationTokenSource _stopping = CancellationTokenSource.CreateLinkedTokenSource(context.ApplicationStopping);
    private readonly SemaphoreSlim _commands = new(1, 1);
    private ComparisonSession? _session;
    private CancellationTokenSource? _seekCancellation;
    private Task _seekWorker = Task.CompletedTask;
    private Task _navigationPause = Task.CompletedTask;
    private Task? _disposeTask;
    private double? _pendingSeek;
    private int _seekVersion;
    private long _lastSeekStarted;
    private bool _resumeAfterScrub;
    private long _reportedPosition;
    private int _positionQueued;
    private volatile bool _stopped;

    public event Action<FramePair>? FrameReady;
    public event Action? ViewResetRequested;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanOperate), nameof(ShowStatus))] private bool _isBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PlayLabel))] private bool _isPlaying;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanPlay), nameof(CanSeek), nameof(CanClear), nameof(ShowClear)), NotifyCanExecuteChangedFor(nameof(ClearAllCommand))] private bool _hasMedia;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanClearA))] private bool _hasA;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanClearB))] private bool _hasB;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError), nameof(ShowStatus), nameof(DisplayError), nameof(CanClear), nameof(ShowClear)), NotifyCanExecuteChangedFor(nameof(ClearAllCommand))] private string _error = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError), nameof(ShowStatus), nameof(DisplayError), nameof(CanClear), nameof(ShowClear)), NotifyCanExecuteChangedFor(nameof(ClearAllCommand))] private string _seekError = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError), nameof(ShowStatus), nameof(DisplayError))] private string _rendererError = "";
    [ObservableProperty] private string _status = "选择或拖入 A、B 素材。滚轮缩放，拖动画面联动平移。";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowStatus), nameof(HasDependencyNotice))]
    private string _dependencyNotice = "";
    [ObservableProperty] private string _aName = "未选择素材";
    [ObservableProperty] private string _bName = "未选择素材";
    [ObservableProperty] private string _aDetails = "视频、图片或序列帧";
    [ObservableProperty] private string _bDetails = "视频、图片或序列帧";
    [ObservableProperty] private string _aSummary = "视频、图片或序列帧";
    [ObservableProperty] private string _bSummary = "视频、图片或序列帧";
    [ObservableProperty] private string _aPreviewWarning = "";
    [ObservableProperty] private string _bPreviewWarning = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PositionText))] private double _positionSeconds;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DurationText), nameof(TimelineMaximum), nameof(CanSeek), nameof(CanPlay), nameof(HasTimeline))]
    private double _durationSeconds;
    [ObservableProperty] private double _sequenceFrameRate = 24;
    [ObservableProperty] private int _selectedAudio;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSelectAudio))] private bool _isAudioChanging;
    [ObservableProperty] private string _rendererStatus = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanPlay), nameof(CanSeek))] private bool _rendererAvailable;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanPlay)), NotifyCanExecuteChangedFor(nameof(TogglePlayCommand))]
    private bool _isSeeking;
    private bool _isScrubbing;
    public bool IsScrubbing
    {
        get => _isScrubbing;
        private set
        {
            if (!SetProperty(ref _isScrubbing, value)) return;
            OnPropertyChanged(nameof(CanPlay));
            TogglePlayCommand.NotifyCanExecuteChanged();
        }
    }
    public bool HasError => Error.Length != 0 || SeekError.Length != 0 || RendererError.Length != 0;
    public string DisplayError => string.Join("\n", new[] { RendererError, Error, SeekError }.Where(message => message.Length != 0));
    public bool HasDependencyNotice => DependencyNotice.Length != 0;
    public bool ShowStatus => IsBusy || HasError || HasDependencyNotice;
    public bool HasTimeline => DurationSeconds > 0;
    public bool CanOperate => !IsBusy && !_stopped;
    public bool CanSelectAudio => CanOperate && !IsAudioChanging;
    public bool ShowClear => HasMedia || Error.Length != 0 || SeekError.Length != 0;
    public bool CanClear => CanOperate && ShowClear;
    public bool CanClearA => CanOperate && HasA;
    public bool CanClearB => CanOperate && HasB;
    public bool CanPlay => CanSeek && !IsScrubbing && !IsSeeking;
    public bool CanSeek => CanOperate && HasMedia && DurationSeconds > 0 && RendererAvailable;
    public string PlayLabel => IsPlaying ? "暂停" : "播放";
    public double TimelineMaximum => Math.Max(DurationSeconds, 0.001);
    public string PositionText => FormatTime(PositionSeconds);
    public string DurationText => FormatTime(DurationSeconds);

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPlay)); OnPropertyChanged(nameof(CanSeek));
        OnPropertyChanged(nameof(CanSelectAudio));
        OnPropertyChanged(nameof(CanClear)); OnPropertyChanged(nameof(CanClearA)); OnPropertyChanged(nameof(CanClearB));
        TogglePlayCommand.NotifyCanExecuteChanged();
        ClearAllCommand.NotifyCanExecuteChanged();
    }
    partial void OnHasMediaChanged(bool value) => TogglePlayCommand.NotifyCanExecuteChanged();
    partial void OnDurationSecondsChanged(double value) => TogglePlayCommand.NotifyCanExecuteChanged();
    partial void OnRendererAvailableChanged(bool value)
    {
        if (value) RendererError = "";
        TogglePlayCommand.NotifyCanExecuteChanged();
    }

    public async Task OpenAsync(MediaSide side, string path, MediaSourceKind kind)
    {
        if (!CanOperate) return;
        if (kind == MediaSourceKind.Sequence && (!double.IsFinite(SequenceFrameRate) || SequenceFrameRate is < 1 or > 240))
        { ReportError("序列帧帧率必须在 1–240 之间。"); return; }
        DependencyNotice = "";
        IsBusy = true;
        IsScrubbing = false; _resumeAfterScrub = false;
        CancelPendingSeek();
        try
        {
            await ExecuteAsync(async (session, token) =>
            {
                Status = "正在打开素材…";
                await session.OpenAsync(side, new(path, kind, SequenceFrameRate), token);
                RefreshDescription(session);
                Status = "滚轮缩放 · 拖动联动平移 · 空格播放/暂停";
            }, create: true);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanClear))]
    private Task ClearAllAsync() => ClearAsync(null);

    public async Task ClearAsync(MediaSide? side)
    {
        if (!CanOperate || (side is null ? !ShowClear : side == MediaSide.A ? !HasA : !HasB)) return;
        IsBusy = true;
        IsScrubbing = false; _resumeAfterScrub = false;
        CancelPendingSeek();
        try
        {
            await _seekWorker;
            if (_session is null) Error = SeekError = "";
            else await ExecuteAsync(async (session, token) =>
            {
                Status = "正在清空对比…";
                await session.ClearAsync(side, token);
                RefreshDescription(session);
            });
            if (!HasMedia && Error.Length == 0 && SeekError.Length == 0)
            {
                SequenceFrameRate = 24;
                PositionSeconds = DurationSeconds = 0;
                ViewResetRequested?.Invoke();
            }
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private async Task TogglePlayAsync()
    {
        var playing = !IsPlaying;
        await _seekWorker;
        await ExecuteAsync((session, token) => CanPlay
            ? session.SetPlayingAsync(playing, token) : Task.CompletedTask);
    }

    public async Task SetAudioAsync(int selected)
    {
        if (selected is < 0 or > 2 || selected == SelectedAudio || !CanSelectAudio) return;
        IsAudioChanging = true;
        try
        {
            await ExecuteAsync((session, token) => session.SetAudioAsync((AudioSource)selected, token));
        }
        finally
        {
            // Restore failed selections before accepting another user request.
            SelectedAudio = (int)(_session?.SelectedAudio ?? AudioSource.Muted);
            OnPropertyChanged(nameof(SelectedAudio));
            IsAudioChanging = false;
        }
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
        IsScrubbing = false;
        RequestSeek(seconds);
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
        if (_seekWorker.IsCompleted)
        {
            _seekWorker = ProcessSeeksAsync();
            IsSeeking = true;
        }
    }

    private async Task ProcessSeeksAsync()
    {
        // Publish the worker before any synchronous completion or property notification
        // can submit another request.
        await Task.Yield();
        try
        {
            while (!_stopped && _pendingSeek is not null)
            {
                var version = _seekVersion;
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                _seekCancellation = cancellation;
                try
                {
                    // Limit request starts, not the gap after decoding. Slow decodes already
                    // consume this interval; release/click requests bypass the drag throttle.
                    if (IsScrubbing && _lastSeekStarted != 0)
                    {
                        var remaining = TimeSpan.FromMilliseconds(16) - Stopwatch.GetElapsedTime(_lastSeekStarted);
                        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, cancellation.Token);
                    }
                    await _commands.WaitAsync(cancellation.Token);
                    try
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        if (_pendingSeek is not { } seconds) continue;
                        _pendingSeek = null;
                        version = _seekVersion;
                        if (_session is { } session)
                        {
                            _lastSeekStarted = Stopwatch.GetTimestamp();
                            await session.SeekAsync(TimeSpan.FromSeconds(seconds), cancellation.Token);
                            if (version == _seekVersion)
                            {
                                SeekError = "";
                                if (!IsScrubbing)
                                    PositionSeconds = Math.Clamp(session.Position.TotalSeconds, 0, DurationSeconds);
                            }
                        }
                    }
                    finally { _commands.Release(); }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    if (!_stopped && version == _seekVersion)
                    {
                        SeekError = "定位失败：" + ex.Message;
                        if (!IsScrubbing && _session is { } session)
                            PositionSeconds = Math.Clamp(session.Position.TotalSeconds, 0, DurationSeconds);
                        context.Log.Write(AppLogLevel.Error, context.PluginId, SeekError, ex);
                    }
                }
                finally { if (ReferenceEquals(_seekCancellation, cancellation)) _seekCancellation = null; }
            }
        }
        finally
        {
            // Observers of IsSeeking may synchronously enqueue another position.
            // Mark this drained worker idle before notifying them.
            _seekWorker = Task.CompletedTask;
            IsSeeking = false;
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
        RendererError = message;
        context.Log.Write(AppLogLevel.Error, context.PluginId, message);
        IsScrubbing = false; _resumeAfterScrub = false;
        CancelPendingSeek();
        _ = ExecuteAsync((session, token) => session.SetPlayingAsync(false, token), clearError: false);
    }

    public async Task EnterViewAsync()
    {
        await _navigationPause;
        await ExecuteAsync((session, token) => session.RefreshPreviewAsync(token));
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
        var requestingRuntime = false;
        try
        {
            await _commands.WaitAsync(_stopping.Token);
            try
            {
                if (_stopped) return;
                if (clearError) Error = SeekError = "";
                if (_session is null)
                {
                    Status = "正在准备 GStreamer 运行库…";
                    var packages = context.Packages ?? throw new NotSupportedException("需要支持原生运行库依赖的宿主，请更新 TractorTool。");
                    requestingRuntime = true;
                    using var lease = await packages.TryRequestAsync(RuntimePackage, _stopping.Token);
                    requestingRuntime = false;
                    context.ApplicationStopping.ThrowIfCancellationRequested();
                    _stopping.Token.ThrowIfCancellationRequested();
                    if (lease is null)
                    {
                        DependencyNotice = "GStreamer 运行库准备未完成；再次打开素材即可重试。";
                        return;
                    }
                    var runtime = Path.GetFullPath(lease.Directory);
                    if (!lease.Libraries.TryGetValue("GStreamer", out var library) ||
                        !string.Equals(Path.GetFullPath(library), Path.Combine(runtime, "bin", GstNative.Core), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("GStreamer 运行库入口与插件所需布局不符，请在外部依赖设置中检查或修复。");
                    var cache = Path.Combine(context.Paths.GetPluginDataDirectory(context.PluginId), "Cache");
                    _stopping.Token.ThrowIfCancellationRequested();
                    // Native loads can partially succeed; keep the package protected until host exit.
                    lease.RetainUntilProcessExit();
                    Status = "正在初始化 GStreamer…";
                    _session = await Task.Run(() => new ComparisonSession(runtime, cache), _stopping.Token);
                    _session.FrameReady += ReceiveFrame;
                    _session.Error += OnSessionError;
                    _session.PlayingChanged += OnPlayingChanged;
                }
                await action(_session, _stopping.Token);
            }
            finally { _commands.Release(); }
        }
        // The host can withdraw installation confirmation before the package request finishes.
        catch (Exception) when (requestingRuntime && (context.ApplicationStopping.IsCancellationRequested || _stopping.IsCancellationRequested)) { }
        catch (OperationCanceledException) when (context.ApplicationStopping.IsCancellationRequested || _stopping.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (context.ApplicationStopping.IsCancellationRequested || _stopping.IsCancellationRequested) { }
        catch (OperationCanceledException) when (requestingRuntime)
        {
            DependencyNotice = "运行库准备已取消；再次打开素材即可重试。";
        }
        catch (Exception ex) { ReportError(ex.Message, ex); }
    }

    private void RefreshDescription(ComparisonSession session)
    {
        AName = session.A?.Name ?? "未选择素材";
        BName = session.B?.Name ?? "未选择素材";
        ADetails = Describe(session.A); BDetails = Describe(session.B);
        ASummary = DescribeSummary(session.A); BSummary = DescribeSummary(session.B);
        APreviewWarning = session.A?.PreviewWarning ?? ""; BPreviewWarning = session.B?.PreviewWarning ?? "";
        DurationSeconds = Math.Max(0, session.Duration.TotalSeconds);
        PositionSeconds = Math.Clamp(session.Position.TotalSeconds, 0, DurationSeconds);
        HasA = session.A is not null; HasB = session.B is not null;
        HasMedia = HasA || HasB;
        SelectedAudio = (int)session.SelectedAudio;
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
                if (!_stopped && !IsScrubbing && !IsSeeking)
                    PositionSeconds = Math.Clamp(TimeSpan.FromTicks(Interlocked.Read(ref _reportedPosition)).TotalSeconds, 0, DurationSeconds);
            });
        var receiver = FrameReady;
        if (receiver is null) pair.Dispose();
        else
        {
            try { receiver(pair); }
            catch (Exception ex) { pair.Dispose(); OnSessionError(new InvalidOperationException("预览失败：" + ex.Message, ex)); }
        }
    }

    private void OnSessionError(Exception error) => Dispatcher.UIThread.Post(() => { if (!_stopped) ReportError(error.Message, error); });
    private void OnPlayingChanged(bool playing) => Dispatcher.UIThread.Post(() => { if (!_stopped) IsPlaying = playing; });
    public void ReportError(string message, Exception? error = null)
    {
        if (_stopped) return;
        DependencyNotice = "";
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
        : $"{media.Width} × {media.Height} · {(media.IsStill ? "图片" : $"{DescribeFrameRate(media.FrameRate)} · {FormatTime(media.Duration.TotalSeconds)}")} · {media.Format}" +
            (media.IsStill ? "" : "\n" + media.AudioDescription);
    private static string DescribeSummary(MediaDescription? media) => media is null ? "视频、图片或序列帧"
        : $"{media.Width} × {media.Height} · {(media.IsStill ? "图片" : DescribeFrameRate(media.FrameRate))}";
    private static string DescribeFrameRate(double frameRate) => frameRate > 0 ? $"{frameRate:0.###} fps" : "可变或未标记帧率";
    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}");
    }
}
