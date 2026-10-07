using Avalonia;
using TractorTool.Contracts;

namespace TractorTool.Plugins.SystemHud;

public sealed record HudSettings : IVersionedSettings
{
    public int SchemaVersion { get; init; } = 1;
    public bool Enabled { get; init; } = true;
    public bool AlwaysVisible { get; init; }
    public bool PowerReminder { get; init; } = true;
    public bool ClickThrough { get; init; } = true;
    public bool Animate { get; init; } = true;
    public int Metric { get; init; }
    public int Anchor { get; init; } = 2;
    public int Monitor { get; init; }
    public double Scale { get; init; } = 1;
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }

    public HudSettings Normalize() => this with
    {
        Metric = Math.Clamp(Metric, 0, 4), Anchor = Math.Clamp(Anchor, 0, 8),
        Monitor = Math.Max(0, Monitor), Scale = Clamp(Scale, .6, 2, 1),
        OffsetX = Clamp(OffsetX, -1000, 1000, 0), OffsetY = Clamp(OffsetY, -1000, 1000, 0)
    };

    private static double Clamp(double value, double min, double max, double fallback)
        => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    // WorkingArea and window position use physical pixels; settings offsets use DIPs.
    public PixelPoint Position(PixelRect area, double dpi)
    {
        var settings = Normalize();
        var width = (int)Math.Ceiling(320 * settings.Scale * dpi);
        var height = (int)Math.Ceiling(124 * settings.Scale * dpi);
        var margin = 20 * dpi;
        var x = area.X + margin + (area.Width - width - 2 * margin) * (settings.Anchor % 3) / 2;
        var y = area.Y + margin + (area.Height - height - 2 * margin) * (settings.Anchor / 3) / 2;
        return new((int)Math.Clamp(x + settings.OffsetX * dpi, area.X, Math.Max(area.X, area.Right - width)),
            (int)Math.Clamp(y + settings.OffsetY * dpi, area.Y, Math.Max(area.Y, area.Bottom - height)));
    }
}

public sealed record HudReading(string Label, string Value, string Detail, double Percent = 0)
{
    public bool HasPercent => Label is "电量" or "CPU" or "内存";
}

public static class HudCounters
{
    public static double? Cpu(ulong idle, ulong kernel, ulong user, ulong oldIdle, ulong oldKernel, ulong oldUser)
    {
        if (idle < oldIdle || kernel < oldKernel || user < oldUser) return null;
        var total = (double)(kernel - oldKernel) + (user - oldUser);
        return total <= 0 ? null : Math.Clamp(100 * (1 - (idle - oldIdle) / total), 0, 100);
    }

    public static double? Rate(long bytes, long oldBytes, double seconds)
        => bytes < oldBytes || seconds <= 0 || !double.IsFinite(seconds) ? null : (bytes - oldBytes) / seconds;

    public static string Speed(double value) => value >= 1024 * 1024
        ? $"{value / (1024 * 1024):0.0} MiB/s" : $"{value / 1024:0.0} KiB/s";
}
