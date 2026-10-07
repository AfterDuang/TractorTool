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
