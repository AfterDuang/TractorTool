namespace TractorTool.Plugins.SystemHud;

public enum HudMode { Compact, Strip, Detail }
public enum HudContent { Text, Icon, Progress }

// View data, not user configuration. Every metric fills the same named regions.
public sealed record HudCompactLayout(HudSlotDisplay Left, HudSlotDisplay Center, HudSlotDisplay Right);
public sealed record HudStripLayout(HudSlotDisplay Left, HudSlotDisplay Primary, HudSlotDisplay Secondary, HudSlotDisplay Right);
public sealed record HudLayout(HudCompactLayout Compact, HudStripLayout Strip, HudStripLayout Expanded, HudSlotDisplay Detail)
{
    public static HudMode AfterClick(HudMode mode, bool doubleClick) => doubleClick
        ? mode == HudMode.Detail ? HudMode.Compact : HudMode.Detail
        : mode switch { HudMode.Compact => HudMode.Strip, HudMode.Strip => HudMode.Compact, _ => HudMode.Detail };
}
