using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using TractorTool.Plugins.MediaCompare.Media;
using TractorTool.Plugins.MediaCompare.Native.Interop;
using TractorTool.Plugins.MediaCompare.Rendering;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

// Uses generated, frame-coded fixtures. Reading actual GPU pixels catches a correct
// looking timestamp assertion that nevertheless compares two different images.
internal static class PlaybackRegressionChecks
{
    internal static void CheckSeekPlayback(string runtime, string cache, string path, TimeSpan position,
        nint clock, Action<string> write)
    {
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
        {
            var opened = GstDecoder.Open(new(path), clock, timeout.Token);
            using var decoder = opened.Decoder;
            opened.FirstFrame.Dispose();
            using var located = decoder.Seek(checked(position.Ticks * 100), timeout.Token);
            write($"seek-preroll: requestNs={position.Ticks * 100}; pts={located.TimestampNanoseconds}; " +
                $"end={located.EndNanoseconds}; fps={decoder.FrameRateNumerator}/{decoder.FrameRateDenominator}");
            decoder.Play(GstNative.gst_clock_get_time(clock) + 30_000_000);
            var previousPts = located.TimestampNanoseconds;
            var previousEnd = located.EndNanoseconds;
            for (var count = 0; count < 8;)
            {
                timeout.Token.ThrowIfCancellationRequested();
                using var frame = decoder.ReadSample(50_000_000);
                if (frame is null)
                {
                    Require(!decoder.IsEnded, "Synthetic seek fixture ended before eight diagnostic frames.");
                    continue;
                }
                if (frame.TimestampNanoseconds == previousPts) continue;
                write($"seek-frame: previousPts={previousPts}; previousEnd={previousEnd}; " +
                    $"nextPts={frame.TimestampNanoseconds}; nextEnd={frame.EndNanoseconds}; " +
                    $"gapNs={frame.TimestampNanoseconds - previousEnd}");
                previousPts = frame.TimestampNanoseconds;
                previousEnd = frame.EndNanoseconds;
                count++;
            }
        }
        using var probe = new Probe(runtime, cache);
        probe.Open(new(path), new(path));
        probe.Await(probe.Session.SeekAsync(position));
        var actual = probe.Session.Position;
        var countBefore = probe.Snapshot().Length;
        probe.Await(probe.Session.SetPlayingAsync(true));
        probe.WaitFor(() => probe.Session.Position >= actual + TimeSpan.FromMilliseconds(250));
        probe.Await(probe.Session.SetPlayingAsync(false));
        var pairs = probe.Snapshot().Skip(countBefore).Where(item => item.Playing).ToArray();
        Require(pairs.Length >= 4 && pairs.All(item => item.A == item.B && item.Position >= actual.Ticks * 100),
            "Identical inputs must continue together after an arbitrary seek without rewinding.");
        var pausedAt = probe.Session.Position;
        var pausedCount = probe.Snapshot().Length;
        probe.Await(probe.Session.SetPlayingAsync(true));
        probe.WaitFor(() => probe.Session.Position >= pausedAt + TimeSpan.FromMilliseconds(150));
        probe.Await(probe.Session.SetPlayingAsync(false));
        Require(probe.Snapshot().Skip(pausedCount).All(item => item.Position >= pausedAt.Ticks * 100),
            "Resuming after a container-rounded seek must not rewind to the previous frame PTS.");
        write($"seek-play: requestMs={position.TotalMilliseconds:F4}; actualMs={actual.TotalMilliseconds:F4}; " +
            $"pairs={pairs.Length}; finalMs={probe.Session.Position.TotalMilliseconds:F4}");
    }

