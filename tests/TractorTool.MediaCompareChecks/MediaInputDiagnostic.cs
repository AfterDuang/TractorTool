using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using TractorTool.Plugins.MediaCompare.Media;
using TractorTool.Plugins.MediaCompare.Native.Interop;

// Opt-in decoder checks against generated fixtures. No host, Avalonia window, or user settings.
internal static class MediaInputDiagnostic
{
    internal static int Run(string[] args)
    {
        if (args.Length != 4 || args[0] is not ("--media-inputs" or "--seek-play"))
        {
            Console.Error.WriteLine("Usage: --media-inputs|--seek-play <runtimeRoot> <newScratchDirectory> <fixtureRoot>");
            return 2;
        }
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The decoder diagnostic requires Windows.");
        foreach (var path in args.Skip(1))
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Diagnostic paths must be absolute.");
        if (Directory.Exists(args[2]) || File.Exists(args[2]))
            throw new IOException("Choose a new scratch directory; existing data is never reused.");
        if (!Directory.Exists(args[1]) || !Directory.Exists(args[3]))
            throw new DirectoryNotFoundException("Runtime or generated fixture directory is missing.");

        Directory.CreateDirectory(args[2]);
        using var log = new StreamWriter(Path.Combine(args[2], "inputs.log")) { AutoFlush = true };
        void Write(string text)
        {
            var line = $"{DateTimeOffset.Now:O} {text}";
            Console.WriteLine(line);
            log.WriteLine(line);
        }
        Write($"process: pid={Environment.ProcessId}");
        GstRuntime.EnsureInitialized(args[1], Path.Combine(args[2], "cache"));
        NativeLibrary.SetDllImportResolver(typeof(MediaInputDiagnostic).Assembly, (name, _, _) =>
            name == GstNative.Core ? NativeLibrary.Load(Path.Combine(args[1], "bin", name)) : 0);
        var failures = 0;
        void Check(string name, Action action)
        {
            Write($"begin: {name}");
            try { action(); Write($"pass: {name}"); }
            catch (Exception error) { failures++; Write($"fail: {name}\n{error}"); }
        }
        string Fixture(string name)
        {
            var path = Path.Combine(args[3], name);
            if (!File.Exists(path)) throw new FileNotFoundException("Generated fixture is missing.", path);
            return path;
        }

        var clock = GstNative.gst_system_clock_obtain();
        if (clock == 0) throw new InvalidOperationException("Could not obtain the GStreamer clock.");
        try
        {
            if (args[0] == "--media-inputs")
            {
                foreach (var name in new[] { "dvdlpcmdec", "avdec_ac3", "avdec_eac3", "avdec_dca", "tsdemux", "pngdec", "jpegdec" })
                {
                    var factory = Native.gst_element_factory_find(name);
                    Write($"factory: {name}={(factory != 0 ? "present" : "missing")}");
                    if (factory != 0) GstNative.gst_object_unref(factory);
                }
                foreach (var extension in new[] { "png", "jpg" })
                {
                    Check($"single-{extension}", () => CheckImage(new(Fixture($"input.{extension}")), false, clock, Write));
                    foreach (var kind in new[] { MediaSourceKind.Sequence, MediaSourceKind.Auto })
                        Check($"sequence-{extension}-{kind}", () => CheckImage(
                            new(Fixture($"{extension}-sequence/frame_0001.{extension}"), kind, 24), true, clock, Write));
                }
                Check("single-png16", () => CheckImage(new(Fixture("input16.png")), false, clock, Write, 16));
                foreach (var kind in new[] { MediaSourceKind.Sequence, MediaSourceKind.Auto })
                    Check($"sequence-png16-{kind}", () => CheckImage(
                        new(Fixture("png16-sequence/frame_0001.png"), kind, 24), true, clock, Write, 16));
                Check("seek-play-subtick-boundaries", () => CheckBoundarySeeks(Fixture("video.mp4"), clock, Write));
                Check("session-actual-seek-position", () => CheckActualSeekPosition(args[1],
                    Path.Combine(args[2], "session-cache"), Fixture("video.mp4"), Write));
                foreach (var sequenceFirst in new[] { false, true })
                    Check($"session-video24-sequence-sequenceFirst={sequenceFirst}", () => CheckSessionPairing(
                        args[1], Path.Combine(args[2], "session-cache"), Fixture("video24.mp4"),
                        Fixture("png-sequence/frame_0001.png"), sequenceFirst, Write));
                Check("10bit-policy", () => CheckHighBitDepth(Fixture("video-10bit.mkv"), clock, Write));
                Check("hdr-sei-policy", () => CheckHdrRejected(Fixture("video-hdr-sei.mkv"), clock, Write));
                Check("decoder-audio-ac3", () => CheckDecoderAudio(Fixture("audio-ac3.m2ts"), clock, Write));
                Check("decoder-audio-lpcm", () => CheckDecoderAudio(Fixture("audio-lpcm.m2ts"), clock, Write));
                Check("audio-ac3", () => CheckAudio(Fixture("audio-ac3.m2ts"), Write));
                Check("audio-lpcm", () => CheckAudio(Fixture("audio-lpcm.m2ts"), Write));
                Check("pixel-aspect-ratio", () => PlaybackRegressionChecks.CheckPixelAspect(Fixture("video-par.mp4"), clock, Write));
                Check("gpu-upload-reuse", () => PlaybackRegressionChecks.CheckUploadReuse(Fixture("input.png"), clock, Write));
                Check("prepared-seek-reuse", () => PlaybackRegressionChecks.CheckPreparedSeekReuse(args[1],
                    Path.Combine(args[2], "session-cache"), args[3], Write));
                Check("common-timeline-and-replay", () => PlaybackRegressionChecks.Run(args[1],
                    Path.Combine(args[2], "session-cache"), args[3], Write));
            }
            foreach (var name in new[] { "seek-24.mkv", "seek-ntsc.mkv", "seek-film.m2ts", "seek-film.mp4" })
                foreach (var ticks in new long[] { 0, 418_000, 1_234_567, 1_248_000, 5_000_000, 10_010_000, 12_345_678, 29_170_000 })
                    Check($"seek-play-{name}-{ticks}ticks", () => PlaybackRegressionChecks.CheckSeekPlayback(
                        args[1], Path.Combine(args[2], "session-cache"), Fixture(name), TimeSpan.FromTicks(ticks), clock, Write));
            foreach (var name in new[] { "seek-24.mkv", "seek-ntsc.mkv", "seek-film.m2ts" })
                foreach (var sequence in new[] { false, true })
                    foreach (var reverse in new[] { false, true })
                        Check($"mixed-seek-{name}-sequence={sequence}-reverse={reverse}", () =>
                            PlaybackRegressionChecks.CheckMixedSeekPlayback(args[1], Path.Combine(args[2], "session-cache"),
                                Fixture(name), sequence
                                    ? new(Fixture("png-sequence/frame_0001.png"), MediaSourceKind.Sequence, 24)
                                    : new(Fixture("seek-film.mp4")), reverse, Write));
        }
        finally { GstNative.gst_object_unref(clock); }
        Write($"complete: failures={failures}; decoded audio is not speaker-output or A/B sync acceptance");
        return failures == 0 ? 0 : 1;
    }

