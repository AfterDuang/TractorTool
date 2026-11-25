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
    private MediaFrame? _currentA, _currentB, _pendingA, _pendingB, _followingA, _followingB;
    private MediaDescription? _descriptionA, _descriptionB;
    private long _positionNanoseconds, _durationTicks, _seekGeneration, _playbackStart;
    private ulong _playbackBaseTime;
    private bool _playing, _ended;
    private int _disposing;
    private int _audio = (int)AudioSource.Muted;

    public ComparisonSession(string runtimeRoot, string cacheRoot)
    {
        GstRuntime.EnsureInitialized(runtimeRoot, cacheRoot);
        _clock = GstNative.gst_system_clock_obtain();
        if (_clock == 0) throw new InvalidOperationException("无法创建公共播放时钟。");
        _worker = Task.Run(RunAsync);
    }

    public event Action<FramePair>? FrameReady;
    public event Action<Exception>? Error;
    public event Action<bool>? PlayingChanged;
    public MediaDescription? A => Volatile.Read(ref _descriptionA);
    public MediaDescription? B => Volatile.Read(ref _descriptionB);
    public TimeSpan Duration => TimeSpan.FromTicks(Interlocked.Read(ref _durationTicks));
    public TimeSpan Position => TimeSpan.FromTicks(Interlocked.Read(ref _positionNanoseconds) / 100);
    public bool IsPlaying => Volatile.Read(ref _playing);
    public AudioSource SelectedAudio => (AudioSource)Volatile.Read(ref _audio);

    public Task OpenAsync(MediaSide side, MediaSource source, CancellationToken token = default)
        => Enqueue(ct => Open(side, source, ct), token);

    public Task RefreshPreviewAsync(CancellationToken token = default)
        => Enqueue(_ => Publish(Interlocked.Read(ref _positionNanoseconds)), token);

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
            var ticks = Math.Clamp(position.Ticks, 0, Duration.Ticks);
            Seek(checked(ticks * 100), ct, generation);
        }, token);
    }

    public Task SetPlayingAsync(bool playing, CancellationToken token = default)
        => Enqueue(ct =>
        {
            if (playing && IsPlaying) return;
            Pause(capturePosition: !playing);
            if (!playing || Duration <= TimeSpan.Zero || (_a is null && _b is null)) return;
            var generation = Interlocked.Increment(ref _seekGeneration);
            if (!Seek(_ended ? 0 : Interlocked.Read(ref _positionNanoseconds), ct, generation)) return;
            ct.ThrowIfCancellationRequested();
            _playbackStart = Interlocked.Read(ref _positionNanoseconds);
            _playbackBaseTime = GstNative.gst_clock_get_time(_clock) + 30_000_000;
            _a?.Play(_playbackBaseTime);
            _b?.Play(_playbackBaseTime);
            SetPlaying(true);
        }, token);

    public Task SetAudioAsync(AudioSource source, CancellationToken token = default)
        => Enqueue(_ =>
        {
            if (!Enum.IsDefined(source)) throw new ArgumentOutOfRangeException(nameof(source));
            // Validate first, then mute the old side before enabling the new one.
            // Never play both sources during a switch or commit an invalid choice.
            var selected = source switch { AudioSource.A => _a, AudioSource.B => _b, _ => null };
            if (source != AudioSource.Muted)
            {
                if (selected is null) throw new InvalidOperationException("请先打开这一侧的素材。");
                selected.EnsureAudioPlayable();
            }
            if (source != AudioSource.A) _a?.SetMuted(true);
            if (source != AudioSource.B) _b?.SetMuted(true);
            selected?.SetMuted(false);
            Volatile.Write(ref _audio, (int)source);
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
                    try { await PumpPlaybackAsync().ConfigureAwait(false); }
                    catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
                    catch (Exception error)
                    {
                        PauseAfterFailure();
                        try { Error?.Invoke(error); }
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
        Pause(capturePosition: true);
        var (decoder, first) = GstDecoder.Open(source, _clock, token);
        using var initialFrame = first;
        MediaFrame? candidateFrame = null, otherFrame = null;
        var committed = false;
        try
        {
            var other = side == MediaSide.A ? _b : _a;
            var duration = GetDuration(decoder.Description, other?.Description);
            var prepared = PrepareFrames(side == MediaSide.A ? decoder : _a, side == MediaSide.B ? decoder : _b,
                side == MediaSide.A ? first : _currentA, side == MediaSide.B ? first : _currentB,
                Interlocked.Read(ref _positionNanoseconds), duration, token)!.Value;
            candidateFrame = side == MediaSide.A ? prepared.A : prepared.B;
            otherFrame = side == MediaSide.A ? prepared.B : prepared.A;
            var position = prepared.Position;
            var selectedSide = side == MediaSide.A ? AudioSource.A : AudioSource.B;
            var audio = SelectedAudio == selectedSide && decoder.Description.AudioStatus != MediaAudioStatus.Ready
                ? AudioSource.Muted : SelectedAudio;
            if (audio == selectedSide) decoder.EnsureAudioPlayable();
            decoder.SetMuted(audio != selectedSide);
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
            Volatile.Write(ref _audio, (int)audio);
            _ended = false;
            Interlocked.Exchange(ref _durationTicks, duration.Ticks);
            Interlocked.Exchange(ref _positionNanoseconds, position);
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
        // Capture before dropping either side's interval evidence. The retained
        // source resumes at the actual pause time, not its last frame's PTS.
        var pausedPosition = side is null ? 0 : IsPlaying ? PausedPosition() : Interlocked.Read(ref _positionNanoseconds);
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
        if (side is null || (SelectedAudio == AudioSource.A && _a is null) || (SelectedAudio == AudioSource.B && _b is null))
            Volatile.Write(ref _audio, (int)AudioSource.Muted);
        _ended = false;
        var duration = GetDuration(_descriptionA, _descriptionB);
        var position = ClampPosition(pausedPosition, duration);
        Interlocked.Exchange(ref _durationTicks, duration.Ticks);
        Interlocked.Exchange(ref _positionNanoseconds, position);
        Publish(position);
    }

    private static long ClampPosition(long requestedNanoseconds, TimeSpan duration)
        => duration > TimeSpan.Zero
            ? Math.Clamp(requestedNanoseconds, 0, checked(duration.Ticks * 100) - 1) : 0;

    private bool Seek(long requestedNanoseconds, CancellationToken token, long generation)
    {
        DiscardPendingFrames();
        var prepared = PrepareFrames(_a, _b, _currentA, _currentB, requestedNanoseconds, Duration, token, generation);
        if (prepared is not { } frames) return false;
        DisposeFrame(ref _currentA); DisposeFrame(ref _currentB);
        _currentA = frames.A; _currentB = frames.B;
        _ended = false;
        Interlocked.Exchange(ref _positionNanoseconds, frames.Position);
        Publish(frames.Position);
        return true;
    }

    private (MediaFrame? A, MediaFrame? B, long Position)? PrepareFrames(
        GstDecoder? a, GstDecoder? b, MediaFrame? heldA, MediaFrame? heldB,
        long requested, TimeSpan duration, CancellationToken token, long? generation = null)
    {
        var position = ClampPosition(requested, duration);
        MediaFrame? nextA = null, nextB = null;
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                nextA = a is null ? null : a.IsStill ? heldA?.Retain() : a.Seek(position, token);
                token.ThrowIfCancellationRequested();
                if (generation is { } current && current != Interlocked.Read(ref _seekGeneration)) return null;
                nextB = b is null ? null : b.IsStill ? heldB?.Retain() : b.Seek(position, token);
                token.ThrowIfCancellationRequested();
                if (generation is { } latest && latest != Interlocked.Read(ref _seekGeneration)) return null;
                if (attempt == 0)
                {
                    var actual = Math.Max(ReachablePosition(nextA, a, position), ReachablePosition(nextB, b, position));
                    if (actual != position)
                    {
                        if (actual != ClampPosition(actual, duration))
                            throw new InvalidDataException("原生定位落在两路共同可播放区间之外，已保留原画面。");
                        // Re-anchor both pipelines (including audio) once to the actual
                        // shared time. A second non-covering result fails, never loops.
                        DisposeFrame(ref nextA); DisposeFrame(ref nextB);
                        position = actual;
                        continue;
                    }
                }
                VerifyCoverage(nextA, a, position);
                VerifyCoverage(nextB, b, position);
                token.ThrowIfCancellationRequested();
                if (generation is { } ready && ready != Interlocked.Read(ref _seekGeneration)) return null;
                var result = (nextA, nextB, position);
                nextA = nextB = null;
                return result;
            }
            throw new InvalidDataException("无法取得两路共同的定位画面。");
        }
        finally { nextA?.Dispose(); nextB?.Dispose(); }
    }

    private static long ReachablePosition(MediaFrame? frame, GstDecoder? decoder, long requested)
        => decoder is null || decoder.IsStill || frame is null ? requested :
            FrameTiming.ResolveSeekPosition(requested, frame.TimestampNanoseconds, frame.EndNanoseconds, decoder.Description.FrameRate);

    private static void VerifyCoverage(MediaFrame? frame, GstDecoder? decoder, long target)
    {
        if (decoder is null || decoder.IsStill) return;
        if (frame is { PrerollEndNanoseconds: null } && target > 0)
            throw new InvalidDataException("这个位置缺少可靠的帧时间，已暂停；可从开头播放，或改用带有完整帧时间信息的素材。");
        if (frame is null || !FrameTiming.CoversPreroll(frame.TimestampNanoseconds, frame.PrerollEndNanoseconds, target))
            throw new InvalidDataException("解码帧与请求时间不对应，已停止显示以免比较错帧。");
    }

    private async Task PumpPlaybackAsync()
    {
        // The union of both streams' real presentation times drives the preview.
        // A slow/still left source must not suppress updates from the right source.
        var readyA = ReadNext(_a, _currentA, ref _pendingA);
        var readyB = ReadNext(_b, _currentB, ref _pendingB);
        if (!readyA || !readyB) return;

        var rateA = (_a?.FrameRateNumerator ?? 0, _a?.FrameRateDenominator ?? 0);
        var rateB = (_b?.FrameRateNumerator ?? 0, _b?.FrameRateDenominator ?? 0);
        if (_pendingA is { } a && _pendingB is { } b &&
            ((a.TimestampNanoseconds < b.TimestampNanoseconds && a.EndNanoseconds is null) ||
             (b.TimestampNanoseconds < a.TimestampNanoseconds && b.EndNanoseconds is null)) &&
            FrameTiming.ShareGridBoundary(a.TimestampNanoseconds, b.TimestampNanoseconds, rateA, rateB))
        {
            // A nominal grid is not proof that an unknown-duration frame lasts
            // even one more nanosecond. Obtain its actual successor before merging.
            if (a.TimestampNanoseconds < b.TimestampNanoseconds && a.EndNanoseconds is null &&
                !ReadNext(_a, a, ref _followingA)) return;
            if (b.TimestampNanoseconds < a.TimestampNanoseconds && b.EndNanoseconds is null &&
                !ReadNext(_b, b, ref _followingB)) return;
        }

        var target = FrameTiming.NextPresentation(_pendingA?.TimestampNanoseconds, _pendingB?.TimestampNanoseconds,
            rateA, rateB, FrameTiming.CoverageEnd(_pendingA?.EndNanoseconds, _followingA?.TimestampNanoseconds),
            FrameTiming.CoverageEnd(_pendingB?.EndNanoseconds, _followingB?.TimestampNanoseconds));
        var end = Math.Min(checked(Duration.Ticks * 100),
            Math.Min(PlaybackEnd(_a, _currentA, _pendingA), PlaybackEnd(_b, _currentB, _pendingB)));
        if (target is null || target >= end)
        {
            if (!await WaitForPresentationAsync(end).ConfigureAwait(false)) return;
            _ended = true;
            Pause();
            return;
        }

        if (!await WaitForPresentationAsync(target.Value).ConfigureAwait(false)) return;
        VerifyPlaybackFrame(_a, _currentA, _pendingA, _followingA, target.Value);
        VerifyPlaybackFrame(_b, _currentB, _pendingB, _followingB, target.Value);
        // Preparation never replaces the last published pair. A queued seek,
        // clear or cancellation therefore cannot expose a half-advanced pair.
        CommitPlaybackFrame(ref _currentA, ref _pendingA, ref _followingA, target.Value);
        CommitPlaybackFrame(ref _currentB, ref _pendingB, ref _followingB, target.Value);
        Interlocked.Exchange(ref _positionNanoseconds, target.Value);
        Publish(target.Value);
    }

    private static bool ReadNext(GstDecoder? decoder, MediaFrame? current, ref MediaFrame? pending)
    {
        if (decoder is null || decoder.IsStill || pending is not null || decoder.IsEnded) return true;
        pending = decoder.ReadSample(15_000_000);
        if (pending is null) return decoder.IsEnded;
        if (current is not null && pending.TimestampNanoseconds < current.TimestampNanoseconds)
            throw new InvalidDataException("媒体帧时间戳发生倒退，无法保证两路画面对齐。");
        // pull_sample returns the preroll once more on entering PLAYING.
        if (current is not null && pending.TimestampNanoseconds == current.TimestampNanoseconds)
        {
            DisposeFrame(ref pending);
            return false;
        }
        return true;
    }

    private static long PlaybackEnd(GstDecoder? decoder, MediaFrame? current, MediaFrame? pending)
        => decoder is { IsStill: false, IsEnded: true } && pending is null && current is not null
            ? current.EndNanoseconds ?? current.TimestampNanoseconds : long.MaxValue;

    private static void VerifyPlaybackFrame(GstDecoder? decoder, MediaFrame? current, MediaFrame? pending,
        MediaFrame? following, long target)
    {
        if (decoder is null || decoder.IsStill) return;
        var advance = pending is not null && pending.TimestampNanoseconds <= target;
        var candidate = advance ? pending : current;
        if (candidate is null) throw new InvalidDataException("播放时缺少可显示的媒体帧。");
        var next = advance ? following : pending;
        var coverage = FrameTiming.Coverage(candidate.TimestampNanoseconds, candidate.EndNanoseconds,
            next?.TimestampNanoseconds, target, decoder.IsEnded);
        if (coverage != FrameCoverage.Covered)
            throw new InvalidDataException("当前解码帧无法覆盖播放位置，已暂停。");
    }

    private static void CommitPlaybackFrame(ref MediaFrame? current, ref MediaFrame? pending,
        ref MediaFrame? following, long target)
    {
        if (pending is null || pending.TimestampNanoseconds > target) return;
        DisposeFrame(ref current);
        current = pending; pending = following; following = null;
    }

    private async Task<bool> WaitForPresentationAsync(long target)
    {
        var deadline = checked(_playbackBaseTime + (ulong)Math.Max(0, target - _playbackStart));
        while (!_stopping.IsCancellationRequested && !_commands.Reader.TryPeek(out _))
        {
            var now = GstNative.gst_clock_get_time(_clock);
            if (now >= deadline) return true;
            // Flush seeks reset running_time to zero. The same base time schedules
            // native audio and this pair; commands interrupt even a very slow frame.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            var milliseconds = Math.Min((deadline - now + 999_999) / 1_000_000, int.MaxValue);
            timeout.CancelAfter((int)milliseconds);
            try { await _commands.Reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!_stopping.IsCancellationRequested) { }
        }
        return false;
    }

    private void Publish(long positionNanoseconds)
    {
        var pair = new FramePair(_currentA?.Retain(), _currentB?.Retain(), positionNanoseconds);
        try
        {
            if (FrameReady is { } handler) handler(pair);
            else pair.Dispose();
        }
        catch { pair.Dispose(); throw; }
    }

    private void Pause(bool capturePosition = false)
    {
        var position = capturePosition && IsPlaying ? PausedPosition() : (long?)null;
        try { _a?.Pause(); _b?.Pause(); }
        finally { SetPlaying(false); }
        if (position is { } actual)
        {
            Interlocked.Exchange(ref _positionNanoseconds, actual);
            Publish(actual);
        }
    }

    private long PausedPosition()
    {
        var now = GstNative.gst_clock_get_time(_clock);
        var elapsed = now > _playbackBaseTime ? now - _playbackBaseTime : 0;
        var actual = _playbackStart + (long)Math.Min(elapsed, (ulong)(long.MaxValue - _playbackStart));
        actual = ClampPosition(actual, Duration);
        var published = Interlocked.Read(ref _positionNanoseconds);
        if (_a is { IsStill: false })
            actual = FrameTiming.ClampPausePosition(published, actual, _currentA?.EndNanoseconds, _pendingA?.TimestampNanoseconds);
        if (_b is { IsStill: false })
            actual = FrameTiming.ClampPausePosition(published, actual, _currentB?.EndNanoseconds, _pendingB?.TimestampNanoseconds);
        return actual;
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
        DisposeFrame(ref _pendingA); DisposeFrame(ref _pendingB);
        DisposeFrame(ref _followingA); DisposeFrame(ref _followingB);
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
