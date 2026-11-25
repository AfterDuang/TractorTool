using TractorTool.Plugins.MediaCompare.Media;

internal static class TimingChecks
{
    internal static void Run()
    {
        PresentationTimeline();
        Require(FrameTiming.ResolveEnd(0, ulong.MaxValue) is null,
            "A VFR sample may have neither duration nor a fixed frame rate.");
        Require(FrameTiming.ResolveEnd(0, 40_000_000) == Ns(40),
            "A valid sample duration must survive missing frame-rate metadata.");
        Require(FrameTiming.ResolveEnd(0, ulong.MaxValue, 25, 1) == Ns(40),
            "Fixed-rate caps may supply a duration when the buffer omits it.");
        Require(FrameTiming.ResolveEnd(0, 20_000_000, 25, 1) == Ns(20),
            "A seek-clipped buffer duration must take precedence over nominal frame rate.");
        var fractionalEnd = FrameTiming.ResolveEnd(33_333_333, 33_333_367);
        Require(fractionalEnd == 66_666_700,
            "Decoded frame boundaries must retain their native nanosecond precision.");
        Require(FrameTiming.Coverage(33_333_333, fractionalEnd, 66_666_700,
            66_666_699, ended: false) == FrameCoverage.Covered,
            "The last nanosecond before the successor belongs to the current frame.");
        Require(FrameTiming.Coverage(33_333_333, fractionalEnd, 66_666_700,
            66_666_700, ended: false) == FrameCoverage.Advance,
            "The exact successor boundary advances without tolerance.");
        Require(FrameTiming.ResolveEnd(33_333_367, ulong.MaxValue, 30, 1) == 66_666_700,
            "Fixed-rate fallback uses the exact caps fraction.");
        Require(FrameTiming.Coverage(33_333_333, null, 66_666_667, 66_666_666, false) == FrameCoverage.Covered,
            "Nominal fixed-rate rounding cannot create a gap before the actual successor.");
        Require(FrameTiming.Coverage(33_333_333, null, 66_666_667, 66_666_667, false) == FrameCoverage.Advance,
            "Absent native duration uses the exact next PTS rather than nominal frame length.");
        Require(FrameTiming.SequenceEnd(125_000_000, 24, 1) == 166_666_667 &&
            FrameTiming.Coverage(125_000_000, FrameTiming.SequenceEnd(125_000_000, 24, 1),
                166_666_667, 166_666_666, false) == FrameCoverage.Covered,
            "Generated sequence coverage follows its ceil PTS grid, not its independently rounded duration.");
        Require(FrameTiming.SequenceEnd(166_666_667, 24, 1) == 208_333_334 &&
            FrameTiming.SequenceEnd(166_666_666, 24, 1) == 166_666_667,
            "A sequence's exact boundary advances, but the preceding nanosecond stays in its frame.");
        Require(FrameTiming.SequenceEnd(41_708_334, 24_000, 1001) == 83_416_667,
            "Fractional-rate sequences use the original rational timebase.");
        Require(FrameTiming.ResolveSeekPosition(1_000_999_900, 1_001_000_000, 1_034_366_666,
            30_000d / 1001) == 1_001_000_000,
            "A demuxer's nearby future keyframe becomes an explicit actual seek position.");
        Require(FrameTiming.ResolveSeekPosition(123, 100, 140, 25) == 123,
            "An already covering frame does not change the requested position.");
        RejectSeekPosition(100, 200, 250, 25);
        RejectSeekPosition(100, 200, null, 25);
        RejectSeekPosition(100, 200, 300, 0);
        RejectSeekPosition(0, 40_000_000, 100_000_000, 25);
        Require(FrameTiming.ResolveEnd(33_366_667, ulong.MaxValue, 30_000, 1001) == 66_733_333,
            "Fractional fixed rates preserve their native endpoint precision.");
        var clippedEnd = FrameTiming.ResolveEnd(33_333_300, 33);
        Require(clippedEnd == 33_333_333 && FrameTiming.CoversPreroll(33_333_300, clippedEnd, 33_333_300),
            "A valid accurate-seek preroll can have less than one TimeSpan tick remaining.");
        Require(FrameTiming.ResolveEnd(100, 1) == 101 && FrameTiming.CoversPreroll(100, 101, 100),
            "A one-nanosecond frame interval must not collapse into unknown duration.");
        Require(FrameTiming.Coverage(100, 101, 101, 100, ended: false) == FrameCoverage.Covered &&
            FrameTiming.Coverage(100, 101, 101, 101, ended: false) == FrameCoverage.Advance,
            "Distinct frames in the same UI tick must not be coalesced or advanced early.");
        Require(!FrameTiming.CoversPreroll(33_333_333, 66_666_666, 33_333_300),
            "A UI-truncated position must not be treated as the next frame's exact start.");
        RejectEnd(ulong.MaxValue, 1, 0, 1);
        RejectEnd((ulong)long.MaxValue - 10, 11, 0, 1);
        RejectEnd((ulong)long.MaxValue - 10, ulong.MaxValue, 25, 1);
        Require(FrameTiming.CoversPreroll(0, null, 0),
            "Playback may begin at the real zero timestamp without guessing a future duration.");
        Require(!FrameTiming.CoversPreroll(Ns(123), null, Ns(123)),
            "A nonzero seek may clip unknown-duration PTS to its target without proving frame identity.");
        Require(!FrameTiming.CoversPreroll(Ns(25), null, Ns(25)),
            "Two unknown-duration frames clipped to the same seek target cannot establish coverage.");
        Require(!FrameTiming.CoversPreroll(Ns(100), null, Ns(123)),
            "An unknown-duration preroll cannot independently establish later coverage.");
        Require(!FrameTiming.CoversPreroll(Ns(200), null, Ns(100)),
            "A future preroll must not replace a covering frame dropped by decoder clipping.");
        Require(!FrameTiming.CoversPreroll(Ns(33.367), Ns(66.7), Ns(33.333)),
            "Even a known-duration preroll must not be accepted before its true timestamp.");
        Require(FrameTiming.CoversPreroll(Ns(100), Ns(140), Ns(123)), "Known preroll interval covers the target.");
        Require(!FrameTiming.CoversPreroll(Ns(100), Ns(120), Ns(123)), "Known preroll gaps remain invalid.");

        Require(Coverage(100, 0, null, 130) == FrameCoverage.NeedNext,
            "Unknown duration waits for actual successor timing.");
        Require(Coverage(100, 0, 180, 130) == FrameCoverage.Covered,
            "A variable-rate frame lasts until its successor timestamp.");
        Require(Coverage(100, 0, 180, 180) == FrameCoverage.Advance,
            "The successor owns the boundary timestamp.");
        Require(Coverage(100, 20, 180, 130) == FrameCoverage.Covered,
            "Continuous presentation holds the current image until its real successor, independently of nominal duration.");
        Require(Coverage(100, 0, null, 130, ended: true) == FrameCoverage.Ended,
            "Unknown final frames cannot extend to a container's audio tail.");
        Require(Coverage(100, 40, null, 130, ended: true) == FrameCoverage.Covered,
            "An explicit final-frame duration remains usable after EOS.");
        Require(Coverage(100, 40, null, 140, ended: true) == FrameCoverage.Ended,
            "Known final-frame coverage is half open.");
        Require(Coverage(200, 0, 230, 100) == FrameCoverage.Gap,
            "A future frame cannot stand in for an earlier target.");
        Require(Coverage(0, 0, 33.367, 33.333) == FrameCoverage.Covered,
            "A real successor at 33.367 ms must not advance at the 33.333 ms target.");
        Require(Coverage(33.367, 0, 66.733, 33.333) == FrameCoverage.Gap,
            "A current frame must not be accepted before its actual timestamp.");
        Require(Coverage(0, 33.333, 33.367, 33.333) == FrameCoverage.Covered,
            "A quantized duration cannot expire an image before its known successor.");
        foreach (var (start, duration, next) in new (long, ulong, long)[]
        {
            (41_666_666, 41_666_666, 83_333_333), // Independently floored 24 fps PTS/duration.
            (0, 41_666_666, 42_000_000),          // Matroska millisecond PTS.
            (0, 41_708_333, 41_711_111),          // MPEG-TS 90 kHz PTS.
            (123_456_700, 41_708_333, 165_167_811) // Nonzero phase after an accurate seek.
        })
        {
            var end = FrameTiming.ResolveEnd((ulong)start, duration);
            Require(FrameTiming.Coverage(start, end, next, next - 1, false) == FrameCoverage.Covered &&
                FrameTiming.Coverage(start, end, next, next, false) == FrameCoverage.Advance &&
                FrameTiming.ClampPausePosition(start, next - 1, end, next) == next - 1,
                "Rounding differences must neither stop playback nor advance a frame before its real PTS, including when pausing.");
        }
    }

