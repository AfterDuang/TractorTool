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
        return new(Expand(profile.Title), Expand(profile.Value), Expand(profile.Detail), reading.Percent,
            new ImmutableSolidColorBrush(Color.Parse(match ? profile.RuleColor : profile.Color)));
    }
}

public sealed record HudDisplay(string Title, string Value, string Detail, double? Percent, IBrush Accent)
{
    public bool HasPercent => Percent.HasValue;
    public double Progress => Percent ?? 0;
}