    internal static void CheckMixedSeekPlayback(string runtime, string cache, string path, MediaSource reference,
        bool reverse, Action<string> write)
    {
        using var probe = new Probe(runtime, cache);
        var source = new MediaSource(path);
        probe.Open(reverse ? reference : source, reverse ? source : reference);
        var requested = TimeSpan.FromTicks(1_248_000); // Inside 24 fps MKV's duration/PTS rounding window.
        probe.Await(probe.Session.SeekAsync(requested));
        var actual = probe.Session.Position;
        var before = probe.Snapshot().Length;
        probe.Await(probe.Session.SetPlayingAsync(true));
        probe.WaitFor(() => probe.Session.Position >= actual + TimeSpan.FromMilliseconds(500));
        probe.Await(probe.Session.SetPlayingAsync(false));
        var pairs = probe.Snapshot().Skip(before).Where(item => item.Playing).ToArray();
        Require(pairs.Length >= 10 && pairs.Select(item => item.A).Distinct().Count() >= 9 &&
            pairs.Select(item => item.B).Distinct().Count() >= 9,
            "Different container timebases must keep both presentation cadences after seeking.");
        for (var i = 0; i < pairs.Length; i++)
        {
            var pair = pairs[i];
            Require(pair.A <= pair.Position && pair.B <= pair.Position && pair.Position >= actual.Ticks * 100,
                "Mixed playback must expose the actual time and never present a future frame.");
            if (i > 0) Require(pair.Position > pairs[i - 1].Position,
                "Mixed playback must preserve increasing presentation times.");
        }
        var pausedAt = probe.Session.Position;
        before = probe.Snapshot().Length;
        probe.Await(probe.Session.SetPlayingAsync(true));
        probe.WaitFor(() => probe.Session.Position >= pausedAt + TimeSpan.FromMilliseconds(150));
        probe.Await(probe.Session.SetPlayingAsync(false));
        Require(probe.Snapshot().Skip(before).All(item => item.Position >= pausedAt.Ticks * 100),
            "Mixed-source pause/resume must preserve the saved time.");
        write($"mixed-seek: source={Path.GetFileName(path)}; reference={reference.Kind}; reverse={reverse}; " +
            $"requestMs={requested.TotalMilliseconds:F4}; actualMs={actual.TotalMilliseconds:F4}; pairs={pairs.Length}");
    }