    private static void PresentationTimeline()
    {
        Require(FrameTiming.NextPresentation(null, null, default, default) is null &&
            FrameTiming.NextPresentation(null, 123, default, default) == 123,
            "A missing or still side cannot determine playback cadence.");
        Require(FrameTiming.NextPresentation(958_333_333, 958_333_334, (24, 1), (24, 1), endA: 1_000_000_000) == 958_333_334 &&
            FrameTiming.NextPresentation(958_333_334, 958_333_333, (24, 1), (24, 1), endB: 1_000_000_000) == 958_333_334,
            "The video and sequence's logical frame 23 must share the later actual PTS regardless of A/B order.");
        Require(FrameTiming.NextPresentation(33_333_333, 33_333_334, (30, 1), (60, 1), endA: 66_666_666) == 33_333_334,
            "Different fixed rates may meet at the same exact rational boundary.");
        Require(FrameTiming.NextPresentation(958_333_333, 958_333_334, (0, 1), (24, 1), endA: 1_000_000_000) == 958_333_333 &&
            FrameTiming.NextPresentation(958_333_332, 958_333_333, (24, 1), (24, 1), endA: 1_000_000_000) == 958_333_332 &&
            FrameTiming.NextPresentation(958_333_333, 958_333_334, (24, 1), (24, 1), 958_333_334) == 958_333_333,
            "Unknown rates, off-grid timestamps and a one-nanosecond interval cannot be stretched or coalesced.");
        Require(FrameTiming.NextPresentation(958_333_333, 958_333_334, (24, 1), (24, 1)) == 958_333_333 &&
            FrameTiming.NextPresentation(958_333_333, 958_333_334, (24, 1), (24, 1),
                FrameTiming.CoverageEnd(null, 958_333_334)) == 958_333_333,
            "An unknown final frame or a real successor one nanosecond later must first publish the earlier frame, not skip it or fail alignment.");
        Require(FrameTiming.NextPresentation(958_333_333, 958_333_334, (24, 1), (24, 1),
                FrameTiming.CoverageEnd(null, 1_000_000_000)) == 958_333_334,
            "A real successor can prove an unknown-duration frame covers the later shared boundary.");
        Require(FrameTiming.ClampPausePosition(0, 800_000_000, 1_000_000_000, 1_000_000_000) == 800_000_000 &&
            FrameTiming.ClampPausePosition(0, 800_000_000, null, 1_000_000_000) == 800_000_000,
            "Pausing a one-fps frame at 0.8s retains shared-clock time for both known duration and actual-successor coverage.");
        Require(FrameTiming.ClampPausePosition(0, 800_000_000, 100_000_000, 1_000_000_000) == 800_000_000 &&
            FrameTiming.ClampPausePosition(0, 800_000_000, 1_000_000_000, 500_000_000) == 499_999_999 &&
            FrameTiming.ClampPausePosition(0, 800_000_000, null, null) == 0,
            "Pause holds until a known successor, without advancing into it or extending an unproven final-frame interval.");

        foreach (var rate in new[] { (24, 1), (24_000, 1001) })
        {
            var video = Enumerable.Range(0, 121).Select(index => Boundary(index, rate, roundUp: false)).ToArray();
            var sequence = Enumerable.Range(0, 121).Select(index => Boundary(index, rate, roundUp: true)).ToArray();
            var forward = Pairs(video, sequence, rate, rate);
            var reverse = Pairs(sequence, video, rate, rate);
            Require(forward.Count == 119 && forward.All(pair => pair.A == pair.B),
                "Every displayed video/sequence pair must identify the same logical source frame, not merely overlapping intervals.");
            Require(forward.SequenceEqual(reverse.Select(pair => (pair.Time, pair.B, pair.A))),
                "Swapping A/B must preserve both presentation time and logical correspondence.");
        }

        var slow = Enumerable.Range(0, 4).Select(index => Boundary(index, (1, 1), false)).ToArray();
        var fast = Enumerable.Range(0, 181).Select(index => Boundary(index, (60, 1), false)).ToArray();
        var slowLeft = Pairs(slow, fast, (1, 1), (60, 1));
        var fastLeft = Pairs(fast, slow, (60, 1), (1, 1));
        Require(slowLeft.Count(pair => pair.Time < 1_000_000_000) == 59 &&
            slowLeft.SequenceEqual(fastLeft.Select(pair => (pair.Time, pair.B, pair.A))),
            "A one-fps sequence must not throttle a sixty-fps video, whichever side contains it.");

        var variable = new long[] { 0, 12_000_000, 55_000_000, 90_000_000, 110_000_000 };
        var fixedRate = Enumerable.Range(0, 5).Select(index => Boundary(index, (30, 1), false)).ToArray();
        var mixed = Pairs(variable, fixedRate, (0, 1), (30, 1));
        Require(mixed.Select(pair => pair.Time).SequenceEqual(new long[] { 12_000_000, 33_333_333, 55_000_000, 66_666_666, 90_000_000 }),
            "A variable-rate stream and a fixed-rate stream use their real event-time union, not a shared frame index.");
    }

