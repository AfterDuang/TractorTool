using TractorTool.Plugins.MediaCompare.Media;

internal static class TimingChecks
{
    internal static void Run()
    {
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
        Require(Coverage(100, 20, 180, 130) == FrameCoverage.Gap,
            "A successor cannot stretch a known short interval across a real gap.");
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
        Require(Coverage(0, 33.333, 33.367, 33.333) == FrameCoverage.Gap,
            "Timestamp tolerance must not bridge even a short explicit gap.");
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
