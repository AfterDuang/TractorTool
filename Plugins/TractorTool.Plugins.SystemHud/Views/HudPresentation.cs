using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace TractorTool.Plugins.SystemHud;

public static class HudPresentation
{
    public static HudDisplay Render(this HudProfile profile, HudReading reading)
    {
        string Expand(string template) => HudProfile.Token.Replace(template, m => m.Groups[1].Value switch
        {
            "label" => reading.Label, "value" => reading.Value, "detail" => reading.Detail,
            "name" => profile.Name, "percent" => reading.Percent?.ToString("0.#", CultureInfo.CurrentCulture) ?? "—",
            "unit" => reading.Unit, "updated" => reading.ObservedAt?.ToString("HH:mm:ss", CultureInfo.CurrentCulture) ?? "—", _ => ""
        });
        // ponytail: one percentage rule per profile; add a rule list only when real layouts need it.
        var match = profile.RuleEnabled && reading.Percent is { } p && (profile.Comparison == 0 ? p <= profile.Threshold : p >= profile.Threshold);
        var title = Expand(profile.Title); var value = Expand(profile.Value); var detail = Expand(profile.Detail);
        var accent = new ImmutableSolidColorBrush(Color.Parse(match ? profile.RuleColor : profile.Color));
        HudSlotDisplay Text(string text) => new(HudContent.Text, text, "", null, accent);
        HudSlotDisplay Icon(int metric) => new(HudContent.Icon, "", HudDisplay.IconFor(metric), null, accent);
        var icon = profile.Metric == 8
            ? new HudSlotDisplay(HudContent.Icon, "", HudDisplay.HardwareIconFor(HardwareMetrics.UnitFor(profile.SensorId)), null, accent)
            : Icon(profile.Metric);
        var identity = profile.Metric == 8 && profile.SensorId.StartsWith("/gpu-", StringComparison.OrdinalIgnoreCase)
            ? Icon(12) : Text(title);
        var hasPercent = reading.Percent is { } percent && double.IsFinite(percent);
        var trailing = hasPercent ? new HudSlotDisplay(HudContent.Progress, "—", icon.Icon, reading.Percent, accent) : Text("");

        // Metric-specific choices end here. Views consume these named regions for all three states.
        var strip = new HudStripLayout(icon, Text(value), Text(title), trailing);
        if (profile.Metric == 0) strip = strip with { Secondary = Text(detail) };
        if (profile.Metric == 3 && hasPercent) // Capacity and usage are separate, readable fields.
            strip = strip with { Primary = Text(detail), Secondary = Text(value) };
        if (profile.Metric == 4) // Never parse a localized explanation to recover the upload reading.
            strip = strip with { Secondary = Text(reading.SecondaryValue ?? "↑ —"), Right = Text(title) };
        // Expanded: icon/name form the leading group; supporting/primary readings form the trailing group.
        // Reuse the four regions, but do not carry strip-only ordering or duplicate icons into details.
        var expanded = new HudStripLayout(icon, Text(value), Text(title), trailing with { Icon = "" });
        if ((profile.Metric is 0 or 3) && hasPercent && profile.Detail == "{detail}")
            expanded = expanded with { Right = Text(detail) };
        if (profile.Metric == 4) expanded = expanded with { Right = strip.Secondary };
        var body = profile.Detail == "{detail}" ? reading.Context?.ReplaceLineEndings("；") ?? detail : detail;
        var headerText = new[] { expanded.Primary.Text, expanded.Secondary.Text, expanded.Right.Text };
        if (headerText.Contains(body)) body = "";
        var updated = reading.ObservedAt?.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        var footer = updated is not null && !headerText.Append(body).Any(t => t.Contains(updated)) ? $"更新于 {updated}" : "";
        var layout = new HudLayout(new(icon, Text(value), identity), strip, expanded, Text(body));
        return new(title, value, detail, accent, layout, footer) { ProfileId = profile.Id, Metric = profile.Metric };
    }

    internal static HudReading HardwareExample(string? id) => HardwareMetrics.UnitFor(id) switch
    {
        "°C" => new("温度", "48 °C", "示例：温度传感器", Unit: "°C"),
        "W" => new("功率", "65 W", "示例：功率传感器", Unit: "W"),
        "%" => new("占用率", "36%", "示例：负载传感器", 36, "%"),
        "MHz" => new("频率", "4200 MHz", "示例：时钟频率", Unit: "MHz"),
        "RPM" => new("风扇", "1200 RPM", "示例：风扇转速", Unit: "RPM"),
        "V" => new("电压", "1.2 V", "示例：电压传感器", Unit: "V"),
        "GiB" => new("容量", "8 GiB", "示例：容量传感器", Unit: "GiB"),
        "MiB" => new("容量", "512 MiB", "示例：容量传感器", Unit: "MiB"),
        _ => new("硬件传感器", "—", "示例：请选择传感器")
    };
}