    private static long Boundary(int index, (int Numerator, int Denominator) rate, bool roundUp)
    {
        var scaled = (long)index * 1_000_000_000 * rate.Denominator;
        return (scaled + (roundUp ? rate.Numerator - 1 : 0)) / rate.Numerator;
    }

    private static List<(long Time, int A, int B)> Pairs(long[] a, long[] b,
        (int Numerator, int Denominator) rateA, (int Numerator, int Denominator) rateB)
    {
        var result = new List<(long, int, int)>();
        var indexA = 0; var indexB = 0;
        while (indexA + 2 < a.Length && indexB + 2 < b.Length)
        {
            var target = FrameTiming.NextPresentation(a[indexA + 1], b[indexB + 1], rateA, rateB,
                a[indexA + 2], b[indexB + 2])!.Value;
            if (a[indexA + 1] <= target) indexA++;
            if (b[indexB + 1] <= target) indexB++;
            Require(a[indexA] <= target && target < a[indexA + 1] &&
                b[indexB] <= target && target < b[indexB + 1],
                "Every selected pair must strictly cover its actual shared presentation time.");
            result.Add((target, indexA, indexB));
        }
        return result;
    }

    private static FrameCoverage Coverage(double start, double duration, double? next, double target, bool ended = false)
        => FrameTiming.Coverage(Ns(start), duration > 0 ? Ns(start + duration) : null,
            next is { } time ? Ns(time) : null, Ns(target), ended);
    private static long Ns(double milliseconds) => checked((long)Math.Round(milliseconds * 1_000_000));
    private static void RejectSeekPosition(long requested, long start, long? end, double rate)
    {
        try { FrameTiming.ResolveSeekPosition(requested, start, end, rate); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Seeking must reject unknown or excessively distant actual locations.");
    }
    private static void RejectEnd(ulong start, ulong duration, int numerator, int denominator)
    {
        try { FrameTiming.ResolveEnd(start, duration, numerator, denominator); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid frame boundaries must be rejected without wrapping or inventing duration.");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
