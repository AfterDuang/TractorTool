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
        return new(Expand(profile.Title), Expand(profile.Value), Expand(profile.Detail), reading.Percent, profile.Metric,
            new ImmutableSolidColorBrush(Color.Parse(match ? profile.RuleColor : profile.Color)));
    }
}

public sealed record HudDisplay(string Title, string Value, string Detail, double? Percent, int Metric, IBrush Accent)
{
    public bool HasPercent => Percent is { } p && double.IsFinite(p);
    public double SweepAngle => HasPercent ? Math.Clamp(Percent!.Value, 0, 100) * 3.6 : 0;
    public string Summary => $"{Title} · {Value}\n{Detail}";
    public string Icon => Icons[Math.Clamp(Metric, 0, Icons.Length - 1)];

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
        "M9,4 A3,3 0 0 1 15,4 L15,14 A6,6 0 1 1 9,14 Z M12,6 L12,17", // sensor
        "M3,5 L21,5 21,20 3,20 Z M3,5 L17,2 17,5 M16,10 L21,10 M16,15 L21,15 M16,10 L16,15", // balance
        "M5,3 L19,3 22,18 22,21 2,21 2,18 Z M2,16 L22,16 M16,19 L19,19", // disk
        "M12,3 A9,9 0 1 1 3,12 M3,3 L3,9 9,9 M12,6 L12,12 16,12" // uptime
    };
}
