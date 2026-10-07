using System.Text.Json.Serialization;

namespace TractorTool.Plugins.SystemHud;

public enum HudMode { Compact, Strip, Detail }
public enum HudContent { Hidden, Icon, Title, Value, Description, Percent, Progress, Summary, Custom }

public sealed record HudSlot(HudContent Content = HudContent.Value, int Icon = -1, string Template = "")
{
    public bool IsValid(bool multiline) => Enum.IsDefined(Content) && Icon is >= -1 and <= 12
        && HudProfile.ValidTemplate(Template, multiline ? 320 : 120, multiline);
}

// Three compact regions, four strip regions, and one detail body; the detail header reuses the strip.
public sealed record HudLayout
{
    public HudSlot CompactLeft { get; init; } = new(HudContent.Icon);
    public HudSlot CompactCenter { get; init; } = new(HudContent.Value);
    public HudSlot CompactRight { get; init; } = new(HudContent.Title);
    public HudSlot StripLeft { get; init; } = new(HudContent.Icon);
    public HudSlot StripPrimary { get; init; } = new(HudContent.Value);
    public HudSlot StripSecondary { get; init; } = new(HudContent.Description);
    public HudSlot StripRight { get; init; } = new(HudContent.Progress);
    public HudSlot Detail { get; init; } = new(HudContent.Summary);

    public HudSlot[] Header(bool strip) => strip ? [StripLeft, StripPrimary, StripSecondary, StripRight]
        : [CompactLeft, CompactCenter, CompactRight];
    [JsonIgnore] public bool IsValid => Header(false).Concat(Header(true)).All(s => s is not null && s.IsValid(false))
        && Detail is not null && Detail.IsValid(true);

    public static HudMode AfterClick(HudMode mode, bool doubleClick) => doubleClick
        ? mode == HudMode.Detail ? HudMode.Compact : HudMode.Detail
        : mode switch { HudMode.Compact => HudMode.Strip, HudMode.Strip => HudMode.Compact, _ => HudMode.Detail };
}
