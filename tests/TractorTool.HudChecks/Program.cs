using Avalonia;
using TractorTool.Plugins.SystemHud;

// Pure arithmetic checks only: no windows, native counters, user settings or network requests.
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
Check(HudCounters.Cpu(150, 250, 150, 100, 150, 100) is { } cpu && Math.Abs(cpu - 66.6666667) < .001,
    "Kernel time includes idle; CPU must subtract it from the kernel+user delta.");
Check(HudCounters.Cpu(100, 150, 100, 100, 150, 100) is null, "No elapsed CPU sample must be unknown.");
Check(HudCounters.Cpu(10, 10, 10, 100, 150, 100) is null, "Reset counters must not create a spike.");
Check(HudCounters.Rate(3072, 1024, 2) == 1024, "Network speed uses actual elapsed seconds.");
Check(HudCounters.Rate(100, 1000, 1) is null && HudCounters.Rate(100, 10, 0) is null,
    "Counter resets/zero time must not produce fake traffic.");
var invalid = new HudSettings { Metric = 99, Anchor = -1, Scale = double.NaN, OffsetX = double.PositiveInfinity };
Check(invalid.Normalize() is { Metric: 4, Anchor: 0, Scale: 1, OffsetX: 0 }, "Stored settings must be bounded.");
var area = new PixelRect(-1920, 0, 1920, 1080);
Check(new HudSettings { Anchor = 8 }.Position(area, 1) == new PixelPoint(-340, 936), "Bottom-right respects secondary-monitor origin and inset.");
Check(new HudSettings { Anchor = 0, OffsetX = -1000, OffsetY = -1000 }.Position(area, 1) == new PixelPoint(-1920, 0), "Offsets cannot lose the HUD off-screen.");
Check(new HudSettings { Anchor = 8, Scale = 2 }.Position(new(0, 0, 400, 200), 2) == new PixelPoint(0, 0), "Oversized HUD clamps to the available origin.");
Console.WriteLine("PASS HUD counters, configuration and positioning");

var legacy = new HudProfiles().Normalize(4);
Check(legacy.Profiles.Length == 1 && legacy.Active.Metric == 4, "First profile must inherit the old metric without overwriting old settings.");
var second = new HudProfile { Name = "CPU", Metric = 2 };
var profiles = (legacy with { Profiles = [legacy.Active, second], Seconds = 1 }).Normalize(0);
Check(profiles.Seconds == 3 && profiles.Next(legacy.ActiveId) == second && profiles.Next(second.Id) == legacy.Active,
    "Carousel must wrap in saved order and have a bounded interval.");
var rule = second with { Title = "{name} · {label}", Value = "{percent}%", RuleEnabled = true, Comparison = 1, Threshold = 80 };
var high = rule.Render(new("CPU", "80%", "真实读数", 80));
Check(high.Title == "CPU · CPU" && high.Value == "80%" && ((Avalonia.Media.ISolidColorBrush)high.Accent).Color == Avalonia.Media.Color.Parse("#FF8A90"),
    "Template and inclusive percentage threshold must agree between editor and HUD.");
var unknown = rule.Render(new("CPU", "—", "正在建立基线"));
Check(!unknown.HasPercent && unknown.Value == "—%" && ((Avalonia.Media.ISolidColorBrush)unknown.Accent).Color == Avalonia.Media.Color.Parse("#FFE66B"),
    "Unknown readings must not become a false zero or trigger a color rule.");
Check(HudProfile.ValidTemplate("{value}/{detail}", 80) && !HudProfile.ValidTemplate("{unknown}", 80)
    && !HudProfile.ValidTemplate("{value}}", 80) && !HudProfile.ValidColor("# 12345"), "Reject invalid templates and colors without throwing in the renderer.");
Check(rule.Render(new("{name}", "{label}", "{value}", 10)).Detail == "{value}", "Variable values are data, never recursively expanded.");
var roundtrip = System.Text.Json.JsonSerializer.Deserialize<HudProfiles>(System.Text.Json.JsonSerializer.Serialize(profiles))!.Normalize(0);
Check(roundtrip.ActiveId == profiles.ActiveId && roundtrip.Profiles.SequenceEqual(profiles.Profiles), "Profile JSON round-trip must preserve identities and options.");
var rejected = false;
try { (profiles with { Profiles = [second, second] }).Normalize(0); }
catch (InvalidDataException) { rejected = true; }
Check(rejected, "Duplicate identities in stored profiles must be rejected, not silently dropped.");
Console.WriteLine("PASS HUD profiles, templates, color rule and carousel order");