    internal static void Run(string runtime, string cache, string fixtures, Action<string> write)
    {
        foreach (var reverse in new[] { false, true })
        {
            using var probe = new Probe(runtime, cache, readPixels: true);
            var video = new MediaSource(Path.Combine(fixtures, "coded.mp4"));
            var sequence = new MediaSource(Path.Combine(fixtures, "coded/frame_0001.png"), MediaSourceKind.Sequence, 24);
            probe.Open(reverse ? sequence : video, reverse ? video : sequence);
            probe.PlayTo(TimeSpan.FromSeconds(1.2));
            var pairs = probe.Snapshot().Where(item => item.Playing).ToArray();
            Require(pairs.Length >= 24, "Matching inputs did not deliver their frame cadence.");
            foreach (var pair in pairs)
            {
                Require(Math.Round(pair.A * 24d / 1_000_000_000) == Math.Round(pair.B * 24d / 1_000_000_000),
                    $"Swapped 24 fps inputs disagree on the logical frame: {pair}.");
                Require(Math.Abs(pair.PixelA - pair.PixelB) <= 1,
                    $"Actual decoded pictures disagree despite their timestamps: {pair}.");
            }
            write($"frame-content: reverse={reverse}; pairs={pairs.Length}; final={pairs[^1]}");
        }

        foreach (var reverse in new[] { false, true })
        {
            using var probe = new Probe(runtime, cache);
            var video = new MediaSource(Path.Combine(fixtures, "video24.mp4"));
            var slow = new MediaSource(Path.Combine(fixtures, "png-sequence/frame_0001.png"), MediaSourceKind.Sequence, 1);
            probe.Open(reverse ? video : slow, reverse ? slow : video);
            probe.PlayTo(TimeSpan.FromSeconds(1.2));
            var pairs = probe.Snapshot().Where(item => item.Playing).ToArray();
            var videoFrames = pairs.Select(item => reverse ? item.A : item.B).Distinct().Count();
            var slowFrames = pairs.Select(item => reverse ? item.B : item.A).Distinct().Count();
            Require(videoFrames >= 24 && slowFrames is >= 1 and <= 2,
                "A slow side must hold its own frame without reducing the other side's presentation cadence.");
            write($"mixed-cadence: reverse={reverse}; videoFrames={videoFrames}; slowFrames={slowFrames}");
        }

        using (var probe = new Probe(runtime, cache))
        {
            probe.Open(new(Path.Combine(fixtures, "coded.mp4")), null);
            probe.PlayTo(TimeSpan.FromSeconds(.5));
            var pausedAt = probe.Session.Position;
            var count = probe.Snapshot().Length;
            probe.Await(probe.Session.SetPlayingAsync(true));
            probe.WaitFor(() => probe.Session.Position >= pausedAt + TimeSpan.FromMilliseconds(150));
            probe.Await(probe.Session.SetPlayingAsync(false));
            Require(probe.Snapshot().Skip(count).All(item => item.Position >= pausedAt.Ticks * 100),
                "Ordinary pause/resume must not rewind.");
            probe.Await(probe.Session.SetPlayingAsync(true));
            probe.WaitFor(() => !probe.Session.IsPlaying);
            Require(probe.Session.Position >= TimeSpan.FromSeconds(1.8), "Playback stopped before natural EOS.");
            count = probe.Snapshot().Length;
            probe.Await(probe.Session.SetPlayingAsync(true));
            probe.WaitFor(() => probe.Session.Position >= TimeSpan.FromMilliseconds(250));
            probe.Await(probe.Session.SetPlayingAsync(false));
            var replay = probe.Snapshot().Skip(count).ToArray();
            Require(replay.Length > 2 && replay[0].Position == 0 && replay[^1].Position < 1_000_000_000,
                "Play after natural EOS must restart and advance from the beginning.");
            write($"replay: pausedMs={pausedAt.TotalMilliseconds:F3}; replayPairs={replay.Length}; finalNs={replay[^1].Position}");
        }

        using (var probe = new Probe(runtime, cache))
        {
            probe.Open(new(Path.Combine(fixtures, "video-slow-audio.mp4")), null);
            probe.Await(probe.Session.SetAudioAsync(AudioSource.A));
            probe.Await(probe.Session.SetPlayingAsync(true));
            Thread.Sleep(650);
            probe.Await(probe.Session.SetPlayingAsync(false));
            var position = probe.Session.Position;
            Require(position >= TimeSpan.FromMilliseconds(500) && position < TimeSpan.FromSeconds(1),
                "Pausing a 1 fps source must retain the clock time, not rewind to its last video PTS.");
            var count = probe.Snapshot().Length;
            probe.Await(probe.Session.SetPlayingAsync(true));
            probe.WaitFor(() => probe.Session.Position > TimeSpan.FromSeconds(1));
            probe.Await(probe.Session.SetPlayingAsync(false));
            Require(probe.Snapshot().Skip(count).First().Position >= position.Ticks * 100,
                "Resuming a low-fps audio source must seek to the actual paused time.");
            write($"slow-audio-pause: pausedMs={position.TotalMilliseconds:F3}; resumedMs={probe.Session.Position.TotalMilliseconds:F3}");
        }

        foreach (var remove in new[] { MediaSide.A, MediaSide.B })
        {
            using var probe = new Probe(runtime, cache);
            var still = new MediaSource(Path.Combine(fixtures, "input.png"));
            var moving = new MediaSource(Path.Combine(fixtures, "video-slow-audio.mp4"));
            probe.Open(remove == MediaSide.A ? still : moving, remove == MediaSide.A ? moving : still);
            var audio = remove == MediaSide.A ? AudioSource.B : AudioSource.A;
            probe.Await(probe.Session.SetAudioAsync(audio));
            probe.Await(probe.Session.SetPlayingAsync(true));
            Thread.Sleep(650);
            probe.Await(probe.Session.ClearAsync(remove));
            var position = probe.Session.Position;
            Require(position >= TimeSpan.FromMilliseconds(500) && position < TimeSpan.FromSeconds(1) &&
                probe.Session.SelectedAudio == audio && !probe.Session.IsPlaying,
                "Clearing the still side must pause the retained video/audio at its actual clock time.");
            probe.Await(probe.Session.SetPlayingAsync(true));
            Require(probe.Session.Position >= position, "Resuming after single-side removal must not rewind.");
            write($"clear-side-clock: removed={remove}; positionMs={position.TotalMilliseconds:F3}; audio={audio}");
        }

        using (var probe = new Probe(runtime, cache))
        {
            probe.Open(new(Path.Combine(fixtures, "video-gap.mkv")), new(Path.Combine(fixtures, "video24.mp4")));
            var watch = Stopwatch.StartNew();
            probe.PlayTo(TimeSpan.FromSeconds(1.2));
            var pairs = probe.Snapshot().Where(item => item.Playing).ToArray();
            Require(pairs.Any(item => item.Position > 500_000_000 && item.Position < 1_000_000_000) &&
                pairs.Any(item => item.Position >= 1_000_000_000) &&
                pairs.All(item => item.A == (item.Position < 1_000_000_000 ? 0 : 1_000_000_000)),
                "Sparse video PTS must hold the last picture until its real successor while the other side keeps advancing.");
            write($"sparse-presentation: elapsedMs={watch.Elapsed.TotalMilliseconds:F1}; pairs={pairs.Length}; " +
                "native duration=100ms; actual PTS=0/1/2s; held and switched at real PTS");
        }
    }

