using System.Threading.Channels;
using TractorTool.Plugins.MediaCompare.Native.Interop;

namespace TractorTool.Plugins.MediaCompare.Media;

/// <summary>
/// One worker owns both pipelines. A flushing seek creates a new request generation;
/// only a pair from that generation can be published. Frame callbacks transfer ownership.
/// </summary>
public sealed class ComparisonSession : IAsyncDisposable
{
    private readonly Channel<Command> _commands = Channel.CreateUnbounded<Command>(new()
        { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _worker;
    private readonly nint _clock;
    private GstDecoder? _a, _b;
    private MediaFrame? _currentA, _currentB, _pendingA, _pendingB, _otherCandidate;
    private MediaDescription? _descriptionA, _descriptionB;
    private long _positionTicks, _durationTicks, _seekGeneration;
    private bool _playing;
    private int _disposing;
    private AudioSource _audio = AudioSource.Muted;

    public ComparisonSession(string runtimeRoot, string cacheRoot)
    {
        GstRuntime.EnsureInitialized(runtimeRoot, cacheRoot);
        _clock = GstNative.gst_system_clock_obtain();
        if (_clock == 0) throw new InvalidOperationException("无法创建公共播放时钟。");
        _worker = Task.Run(RunAsync);
    }

    public event Action<FramePair>? FrameReady;
    public event Action<string>? Error;
    public event Action<bool>? PlayingChanged;
    public MediaDescription? A => Volatile.Read(ref _descriptionA);
    public MediaDescription? B => Volatile.Read(ref _descriptionB);
    public TimeSpan Duration => TimeSpan.FromTicks(Interlocked.Read(ref _durationTicks));
    public TimeSpan Position => TimeSpan.FromTicks(Interlocked.Read(ref _positionTicks));
    public bool IsPlaying => Volatile.Read(ref _playing);

    public Task OpenAsync(MediaSide side, MediaSource source, CancellationToken token = default)
        => Enqueue(ct => Open(side, source, ct), token);

    public Task ClearAsync(MediaSide? side = null, CancellationToken token = default)
    {
        if (side is not null && !Enum.IsDefined(side.Value)) throw new ArgumentOutOfRangeException(nameof(side));
        Interlocked.Increment(ref _seekGeneration);
        return Enqueue(ct => Clear(side, ct), token);
    }

    public Task SeekAsync(TimeSpan position, CancellationToken token = default)
    {
        var generation = Interlocked.Increment(ref _seekGeneration);
        return Enqueue(ct =>
        {
            if (generation != Interlocked.Read(ref _seekGeneration)) return;
            Pause();
            Seek(position, ct, generation);
        }, token);
    }

    public Task SetPlayingAsync(bool playing, CancellationToken token = default)
        => Enqueue(ct =>
        {
            Pause();
            if (!playing || Duration <= TimeSpan.Zero || (_a is null && _b is null)) return;
            var generation = Interlocked.Increment(ref _seekGeneration);
            Seek(Position, ct, generation);
            ct.ThrowIfCancellationRequested();
            var baseTime = GstNative.gst_clock_get_time(_clock) + 30_000_000;
            var masterIsA = _a is { IsStill: false };
            _a?.Play(baseTime, synchronizeVideo: masterIsA);
            _b?.Play(baseTime, synchronizeVideo: !masterIsA);
            SetPlaying(true);
        }, token);

    public Task SetAudioAsync(AudioSource source, CancellationToken token = default)
        => Enqueue(_ =>
        {
            if (!Enum.IsDefined(source)) throw new ArgumentOutOfRangeException(nameof(source));
            _audio = source;
            ApplyAudio();
        }, token);

    private Task Enqueue(Action<CancellationToken> action, CancellationToken token)
    {
        if (Volatile.Read(ref _disposing) != 0) return Task.FromException(new ObjectDisposedException(nameof(ComparisonSession)));
        var command = new Command(action, token);
        if (!_commands.Writer.TryWrite(command)) command.Completion.TrySetException(new ObjectDisposedException(nameof(ComparisonSession)));
        return command.Completion.Task;
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                while (_commands.Reader.TryRead(out var command))
                {
                    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(command.Token, _stopping.Token);
                    try
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        command.Action(cancellation.Token);
                        command.Completion.TrySetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        // A canceled operation leaves playback paused; a later seek/open can recover.
                        PauseAfterFailure();
                        command.Completion.TrySetCanceled(cancellation.Token);
                    }
                    catch (Exception error)
                    {
                        PauseAfterFailure();
                        command.Completion.TrySetException(error);
                    }
                }
                if (_stopping.IsCancellationRequested) break;
                if (IsPlaying)
                {
                    try { PumpPlayback(); }
                    catch (Exception error)
                    {
                        PauseAfterFailure();
                        try { Error?.Invoke(error.Message); }
                        catch { /* An observer cannot prevent native resource cleanup. */ }
                    }
                }
                else if (!await _commands.Reader.WaitToReadAsync(_stopping.Token).ConfigureAwait(false)) break;
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        finally
        {
            SetPlaying(false);
            DiscardPendingFrames();
            DisposeFrame(ref _currentA); DisposeFrame(ref _currentB);
            try { _a?.Dispose(); }
            finally
            {
                try { _b?.Dispose(); }
                finally { GstNative.gst_object_unref(_clock); }
            }
            while (_commands.Reader.TryRead(out var command)) command.Completion.TrySetCanceled();
        }
    }

    private void Open(MediaSide side, MediaSource source, CancellationToken token)
    {
        if (!Enum.IsDefined(side)) throw new ArgumentOutOfRangeException(nameof(side));
        Pause();
        var (decoder, first) = GstDecoder.Open(source, _clock, token);
        MediaFrame? candidateFrame = first, otherFrame = null;
        var committed = false;
        try
        {
            var other = side == MediaSide.A ? _b : _a;
            var duration = GetDuration(decoder.Description, other?.Description);
            var position = ClampPosition(Position, duration);
            if (!decoder.IsStill)
            {
                var located = decoder.Seek(position, token);
                candidateFrame.Dispose();
                candidateFrame = located;
            }
            otherFrame = other is null ? null : other.IsStill
                ? (side == MediaSide.A ? _currentB : _currentA)?.Retain()
                : other.Seek(position, token);
            VerifyCoverage(candidateFrame, decoder, position);
            VerifyCoverage(otherFrame, other, position);
            decoder.SetMuted(decoder.IsStill || _audio != (side == MediaSide.A ? AudioSource.A : AudioSource.B));
            token.ThrowIfCancellationRequested();

            // Preparation may seek the other paused pipeline, but it never changes the
            // visible pair or source ownership until both candidate frames are valid.
            var previous = side == MediaSide.A ? _a : _b;
            DiscardPendingFrames();
            DisposeFrame(ref _currentA); DisposeFrame(ref _currentB);
            if (side == MediaSide.A)
            {
                _a = decoder; _currentA = candidateFrame; _currentB = otherFrame;
                Volatile.Write(ref _descriptionA, decoder.Description);
            }
            else
            {
                _b = decoder; _currentB = candidateFrame; _currentA = otherFrame;
                Volatile.Write(ref _descriptionB, decoder.Description);
            }
            candidateFrame = otherFrame = null;
            committed = true;
            Interlocked.Exchange(ref _durationTicks, duration.Ticks);
            Interlocked.Exchange(ref _positionTicks, position.Ticks);
            previous?.Dispose();
            Publish(position);
        }
        finally
        {
            candidateFrame?.Dispose(); otherFrame?.Dispose();
            if (!committed) decoder.Dispose();
        }
    }

    private static TimeSpan GetDuration(MediaDescription? a, MediaDescription? b)
    {
        if (a is not { IsStill: false }) return b is { IsStill: false } ? b.Duration : TimeSpan.Zero;
        return b is { IsStill: false } && b.Duration < a.Duration ? b.Duration : a.Duration;
    }

    private void Clear(MediaSide? side, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Clearing is also the recovery path for a failed pipeline.
        PauseAfterFailure();
        DiscardPendingFrames();
        if (side is null or MediaSide.A)
        {
            DisposeFrame(ref _currentA);
            var decoder = _a; _a = null;
            Volatile.Write(ref _descriptionA, null);
            decoder?.Dispose();
        }
        if (side is null or MediaSide.B)
        {
            DisposeFrame(ref _currentB);
            var decoder = _b; _b = null;
            Volatile.Write(ref _descriptionB, null);
            decoder?.Dispose();
        }
        if (side is null || (_audio == AudioSource.A && _a is null) || (_audio == AudioSource.B && _b is null))
            _audio = AudioSource.Muted;
        var duration = GetDuration(_descriptionA, _descriptionB);
        var position = ClampPosition(Position, duration);
        Interlocked.Exchange(ref _durationTicks, duration.Ticks);
        Interlocked.Exchange(ref _positionTicks, position.Ticks);
        Publish(position);
    }

    private static TimeSpan ClampPosition(TimeSpan requested, TimeSpan duration)
        => duration > TimeSpan.Zero
            ? TimeSpan.FromTicks(Math.Clamp(requested.Ticks, 0, duration.Ticks - 1)) : TimeSpan.Zero;

    private void Seek(TimeSpan requested, CancellationToken token, long generation)
    {
        var position = ClampPosition(requested, Duration);
        DiscardPendingFrames();
        MediaFrame? nextA = null, nextB = null;
        try
        {
            nextA = _a is null ? null : _a.IsStill ? _currentA?.Retain() : _a.Seek(position, token);
            token.ThrowIfCancellationRequested();
            if (generation != Interlocked.Read(ref _seekGeneration)) return;
            nextB = _b is null ? null : _b.IsStill ? _currentB?.Retain() : _b.Seek(position, token);
            token.ThrowIfCancellationRequested();
            if (generation != Interlocked.Read(ref _seekGeneration)) return;
            VerifyCoverage(nextA, _a, position);
            VerifyCoverage(nextB, _b, position);
            DisposeFrame(ref _currentA); DisposeFrame(ref _currentB);
            _currentA = nextA; _currentB = nextB; nextA = nextB = null;
            Interlocked.Exchange(ref _positionTicks, position.Ticks);
            Publish(position);
        }
        finally { nextA?.Dispose(); nextB?.Dispose(); }
    }

    private static void VerifyCoverage(MediaFrame? frame, GstDecoder? decoder, TimeSpan target)
    {
        if (decoder is null || decoder.IsStill) return;
        if (frame is { Duration: var duration } && duration <= TimeSpan.Zero && target > TimeSpan.Zero)
            throw new InvalidDataException("这个位置缺少可靠的帧时间，已暂停；可从开头播放，或改用带有完整帧时间信息的素材。");
        if (frame is null || !FrameTiming.CoversPreroll(frame.Timestamp, frame.Duration, target))
            throw new InvalidDataException("解码帧与请求时间不对应，已停止显示以免比较错帧。");
    }

    private void PumpPlayback()
    {
        // A is the review clock when it moves; with a still A, B advances it instead.
        var masterIsA = _a is { IsStill: false };
        var master = masterIsA ? _a : _b;
        if (master is null || master.IsStill) { Pause(); return; }
        ref var pendingMaster = ref (masterIsA ? ref _pendingA : ref _pendingB);
        ref var currentMaster = ref (masterIsA ? ref _currentA : ref _currentB);
        if (pendingMaster is null)
        {
            pendingMaster = master.ReadSample(15_000_000);
            if (pendingMaster is null)
            {
                if (master.IsEnded) Pause();
                return;
            }
            // pull_sample returns the preroll once more on entering PLAYING.
            if (currentMaster is not null && pendingMaster.Timestamp < currentMaster.Timestamp)
                throw new InvalidDataException("媒体帧时间戳发生倒退，无法保证两路画面对齐。");
            if (currentMaster is not null && pendingMaster.Timestamp == currentMaster.Timestamp)
            { DisposeFrame(ref pendingMaster); return; }
        }
        var target = pendingMaster.Timestamp;
        if (target >= Duration) { Pause(); return; }
        var other = masterIsA ? _b : _a;
        ref var currentOther = ref (masterIsA ? ref _currentB : ref _currentA);
        ref var pendingOther = ref (masterIsA ? ref _pendingB : ref _pendingA);
        if (other is { IsStill: false })
        {
            while (true)
            {
                if (_stopping.IsCancellationRequested || _commands.Reader.TryPeek(out _)) return;
                var candidate = _otherCandidate ?? currentOther;
                var coverage = candidate is null ? FrameCoverage.NeedNext : FrameTiming.Coverage(
                    candidate.Timestamp, candidate.Duration, pendingOther?.Timestamp, target, other.IsEnded);
                if (coverage == FrameCoverage.Covered) break;
                if (coverage == FrameCoverage.Ended) { Pause(); return; }
                if (coverage == FrameCoverage.Gap)
                    throw new InvalidDataException("两路媒体存在不连续时间戳，无法保证当前帧对应。");
                if (coverage == FrameCoverage.NeedNext)
                {
                    pendingOther ??= other.ReadSample(15_000_000);
                    if (pendingOther is null)
                    {
                        if (other.IsEnded) Pause();
                        return;
                    }
                    if (candidate is not null)
                    {
                        if (pendingOther.Timestamp < candidate.Timestamp)
                            throw new InvalidDataException("媒体帧时间戳发生倒退，无法保证两路画面对齐。");
                        // The appsink returns its preroll again when playback starts.
                        if (pendingOther.Timestamp == candidate.Timestamp) { DisposeFrame(ref pendingOther); continue; }
                        continue;
                    }
                }
                if (pendingOther!.Timestamp > target)
                    throw new InvalidDataException("两路媒体存在不连续时间戳，无法保证当前帧对应。");
                DisposeFrame(ref _otherCandidate);
                _otherCandidate = pendingOther; pendingOther = null;
            }
        }
        // Only commit a complete pair. Interruption/clear must retain the last
        // published other frame, not a future candidate read during alignment.
        if (_otherCandidate is not null)
        {
            DisposeFrame(ref currentOther);
            currentOther = _otherCandidate; _otherCandidate = null;
        }
        DisposeFrame(ref currentMaster);
        currentMaster = pendingMaster; pendingMaster = null;
        Interlocked.Exchange(ref _positionTicks, target.Ticks);
        Publish(target);
    }

    private void Publish(TimeSpan position)
    {
        var pair = new FramePair(_currentA?.Retain(), _currentB?.Retain(), position);
        try
        {
            if (FrameReady is { } handler) handler(pair);
            else pair.Dispose();
        }
        catch { pair.Dispose(); throw; }
    }

    private void ApplyAudio()
    {
        _a?.SetMuted(_audio != AudioSource.A || _a.IsStill);
        _b?.SetMuted(_audio != AudioSource.B || _b.IsStill);
    }

    private void Pause()
    {
        try { _a?.Pause(); _b?.Pause(); }
        finally { SetPlaying(false); }
    }
    private void PauseAfterFailure()
    {
        try { _a?.Pause(); } catch { }
        try { _b?.Pause(); } catch { }
        SetPlaying(false);
    }
    private void SetPlaying(bool playing)
    {
        if (Volatile.Read(ref _playing) == playing) return;
        Volatile.Write(ref _playing, playing);
        try { PlayingChanged?.Invoke(playing); }
        catch { /* UI observer failures must not leave decoder threads running. */ }
    }
    private static void DisposeFrame(ref MediaFrame? frame) { frame?.Dispose(); frame = null; }

    private void DiscardPendingFrames()
    {
        DisposeFrame(ref _pendingA); DisposeFrame(ref _pendingB); DisposeFrame(ref _otherCandidate);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposing, 1) == 0)
        {
            _stopping.Cancel();
            _commands.Writer.TryComplete();
        }
        await _worker.ConfigureAwait(false);
        _stopping.Dispose();
    }

    private sealed class Command(Action<CancellationToken> action, CancellationToken token)
    {
        internal Action<CancellationToken> Action { get; } = action;
        internal CancellationToken Token { get; } = token;
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
