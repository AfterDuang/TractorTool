using TractorTool.Plugins.MediaCompare.Media;

internal static class TimingChecks
{
    internal static void Run()
    {
        Require(FrameTiming.ResolveDuration(0, ulong.MaxValue) == TimeSpan.Zero,
            "A VFR sample may have neither duration nor a fixed frame rate.");
        Require(FrameTiming.ResolveDuration(0, 40_000_000) == Ms(40),
            "A valid sample duration must survive missing frame-rate metadata.");
        Require(FrameTiming.ResolveDuration(0, ulong.MaxValue, 25, 1) == Ms(40),
            "Fixed-rate caps may supply a duration when the buffer omits it.");
        Require(FrameTiming.ResolveDuration(0, 20_000_000, 25, 1) == Ms(20),
            "A seek-clipped buffer duration must take precedence over nominal frame rate.");
        var fractionalStart = TimeSpan.FromTicks(333_333);
        var fractionalEnd = TimeSpan.FromTicks(666_667);
        var fractionalDuration = FrameTiming.ResolveDuration(33_333_333, 33_333_367);
        Require(fractionalStart + fractionalDuration == fractionalEnd,
            "Nanosecond PTS and duration carry into the absolute end before tick conversion.");
        Require(FrameTiming.Coverage(fractionalStart, fractionalDuration, fractionalEnd,
            TimeSpan.FromTicks(666_666), ended: false) == FrameCoverage.Covered,
            "The 66,666,600 ns target is covered before the actual 66,666,700 ns boundary.");
        Require(FrameTiming.Coverage(fractionalStart, fractionalDuration, fractionalEnd,
            fractionalEnd, ended: false) == FrameCoverage.Advance,
            "The exact successor boundary still advances without tolerance.");
        Require(FrameTiming.ResolveDuration(33_333_367, ulong.MaxValue, 30, 1) == TimeSpan.FromTicks(333_334),
            "Fixed-rate fallback carries fractional PTS into its absolute end as well.");
        Require(FrameTiming.ResolveDuration(33_366_667, ulong.MaxValue, 30_000, 1001) == TimeSpan.FromTicks(333_667),
            "Fractional fixed rates preserve the exact caps fraction through endpoint conversion.");
        RejectDuration(ulong.MaxValue - 10, 11, 0, 1);
        RejectDuration(ulong.MaxValue - 10, 10, 0, 1);
        RejectDuration(ulong.MaxValue - 10, ulong.MaxValue, 25, 1);
        RejectDuration(0, 1, 0, 1);
        Require(FrameTiming.CoversPreroll(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero),
            "Playback may begin at the real zero timestamp without guessing a future duration.");
        Require(!FrameTiming.CoversPreroll(Ms(123), TimeSpan.Zero, Ms(123)),
            "A nonzero seek may clip unknown-duration PTS to its target without proving frame identity.");
        Require(!FrameTiming.CoversPreroll(Ms(25), TimeSpan.Zero, Ms(25)),
            "Two unknown-duration frames clipped to the same seek target cannot establish coverage.");
        Require(!FrameTiming.CoversPreroll(Ms(100), TimeSpan.Zero, Ms(123)),
            "An unknown-duration preroll cannot independently establish later coverage.");
        Require(!FrameTiming.CoversPreroll(Ms(200), TimeSpan.Zero, Ms(100)),
            "A future preroll must not replace a covering frame dropped by decoder clipping.");
        Require(!FrameTiming.CoversPreroll(Ms(33.367), Ms(33.333), Ms(33.333)),
            "Even a known-duration preroll must not be accepted before its true timestamp.");
        Require(FrameTiming.CoversPreroll(Ms(100), Ms(40), Ms(123)), "Known preroll interval covers the target.");
        Require(!FrameTiming.CoversPreroll(Ms(100), Ms(20), Ms(123)), "Known preroll gaps remain invalid.");

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
        => FrameTiming.Coverage(Ms(start), Ms(duration), next is { } time ? Ms(time) : null, Ms(target), ended);
    private static TimeSpan Ms(double value) => TimeSpan.FromMilliseconds(value);
    private static void RejectDuration(ulong start, ulong duration, int numerator, int denominator)
    {
        try { FrameTiming.ResolveDuration(start, duration, numerator, denominator); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid frame boundaries must be rejected without wrapping or inventing duration.");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