    internal static void CheckPixelAspect(string path, nint clock, Action<string> write)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var opened = GstDecoder.Open(new(path), clock, timeout.Token);
        using var decoder = opened.Decoder;
        using var frame = opened.FirstFrame;
        Require(frame.Width == 720 && frame.Height == 576 &&
            Math.Abs(frame.DisplayWidth / frame.DisplayHeight - 4d / 3) < 1e-10,
            "720x576 PAR 16:15 must display as 4:3 without changing texture dimensions.");
        Require(Math.Abs(decoder.Description.PixelAspectRatio - 16d / 15) < 1e-10,
            "Media information must retain the negotiated pixel aspect ratio.");
        write($"pixel-aspect: texture={frame.Width}x{frame.Height}; display={frame.DisplayWidth}x{frame.DisplayHeight}");
    }

    private readonly record struct Observation(long A, long B, long Position, bool Playing, byte PixelA, byte PixelB);

    internal static void CheckUploadReuse(string path, nint clock, Action<string> write)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var opened = GstDecoder.Open(new(path), clock, timeout.Token);
        using var decoder = opened.Decoder;
        using var frame = opened.FirstFrame;
        long luid;
        frame.LockDevice!();
        try
        {
            Marshal.AddRef(frame.Texture);
            using var texture = new ID3D11Texture2D(frame.Texture);
            using var device = texture.Device;
            using var dxgi = device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgi.GetAdapter();
            luid = adapter.Description.Luid;
        }
        finally { frame.UnlockDevice!(); }
        var uploads = 0;
        MediaFrame Tracked()
        {
            var held = frame.Retain();
            return new(held.Width, held.Height, held.TimestampNanoseconds, held.EndNanoseconds,
                held.Texture, held.Subresource, held.Dispose,
                () => { held.LockDevice!(); uploads++; }, held.UnlockDevice,
                held.PrerollEndNanoseconds, held.PixelAspectRatio);
        }
        using var first = Tracked();
        using var replacement = Tracked(); // Same PTS and texture, distinct content identity.
        using (var renderer = D3DPreviewRenderer.Create(BitConverter.GetBytes(luid)))
        {
            using var pair = new FramePair(first.Retain(), null, 0);
            renderer.Upload(pair);
            using var retained = new FramePair(first.Retain(), null, 0);
            renderer.Upload(retained);
            Require(uploads == 1, "An unchanged retained sample must not be uploaded twice.");
            using var changed = new FramePair(replacement.Retain(), null, 0);
            renderer.Upload(changed);
            Require(uploads == 2, "A new sample with reused PTS and texture must still upload.");
            using var empty = new FramePair(null, null, 0);
            renderer.Upload(empty);
            renderer.Upload(changed);
            Require(uploads == 3, "Clearing a side must invalidate its upload cache.");
        }
        using (var recreated = D3DPreviewRenderer.Create(BitConverter.GetBytes(luid)))
        {
            using var pair = new FramePair(replacement.Retain(), null, 0);
            recreated.Upload(pair);
            Require(uploads == 4, "A recreated renderer must upload its first sample.");
        }
        write($"gpu-upload-reuse: uploads={uploads}; retain/replacement/clear/recreate verified");
    }

    private sealed class Probe : IDisposable
    {
        private readonly object _gate = new();
        private readonly List<Observation> _observations = [];
        private readonly bool _readPixels;
        private Exception? _failure;
        internal ComparisonSession Session { get; }

        internal Probe(string runtime, string cache, bool readPixels = false)
        {
            _readPixels = readPixels;
            Session = new(runtime, cache);
            Session.FrameReady += Receive;
            Session.Error += Failed;
        }

        internal void Open(MediaSource a, MediaSource? b)
        {
            Await(Session.OpenAsync(MediaSide.A, a));
            if (b is not null) Await(Session.OpenAsync(MediaSide.B, b));
        }

        internal void PlayTo(TimeSpan position)
        {
            var watch = Stopwatch.StartNew();
            Await(Session.SetPlayingAsync(true));
            WaitFor(() => Session.Position >= position);
            Await(Session.SetPlayingAsync(false));
            Require(watch.Elapsed >= position - TimeSpan.FromMilliseconds(150),
                "Video must follow the shared clock rather than run as fast as it can decode.");
        }

        internal void Await(Task task)
        {
            task.WaitAsync(TimeSpan.FromSeconds(25)).GetAwaiter().GetResult();
            CheckFailure();
        }

        internal void WaitFor(Func<bool> condition)
        {
            var watch = Stopwatch.StartNew();
            while (!condition())
            {
                CheckFailure();
                if (watch.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException("Playback condition was not reached.");
                Thread.Sleep(10);
            }
            CheckFailure();
        }

        internal Observation[] Snapshot() { lock (_gate) return _observations.ToArray(); }

        private void Receive(FramePair pair)
        {
            using (pair)
            {
                try
                {
                    if (pair.A is not { } a) return;
                    var pixelA = _readPixels ? ReadBlue(a) : (byte)0;
                    var pixelB = _readPixels && pair.B is { } b ? ReadBlue(b) : (byte)0;
                    lock (_gate) _observations.Add(new(a.TimestampNanoseconds, pair.B?.TimestampNanoseconds ?? 0,
                        pair.PositionNanoseconds, Session.IsPlaying, pixelA, pixelB));
                }
                catch (Exception error) { lock (_gate) _failure ??= error; }
            }
        }

        private void Failed(Exception error) { lock (_gate) _failure ??= error; }
        private void CheckFailure() { lock (_gate) { if (_failure is { } error) ExceptionDispatchInfo.Throw(error); } }
        public void Dispose()
        {
            Session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            Session.FrameReady -= Receive;
            Session.Error -= Failed;
        }
    }

    private static byte ReadBlue(MediaFrame frame)
    {
        if (frame.LockDevice is null || frame.UnlockDevice is null) throw new InvalidOperationException("Missing device lock.");
        frame.LockDevice();
        try
        {
            Marshal.AddRef(frame.Texture);
            using var source = new ID3D11Texture2D(frame.Texture);
            using var device = source.Device;
            using var context = device.ImmediateContext;
            using var staging = device.CreateTexture2D(new Texture2DDescription
            {
                Width = 1, Height = 1, ArraySize = 1, MipLevels = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read
            });
            context.CopySubresourceRegion(staging, 0, 0, 0, 0, source, frame.Subresource, new Box(0, 0, 0, 1, 1, 1));
            context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped).CheckError();
            try { return Marshal.ReadByte(mapped.DataPointer); }
            finally { context.Unmap(staging, 0); }
        }
        finally { frame.UnlockDevice(); }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