    private static void CheckImage(MediaSource source, bool sequence, nint clock, Action<string> write, int sourceDepth = 8)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var opened = GstDecoder.Open(source, clock, timeout.Token);
        using var decoder = opened.Decoder;
        using (var frame = opened.FirstFrame)
        {
            Require(frame.Width > 0 && frame.Height > 0 && frame.Texture != 0, "No decoded GPU image was returned.");
            Require(decoder.IsStill != sequence, "Image/sequence classification did not match the request.");
            Require(sourceDepth > 8 ? decoder.Description.PreviewWarning.Contains(sourceDepth.ToString(), StringComparison.Ordinal) &&
                decoder.Description.PreviewWarning.Contains("8", StringComparison.Ordinal) :
                string.IsNullOrEmpty(decoder.Description.PreviewWarning),
                "The persistent per-canvas warning must describe 16-to-8-bit conversion only for high-depth fixtures.");
            write($"image: {decoder.Description}; ptsNs={frame.TimestampNanoseconds}; endNs={frame.EndNanoseconds}");
        }
        if (!sequence) return;
        var target = checked(decoder.Description.Duration.Ticks * 100 / 2);
        using (var located = decoder.Seek(target, timeout.Token))
            Require(FrameTiming.CoversPreroll(located.TimestampNanoseconds, located.PrerollEndNanoseconds, target),
                "Sequence seek did not cover the requested time.");
        decoder.Play(GstNative.gst_clock_get_time(clock) + 30_000_000);
        var advanced = false;
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(3))
        {
            timeout.Token.ThrowIfCancellationRequested();
            using var sample = decoder.ReadSample(50_000_000);
            if (sample is not null && sample.TimestampNanoseconds > target) { advanced = true; break; }
            if (decoder.IsEnded) break;
        }
        decoder.Pause();
        Require(advanced, "Sequence playback did not advance past its seeked frame.");
    }

    private static void CheckBoundarySeeks(string path, nint clock, Action<string> write)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var opened = GstDecoder.Open(new(path), clock, timeout.Token);
        using var decoder = opened.Decoder;
        opened.FirstFrame.Dispose();
        var failures = new List<string>();
        // 30/1 and 30000/1001 boundaries, including legitimate tails smaller than one .NET tick.
        foreach (var target in new long[] { 33_333_300, 33_333_333, 33_333_334, 33_366_600,
            33_366_666, 33_366_667, 1_000_999_900, 1_001_000_000, 66_666_600, 66_666_666,
            66_733_300, 66_733_333 })
        {
            using (var located = decoder.Seek(target, timeout.Token))
            {
                var covered = FrameTiming.CoversPreroll(located.TimestampNanoseconds, located.PrerollEndNanoseconds, target);
                var observation = $"requestedNs={target}; ptsNs={located.TimestampNanoseconds}; " +
                    $"endNs={located.EndNanoseconds}; prerollEndNs={located.PrerollEndNanoseconds}; covered={covered}";
                write("seek: " + observation);
                // qtdemux ceil-converts this MP4 track's timebase and picks the
                // 1.001 s keyframe. Session must report/re-align to that actual time.
                var expectedNativeRounding = target == 1_000_999_900 && located.TimestampNanoseconds == 1_001_000_000 &&
                    FrameTiming.ResolveSeekPosition(target, located.TimestampNanoseconds,
                        located.EndNanoseconds, decoder.Description.FrameRate) == 1_001_000_000;
                if (!covered && !expectedNativeRounding) failures.Add(observation);
            }
            decoder.Play(GstNative.gst_clock_get_time(clock) + 30_000_000);
            var advanced = false;
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(3))
            {
                timeout.Token.ThrowIfCancellationRequested();
                using var sample = decoder.ReadSample(50_000_000);
                if (sample is not null)
                {
                    write($"seek-play: requestedNs={target}; ptsNs={sample.TimestampNanoseconds}; endNs={sample.EndNanoseconds}");
                    if (sample.TimestampNanoseconds > target) { advanced = true; break; }
                }
            }
            decoder.Pause();
            Require(advanced, "Playback after a boundary seek did not advance.");
        }
        Require(failures.Count == 0, "Boundary seeks returned frames outside the target interval: " + string.Join(" | ", failures));
    }

    private static void CheckActualSeekPosition(string runtime, string cache, string video, Action<string> write)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var session = new ComparisonSession(runtime, cache);
        PairObservation? observed = null;
        void Receive(FramePair pair)
        {
            using (pair)
            {
                if (pair.A is { } a && pair.B is { } b)
                    observed = new(a.TimestampNanoseconds, a.EndNanoseconds, b.TimestampNanoseconds,
                        b.EndNanoseconds, pair.PositionNanoseconds, session.IsPlaying);
            }
        }
        void Await(Task task) => task.WaitAsync(timeout.Token).GetAwaiter().GetResult();
        session.FrameReady += Receive;
        try
        {
            Await(session.OpenAsync(MediaSide.A, new(video), timeout.Token));
            Await(session.OpenAsync(MediaSide.B, new(video), timeout.Token));
            Await(session.SeekAsync(TimeSpan.FromTicks(10_009_999), timeout.Token));
            const long actual = 1_001_000_000;
            Require(observed is { } pair && pair.Position == actual && session.Position.Ticks == actual / 100 &&
                FrameTiming.CoversPreroll(pair.A, pair.EndA, actual) && FrameTiming.CoversPreroll(pair.B, pair.EndB, actual),
                "The session must align both frames/audio to the reported actual seek location.");
            write($"actual-seek: requestedNs=1000999900; effectiveNs={session.Position.Ticks * 100}; pair={observed}");
            Await(session.SetPlayingAsync(true, timeout.Token));
            Await(session.SetPlayingAsync(false, timeout.Token));
        }
        finally
        {
            session.FrameReady -= Receive;
            session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
    }

    private static void CheckHighBitDepth(string path, nint clock, Action<string> write)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var opened = GstDecoder.Open(new(path), clock, timeout.Token);
        using var decoder = opened.Decoder;
        using var frame = opened.FirstFrame;
        Require(frame.Texture != 0 && decoder.Description.Format.Contains("10", StringComparison.Ordinal) &&
            decoder.Description.Format.Contains("8", StringComparison.Ordinal) &&
            decoder.Description.PreviewWarning.Contains("10", StringComparison.Ordinal) &&
            decoder.Description.PreviewWarning.Contains("8", StringComparison.Ordinal),
            "10-bit SDR input must decode and clearly disclose the 8-bit preview conversion.");
        Require(decoder.Description.AudioStatus == MediaAudioStatus.NoTrack,
            "The silent 10-bit fixture must report that it has no audio track.");
        write($"10bit-preview: {decoder.Description.Format}");
    }

    private static void CheckHdrRejected(string path, nint clock, Action<string> write)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            // The fixture has BT.709 VUI tags but retains HDR mastering/light-level SEI.
            var opened = GstDecoder.Open(new(path), clock, timeout.Token);
            using var decoder = opened.Decoder;
            using var frame = opened.FirstFrame;
        }
        catch (NotSupportedException error) when (error.Message.Contains("HDR", StringComparison.OrdinalIgnoreCase))
        {
            write($"hdr-sei-rejected: {error.Message}");
            return;
        }
        throw new InvalidOperationException("HDR SEI input must not be accepted as ordinary SDR preview.");
    }

    private static void CheckDecoderAudio(string path, nint clock, Action<string> write)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var opened = GstDecoder.Open(new(path), clock, timeout.Token);
        using var decoder = opened.Decoder;
        using var frame = opened.FirstFrame;
        write($"decoder-audio: {decoder.Description.AudioStatus}; {decoder.Description.AudioDescription}");
        Require(decoder.Description.AudioStatus == MediaAudioStatus.Ready,
            "The actual media decoder did not report ready audio for the generated M2TS fixture.");
        decoder.SetMuted(false);
        decoder.SetMuted(true);
    }

    private readonly record struct PairObservation(long A, long? EndA, long B, long? EndB, long Position, bool Playing);

    private static void CheckSessionPairing(string runtime, string cache, string video, string sequence,
        bool sequenceFirst, Action<string> write)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var session = new ComparisonSession(runtime, cache);
        var gate = new object();
        var pairs = new List<PairObservation>();
        Exception? failure = null;
        void Receive(FramePair pair)
        {
            using (pair)
            {
                if (pair.A is not { } a || pair.B is not { } b) return;
                lock (gate) pairs.Add(new(a.TimestampNanoseconds, a.EndNanoseconds,
                    b.TimestampNanoseconds, b.EndNanoseconds, pair.PositionNanoseconds, session.IsPlaying));
            }
        }
        void Failed(Exception error) { lock (gate) failure ??= error; }
        void Await(Task operation) => operation.WaitAsync(TimeSpan.FromSeconds(20), timeout.Token).GetAwaiter().GetResult();
        PairObservation[] Snapshot()
        {
            lock (gate)
            {
                if (failure is not null) ExceptionDispatchInfo.Throw(failure);
                return pairs.ToArray();
            }
        }
        session.FrameReady += Receive;
        session.Error += Failed;
        try
        {
            var sequenceSource = new MediaSource(sequence, MediaSourceKind.Auto, 24);
            var videoSource = new MediaSource(video);
            Await(session.OpenAsync(MediaSide.A, sequenceFirst ? sequenceSource : videoSource, timeout.Token));
            Await(session.OpenAsync(MediaSide.B, sequenceFirst ? videoSource : sequenceSource, timeout.Token));
            for (var phase = 0; phase < 2; phase++)
            {
                if (phase == 1) Await(session.SeekAsync(TimeSpan.FromTicks(1_666_666), timeout.Token));
                var count = Snapshot().Length;
                var start = session.Position;
                Await(session.SetPlayingAsync(true, timeout.Token));
                Task.Delay(1000, timeout.Token).GetAwaiter().GetResult();
                Await(session.SetPlayingAsync(false, timeout.Token));
                var observations = Snapshot().Skip(count).Where(pair => pair.Playing).ToArray();
                Require(observations.Length >= 12 && session.Position - start >= TimeSpan.FromMilliseconds(600),
                    "Video/sequence playback did not advance through paired frames.");
                for (var i = 0; i < observations.Length; i++)
                {
                    var pair = observations[i];
                    var target = pair.Position;
                    Require(pair.A <= target && pair.B <= target,
                        "A video/sequence pair contains a future frame.");
                    Require(Math.Round(pair.A * 24d / 1_000_000_000) == Math.Round(pair.B * 24d / 1_000_000_000),
                        "Matching 24 fps inputs must show the same logical frame, independent of A/B placement.");
                    for (var next = i + 1; next < observations.Length; next++)
                    {
                        var successor = observations[next];
                        if (successor.A > pair.A) Require(target < successor.A, "A frame exceeded its real successor PTS.");
                        if (successor.B > pair.B) Require(target < successor.B, "B frame exceeded its real successor PTS.");
                        if (successor.A > pair.A && successor.B > pair.B) break;
                    }
                }
                var before = Snapshot();
                Await(session.RefreshPreviewAsync(timeout.Token));
                var after = Snapshot();
                Require(after.Length == before.Length + 1 && after[^1] == (before[^1] with { Playing = false }),
                    "Refreshing the paused preview must preserve both original frame timestamps and the position.");
                write($"paired-sequence: sequenceFirst={sequenceFirst}; phase={phase}; pairs={observations.Length}; " +
                    $"advanceMs={(session.Position - start).TotalMilliseconds:F3}; refreshPtsNs={after[^1].A}/{after[^1].B}");
            }
        }
        finally
        {
            session.FrameReady -= Receive;
            session.Error -= Failed;
            session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
    }

    private static void CheckAudio(string path, Action<string> write)
    {
        // Decode with the shipped modules into PCM. This deliberately does not claim
        // that the user's selected output device, mute state, or speakers work.
        var description = $"uridecodebin uri=\"{new Uri(path).AbsoluteUri}\" name=decode " +
            "decode. ! queue ! video/x-raw ! fakesink sync=false " +
            "decode. ! queue ! audioconvert ! audio/x-raw,format=S16LE,layout=interleaved ! " +
            "appsink name=audio sync=false max-buffers=8 drop=false wait-on-eos=false";
        var pipeline = Native.gst_parse_launch(description, out var parseError);
        nint sink = 0, bus = 0;
        try
        {
            if (parseError != 0 || pipeline == 0)
                throw new InvalidOperationException(GstNative.TakeError(parseError, "Could not create audio decoder pipeline."));
            sink = GstNative.gst_bin_get_by_name(pipeline, "audio");
            bus = GstNative.gst_element_get_bus(pipeline);
            Require(sink != 0 && bus != 0, "Audio pipeline has no sink or bus.");
            if (GstNative.gst_element_set_state(pipeline, GstNative.Playing) == 0)
            { CheckBusError(bus); throw new InvalidOperationException("Audio pipeline could not start."); }
            var watch = Stopwatch.StartNew();
            long frames = 0;
            var rate = 0;
            var samples = 0;
            var nonzero = false;
            while (watch.Elapsed < TimeSpan.FromSeconds(10))
            {
                CheckBusError(bus);
                var sample = GstNative.gst_app_sink_try_pull_sample(sink, 50_000_000);
                if (sample == 0)
                {
                    if (samples > 0 && GstNative.gst_app_sink_is_eos(sink) != 0) break;
                    continue;
                }
                try
                {
                    var buffer = GstNative.gst_sample_get_buffer(sample);
                    var caps = GstNative.gst_sample_get_caps(sample);
                    Require(buffer != 0 && caps != 0, "Decoded audio has no buffer or caps.");
                    var structure = GstNative.gst_caps_get_structure(caps, 0);
                    var channels = 0;
                    Require(GstNative.gst_structure_get_int(structure, "rate", out var currentRate) != 0 && currentRate > 0 &&
                        GstNative.gst_structure_get_int(structure, "channels", out channels) != 0 && channels > 0,
                        "Decoded audio has no sample rate or channel count.");
                    if (rate != 0) Require(rate == currentRate, "Audio sample rate changed during the diagnostic.");
                    rate = currentRate;
                    var size = Native.gst_buffer_get_size(buffer);
                    Require(size is > 0 and <= 2 * 1024 * 1024 && size % (nuint)(channels * 2) == 0,
                        "Decoded PCM buffer has an invalid size.");
                    var data = new byte[checked((int)size)];
                    Require(Native.gst_buffer_extract(buffer, 0, data, size) == size, "Could not read decoded PCM.");
                    nonzero |= data.Any(value => value != 0);
                    frames += data.Length / (channels * 2);
                    samples++;
                    if (frames >= rate && nonzero) break;
                }
                finally { GstNative.gst_mini_object_unref(sample); }
            }
            CheckBusError(bus);
            write($"audio: buffers={samples}; frames={frames}; rate={rate}; nonzero={nonzero}");
            Require(rate > 0 && frames >= rate && nonzero, "No complete second of non-silent decoded audio was received.");
        }
        finally
        {
            if (pipeline != 0) GstNative.gst_element_set_state(pipeline, GstNative.Null);
            if (sink != 0) GstNative.gst_object_unref(sink);
            if (bus != 0) GstNative.gst_object_unref(bus);
            if (pipeline != 0) GstNative.gst_object_unref(pipeline);
        }
    }

    private static void CheckBusError(nint bus)
    {
        while (GstNative.gst_bus_pop(bus) is var message && message != 0)
        {
            try
            {
                if (Marshal.PtrToStructure<GstNative.MessageHeader>(message).Type != 2) continue;
                GstNative.gst_message_parse_error(message, out var error, out var debug);
                string? details;
                try { details = debug == 0 ? null : Marshal.PtrToStringUTF8(debug); }
                finally { if (debug != 0) GstNative.g_free(debug); }
                throw new InvalidOperationException(GstNative.TakeError(error, "Audio decoder failed."),
                    details is null ? null : new InvalidOperationException(details));
            }
            finally { GstNative.gst_mini_object_unref(message); }
        }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static class Native
    {
        [DllImport(GstNative.Core, CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint gst_element_factory_find([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(GstNative.Core, CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint gst_parse_launch([MarshalAs(UnmanagedType.LPUTF8Str)] string description, out nint error);
        [DllImport(GstNative.Core, CallingConvention = CallingConvention.Cdecl)]
        internal static extern nuint gst_buffer_get_size(nint buffer);
        [DllImport(GstNative.Core, CallingConvention = CallingConvention.Cdecl)]
        internal static extern nuint gst_buffer_extract(nint buffer, nuint offset, [Out] byte[] destination, nuint size);
    }
}
