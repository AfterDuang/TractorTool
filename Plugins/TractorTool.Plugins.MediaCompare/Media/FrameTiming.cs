namespace TractorTool.Plugins.MediaCompare.Media;

internal enum FrameCoverage { Covered, NeedNext, Advance, Ended, Gap }

/// <summary>Unknown duration is bounded by a successor's PTS, never the container's audio tail.</summary>
internal static class FrameTiming
{
    internal static TimeSpan ResolveDuration(ulong startNanoseconds, ulong durationNanoseconds,
        int rateNumerator = 0, int rateDenominator = 1)
    {
        if (startNanoseconds == ulong.MaxValue)
            throw new InvalidDataException("媒体帧没有有效的开始时间。");
        ulong endTicks;
        if (durationNanoseconds is > 0 and < ulong.MaxValue)
        {
            if (durationNanoseconds >= ulong.MaxValue - startNanoseconds)
                throw new InvalidDataException("媒体帧结束时间超出有效范围。");
            endTicks = checked(startNanoseconds + durationNanoseconds) / 100;
        }
        else if (rateNumerator > 0 && rateDenominator > 0)
        {
            // Keep the negotiated frame-rate fraction exact until the absolute end
            // is converted to ticks. UInt128 accommodates ulong PTS * int numerator.
            var scaledEnd = checked((UInt128)startNanoseconds * (uint)rateNumerator +
                (UInt128)1_000_000_000 * (uint)rateDenominator);
            if (scaledEnd >= (UInt128)ulong.MaxValue * (uint)rateNumerator)
                throw new InvalidDataException("媒体帧结束时间超出有效范围。");
            endTicks = (ulong)(scaledEnd / ((UInt128)100 * (uint)rateNumerator));
        }
        else return TimeSpan.Zero;

        // Independently truncating PTS and duration would lose their nanosecond
        // carry and create false gaps. Both boundaries use the same conversion.
        var ticks = checked((long)(endTicks - startNanoseconds / 100));
        if (ticks <= 0) throw new InvalidDataException("媒体帧时间精度超出当前播放支持范围。");
        return TimeSpan.FromTicks(ticks);
    }

    internal static bool CoversPreroll(TimeSpan start, TimeSpan duration, TimeSpan target)
    {
        if (start > target) return false;
        if (duration > TimeSpan.Zero) return target - start < duration;
        // GstVideoDecoder may clip an unknown-duration frame to a nonzero seek
        // target using an estimated end time. Matching PTS cannot prove its identity.
        return target == TimeSpan.Zero && start == TimeSpan.Zero;
    }

    internal static FrameCoverage Coverage(TimeSpan start, TimeSpan duration, TimeSpan? next,
        TimeSpan target, bool ended)
    {
        if (start > target) return FrameCoverage.Gap;
        if (next is { } nextStart && nextStart <= target) return FrameCoverage.Advance;
        if (duration > TimeSpan.Zero && target - start < duration) return FrameCoverage.Covered;
        if (duration <= TimeSpan.Zero && target == start) return FrameCoverage.Covered;
        if (next is null) return ended ? FrameCoverage.Ended : FrameCoverage.NeedNext;
        return duration > TimeSpan.Zero ? FrameCoverage.Gap : FrameCoverage.Covered;
    }
}
