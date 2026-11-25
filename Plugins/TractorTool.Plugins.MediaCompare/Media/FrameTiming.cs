namespace TractorTool.Plugins.MediaCompare.Media;

internal enum FrameCoverage { Covered, NeedNext, Advance, Ended, Gap }

/// <summary>Pair frames in the decoder's nanosecond timebase; TimeSpan is only a UI boundary.</summary>
internal static class FrameTiming
{
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