public sealed record HudDisplay(string Title, string Value, string Detail, IBrush Accent, HudLayout Layout, string Updated)
{
    public Guid ProfileId { get; init; }
    public int Metric { get; init; }
    public string Summary => $"{Title} · {Value}\n{Detail}";
    internal static string IconFor(int icon) => Icons[Math.Clamp(icon, 0, Icons.Length - 1)];
    internal static string HardwareIconFor(string unit) => IconFor(unit switch
    {
        "°C" => 8, "W" => 13, "%" => 14, "MHz" => 1, "RPM" => 15, "V" => 16, "GiB" or "MiB" => 3, _ => 2
    });

    // Small original line icons; no font glyph dependency or downloaded artwork.
    private static readonly string[] Icons =
    {
        "M3,7 L19,7 19,17 3,17 Z M21,10 L21,14 M6,10 L6,14 M9,10 L9,14 M12,10 L12,14", // battery
        "M12,2 A10,10 0 1 1 12,22 A10,10 0 1 1 12,2 M12,6 L12,12 16,14", // clock
        "M6,6 L18,6 18,18 6,18 Z M9,9 L15,9 15,15 9,15 Z M9,2 L9,6 M15,2 L15,6 M9,18 L9,22 M15,18 L15,22 M2,9 L6,9 M2,15 L6,15 M18,9 L22,9 M18,15 L22,15", // CPU
        "M3,6 L21,6 21,17 3,17 Z M7,9 L7,13 M12,9 L12,13 M17,9 L17,13 M6,17 L6,21 M10,17 L10,21 M14,17 L14,21 M18,17 L18,21", // memory
        "M7,3 L7,21 M3,17 L7,21 11,17 M17,21 L17,3 M13,7 L17,3 21,7", // transfer
        "M2,12 L6,12 9,5 14,19 17,12 22,12", // latency
        "M12,2 A10,10 0 1 1 12,22 A10,10 0 1 1 12,2 M2,12 L22,12 M12,2 C6,8 6,16 12,22 C18,16 18,8 12,2", // HTTP
        "M7,3 L4,3 4,10 2,12 4,14 4,21 7,21 M17,3 L20,3 20,10 22,12 20,14 20,21 17,21 M10,12 L14,12", // JSON
        "M9,4 A3,3 0 0 1 15,4 L15,13 A5,5 0 1 1 9,13 Z M12,6 L12,18", // sensor
        "M3,5 L21,5 21,20 3,20 Z M3,5 L17,2 17,5 M16,10 L21,10 M16,15 L21,15 M16,10 L16,15", // balance
        "M5,3 L19,3 22,18 22,21 2,21 2,18 Z M2,16 L22,16 M16,19 L19,19", // disk
        "M12,3 A9,9 0 1 1 3,12 M3,3 L3,9 9,9 M12,6 L12,12 16,12", // uptime
        "M2,5 L22,5 22,18 2,18 Z M7,8 A3.5,3.5 0 1 1 7,15 A3.5,3.5 0 1 1 7,8 M17,8 A3.5,3.5 0 1 1 17,15 A3.5,3.5 0 1 1 17,8 M6,18 L6,21 M10,18 L10,21 M14,18 L14,21", // GPU
        "M14,5 L6,13 11,13 10,19 18,11 13,11 Z", // power
        "M5,18 A9,9 0 1 1 19,18 M6,17 L18,17 M12,13 L16,7 M6,10 L8,11 M12,4 L12,6 M18,10 L16,11", // load
        "M12,10 C4,1 3,12 10,12 M14,12 C23,4 12,3 12,10 M12,14 C20,23 21,12 14,12 M10,12 C1,20 12,21 12,14 M12,10 A2,2 0 1 1 12,14 A2,2 0 1 1 12,10", // fan
        "M12,2 A10,10 0 1 1 12,22 A10,10 0 1 1 12,2 M7,7 L12,17 17,7" // voltage
    };
}

public sealed record HudSlotDisplay(HudContent Content, string Text, string Icon, double? Percent, IBrush Accent)
{
    public bool HasPercent => Percent is { } p && double.IsFinite(p);
    public bool IsRing => Content == HudContent.Progress && HasPercent;
    public bool IsIcon => Content == HudContent.Icon;
    public bool IsText => !IsIcon && !IsRing;
    public double SweepAngle => HasPercent ? Math.Clamp(Percent!.Value, 0, 100) * 3.6 : 0;
}
