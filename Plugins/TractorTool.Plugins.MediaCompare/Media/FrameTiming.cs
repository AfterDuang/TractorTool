namespace TractorTool.Plugins.MediaCompare.Media;

internal enum FrameCoverage { Covered, NeedNext, Advance, Ended, Gap }

/// <summary>Pair frames in the decoder's nanosecond timebase; TimeSpan is only a UI boundary.</summary>
internal static class FrameTiming
{
    internal static long? NextPresentation(long? a, long? b,
        (int Numerator, int Denominator) rateA, (int Numerator, int Denominator) rateB,
        long? endA = null, long? endB = null)
    {
        if (a is null) return b;
        if (b is null) return a;
        var first = Math.Min(a.Value, b.Value);
        var last = Math.Max(a.Value, b.Value);
        if (first == last) return first;
        // Some CFR sources floor their rational PTS and others ceil it. Coalesce
        // only two representations of the very same grid boundary, at its later
        // real timestamp. This neither shifts frames nor tolerates arbitrary gaps.
        if ((a < b ? endA : endB) is not { } earlierEnd || earlierEnd <= last ||
            !ShareGridBoundary(a.Value, b.Value, rateA, rateB)) return first;
        return last;
    }

    internal static bool ShareGridBoundary(long a, long b,
        (int Numerator, int Denominator) rateA, (int Numerator, int Denominator) rateB)
    {
        if (Math.Abs(a - b) != 1 || !TryGridIndex(a, rateA, out var indexA) ||
            !TryGridIndex(b, rateB, out var indexB)) return false;
        return indexA * (uint)rateA.Denominator * (uint)rateB.Numerator ==
            indexB * (uint)rateB.Denominator * (uint)rateA.Numerator;
    }

    internal static long? CoverageEnd(long? end, long? next)
        => end is null ? next : next is null ? end : Math.Min(end.Value, next.Value);

    internal static long? GapDeadline(long? end, long? next, (int Numerator, int Denominator) rate)
        => end is { } stop && next is { } start && stop < start && !ShareGridBoundary(stop, start, rate, rate)
            ? stop : null;

    internal static long ClampPausePosition(long published, long clockPosition, long? end, long? next)
    {
        var limit = CoverageEnd(end, next) is { } stop ? Math.Max(published, stop - 1) : published;
        return Math.Clamp(clockPosition, published, limit);
    }

    private static bool TryGridIndex(long timestamp, (int Numerator, int Denominator) rate, out UInt128 index)
    {
        index = 0;
        if (timestamp < 0 || rate.Numerator <= 0 || rate.Denominator <= 0) return false;
        var scale = (UInt128)1_000_000_000 * (uint)rate.Denominator;
        index = ((UInt128)timestamp * (uint)rate.Numerator + scale / 2) / scale;
        var exact = index * scale;
        var floor = exact / (uint)rate.Numerator;
        var ceiling = (exact + (uint)rate.Numerator - 1) / (uint)rate.Numerator;
        return (UInt128)timestamp == floor || (UInt128)timestamp == ceiling;
    }

    internal static long? ResolveEnd(ulong startNanoseconds, ulong durationNanoseconds,
        int rateNumerator = 0, int rateDenominator = 1)
    {
        if (startNanoseconds > long.MaxValue)
            throw new InvalidDataException("媒体帧没有有效的开始时间。");
        UInt128 end;
        if (durationNanoseconds is > 0 and < ulong.MaxValue)
            end = (UInt128)startNanoseconds + durationNanoseconds;
        else if (rateNumerator > 0 && rateDenominator > 0)
            end = ((UInt128)startNanoseconds * (uint)rateNumerator +
                (UInt128)1_000_000_000 * (uint)rateDenominator) / (uint)rateNumerator;
        else return null;

        if (end > long.MaxValue || end <= startNanoseconds)
            throw new InvalidDataException("媒体帧结束时间超出有效范围。");
        // Accurate seeks can legitimately leave only 1 ns of a frame. Keep it;
        // rounding before coverage checks would turn that interval into zero.
        return (long)end;
    }

    internal static bool CoversPreroll(long start, long? end, long target)
    {
        if (start > target) return false;
        if (end is { } stop) return target < stop;
        // GstVideoDecoder may clip an unknown-duration frame to a nonzero seek
        // target using an estimated end time. Matching PTS cannot prove identity.
        return target == 0 && start == 0;
    }

    internal static long SequenceEnd(ulong startNanoseconds, int rateNumerator, int rateDenominator)
    {
        if (startNanoseconds > long.MaxValue || rateNumerator <= 0 || rateDenominator <= 0)
            throw new InvalidDataException("序列帧缺少有效的时间或帧率。");
        // imagesequencesrc 1.28 uses ceil(index * period) for PTS but floor(period)
        // for duration. Its generated sequence is contiguous; calculate the next
        // PTS on that same rational grid instead of introducing one-nanosecond gaps.
        var scale = (UInt128)1_000_000_000 * (uint)rateDenominator;
        var nextIndex = (UInt128)startNanoseconds * (uint)rateNumerator / scale + 1;
        var end = (nextIndex * scale + (uint)rateNumerator - 1) / (uint)rateNumerator;
        if (end > long.MaxValue) throw new InvalidDataException("序列帧结束时间超出有效范围。");
        return (long)end;
    }

    internal static long ResolveSeekPosition(long requested, long start, long? end, double frameRate)
    {
        if (start <= requested) return requested;
        // qtdemux 1.28 rounds TIME upward into the track timescale before finding
        // a sample/keyframe. A nearby actual PTS may therefore follow the request.
        // Adopt that explicit location, never stretch its frame back over the request.
        var distance = start - requested;
        if (end is not { } stop || stop <= start || distance >= stop - start ||
            !double.IsFinite(frameRate) || frameRate <= 0 || distance >= 1_000_000_000d / frameRate)
            throw new InvalidDataException("此素材无法定位到请求时间附近的完整帧，已保留原画面。");
        return start;
    }

    internal static FrameCoverage Coverage(long start, long? end, long? next,
        long target, bool ended)
    {
        if (start > target) return FrameCoverage.Gap;
        if (next is { } nextStart && nextStart <= target) return FrameCoverage.Advance;
        if (end is { } stop && target < stop) return FrameCoverage.Covered;
        if (end is null && target == start) return FrameCoverage.Covered;
        if (next is null) return ended ? FrameCoverage.Ended : FrameCoverage.NeedNext;
        return end is not null ? FrameCoverage.Gap : FrameCoverage.Covered;
    }
}
