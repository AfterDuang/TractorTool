using Avalonia;
using TractorTool.Contracts;

namespace TractorTool.Plugins.SystemHud;

public sealed record HudSettings : IVersionedSettings
{
    // Shared by the card, native window and screen-bound positioning (DIPs).
    public const double CardWidth = 360;
    public const double CardHeight = 60;
    public const double ExpandedHeight = 224;

    public int SchemaVersion { get; init; } = 1;
    public bool Enabled { get; init; } = true;
    public bool AlwaysVisible { get; init; }
    public bool PowerReminder { get; init; } = true;
    public bool ClickThrough { get; init; }
    public bool Animate { get; init; } = true;
    public bool HardwareEnabled { get; init; }
    public int Metric { get; init; }
    public int Anchor { get; init; } = 2;
    public int Monitor { get; init; }
    public double Scale { get; init; } = 1;
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }
    public string? ScreenName { get; init; }
    public double? PositionX { get; init; }
    public double? PositionY { get; init; }

    public HudSettings Normalize() => this with
    {
        Metric = Math.Clamp(Metric, 0, 4), Anchor = Math.Clamp(Anchor, 0, 8),
        Monitor = Math.Max(0, Monitor), Scale = Clamp(Scale, .6, 2, 1),
        OffsetX = Clamp(OffsetX, -1000, 1000, 0), OffsetY = Clamp(OffsetY, -1000, 1000, 0),
        PositionX = HasPosition ? Math.Clamp(PositionX!.Value, 0, 1) : null,
        PositionY = HasPosition ? Math.Clamp(PositionY!.Value, 0, 1) : null
    };

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasPosition => PositionX is { } x && double.IsFinite(x) && PositionY is { } y && double.IsFinite(y);

    private static double Clamp(double value, double min, double max, double fallback)
        => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    // WorkingArea and window position use physical pixels; settings offsets use DIPs.
    public PixelPoint Position(PixelRect area, double dpi, bool expanded = false)
    {
        var settings = Normalize();
        var width = (int)Math.Ceiling(CardWidth * settings.Scale * dpi);
        var height = (int)Math.Ceiling(CardHeight * settings.Scale * dpi);
        PixelPoint position;
        if (settings.HasPosition)
            position = new(area.X + (int)Math.Round(Math.Max(0, area.Width - width) * settings.PositionX!.Value),
                area.Y + (int)Math.Round(Math.Max(0, area.Height - height) * settings.PositionY!.Value));
        else
        {
            // Legacy anchor/offset fields still restore older installations until their first drag.
            var margin = 20 * dpi;
            var x = area.X + margin + (area.Width - width - 2 * margin) * (settings.Anchor % 3) / 2;
            var y = area.Y + margin + (area.Height - height - 2 * margin) * (settings.Anchor / 3) / 2;
            position = new((int)Math.Clamp(x + settings.OffsetX * dpi, area.X, Math.Max(area.X, area.Right - width)),
                (int)Math.Clamp(y + settings.OffsetY * dpi, area.Y, Math.Max(area.Y, area.Bottom - height)));
        }
        // Expansion is transient: keep the compact anchor, moving up only at the screen edge.
        var visibleHeight = (int)Math.Ceiling((expanded ? ExpandedHeight : CardHeight) * settings.Scale * dpi);
        return new(position.X, Math.Clamp(position.Y, area.Y, Math.Max(area.Y, area.Bottom - visibleHeight)));
    }

    public HudSettings AtPosition(PixelPoint position, PixelRect area, double dpi, string? screenName, int monitor, bool expanded = false)
    {
        var settings = Normalize();
        var travelX = Math.Max(0, area.Width - Math.Ceiling(CardWidth * settings.Scale * dpi));
        var travelY = Math.Max(0, area.Height - Math.Ceiling(CardHeight * settings.Scale * dpi));
        var visibleTravelY = Math.Max(0, area.Height - Math.Ceiling((expanded ? ExpandedHeight : CardHeight) * settings.Scale * dpi));
        return settings with
        {
            ScreenName = screenName, Monitor = monitor,
            PositionX = travelX == 0 ? 0 : Math.Clamp((position.X - (double)area.X) / travelX, 0, 1),
            // Save the visible drop location using the compact coordinate system for both states.
            PositionY = travelY == 0 ? 0 : Math.Clamp(position.Y - (double)area.Y, 0, visibleTravelY) / travelY
        };
    }
}

public sealed record HudReading(string Label, string Value, string Detail, double? Percent = null, string Unit = "", DateTimeOffset? ObservedAt = null);

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
