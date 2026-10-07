using Avalonia;
using System.Net;
using System.Text;
using TractorTool.Plugins.SystemHud;

if (args is ["--ui-only", var output]) { HudVisualChecks.Run(output); return; }

// Pure logic and in-memory HTTP checks: no windows, native counters, user settings or real network.
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
Check(new HudSettings { Anchor = 8 }.Position(area, 1) == new PixelPoint(-268, 1016), "Bottom-right respects secondary-monitor origin and inset.");
Check(new HudSettings { Anchor = 0, OffsetX = -1000, OffsetY = -1000 }.Position(area, 1) == new PixelPoint(-1920, 0), "Offsets cannot lose the HUD off-screen.");
Check(new HudSettings { Anchor = 8, Scale = 2 }.Position(new(0, 0, 400, 200), 2) == new PixelPoint(0, 0), "Oversized HUD clamps to the available origin.");
var dragged = new HudSettings().AtPosition(new(-1300, 500), area, 1.5, "secondary", 2);
Check(dragged.Position(area, 1.5) == new PixelPoint(-1300, 500), "Dragging must round-trip physical position on a negative-origin, scaled monitor.");
var restored = System.Text.Json.JsonSerializer.Deserialize<HudSettings>(System.Text.Json.JsonSerializer.Serialize(dragged))!;
Check(restored == dragged && restored.Position(area, 1.5) == new PixelPoint(-1300, 500), "Saved drag position and monitor must survive restart.");
var bottom = new HudSettings().AtPosition(new(5000, 5000), area, 1, "secondary", 2);
Check(bottom.Position(area, 1) == new PixelPoint(-248, 1036) && bottom.Position(new(0, 0, 1280, 720), 2) == new PixelPoint(784, 632),
    "Dragging outside the work area and restoring on another DPI must keep the entire HUD visible.");
Check(bottom.Position(new(0, 0, 100, 100), 2) == new PixelPoint(0, 0), "A work area smaller than the HUD must clamp safely.");
Check(!new HudSettings().ClickThrough && !new HudSettings { PositionX = double.NaN, PositionY = .5 }.Normalize().HasPosition
    && !new HudSettings { PositionX = .5 }.Normalize().HasPosition,
    "New HUDs must allow dragging; corrupt or partial positions must fall back to legacy placement.");
Check(System.Text.Json.JsonSerializer.Deserialize<HudSettings>("{\"ClickThrough\":true,\"Anchor\":8}") is { ClickThrough: true, HasPosition: false },
    "Existing explicit click-through preferences and anchor-only configurations remain compatible.");
var expandedSize = HudSettings.SizeFor(HudMode.Detail);
Check(bottom.Position(area, 1, expandedSize) == new PixelPoint(-400, 920)
    && bottom.Position(new(0, 0, 1280, 720), 2, expandedSize) == new PixelPoint(480, 400)
    && bottom.Position(area, 1) == new PixelPoint(-248, 1036),
    "Expansion fits all edges without rewriting the compact anchor, including high DPI.");
Check(dragged.Position(area, 1.5, expandedSize) == new PixelPoint(-1414, 413)
    && bottom.Position(new(0, 0, 100, 100), 2, expandedSize) == new PixelPoint(0, 0),
    "Expansion grows around the original centre and safely clamps an undersized work area.");
var expandedDrop = new HudSettings().AtPosition(new(5000, 5000), area, 1, "secondary", 2, expandedSize);
Check(expandedDrop.Position(area, 1, expandedSize) == new PixelPoint(-400, 920)
    && expandedDrop.Position(area, 1) == new PixelPoint(-324, 978),
    "An expanded drop stores the clamped centre; collapse shrinks around that centre.");
Check(HudWindow.ContainsIsland(new(216, 104), new(248, 44), 22)
    && !HudWindow.ContainsIsland(new(92, 82), new(248, 44), 22)
    && !HudWindow.ContainsIsland(new(10, 104), expandedSize, 28),
    "Transparent envelope and rounded corners must pass pointer input through.");
var spring = HudCard.MorphEasing();
var overshoots = false;
foreach (var from in Enum.GetValues<HudMode>())
foreach (var to in Enum.GetValues<HudMode>())
for (var step = 0; step <= 100; step++)
{
    var progress = spring.Ease(step / 100d);
    overshoots |= progress > 1;
    var start = HudSettings.SizeFor(from); var end = HudSettings.SizeFor(to);
    var size = new Size(start.Width + (end.Width - start.Width) * progress, start.Height + (end.Height - start.Height) * progress);
    Check(double.IsFinite(progress) && size.Width > 0 && size.Width <= HudWindow.FrameWidth && size.Height > 0 && size.Height <= HudWindow.FrameHeight,
        "The spring stays inside the transparent native envelope.");
    var position = bottom.Position(area, 1.5, size);
    Check(position.X >= area.X && position.Y >= area.Y
        && position.X + Math.Ceiling(size.Width * 1.5) <= area.Right && position.Y + Math.Ceiling(size.Height * 1.5) <= area.Bottom,
        "Every intermediate morph size stays on-screen, including spring overshoot.");
}
Check(overshoots && Math.Abs(spring.Ease(1) - 1) < .001, "The nonlinear morph has a small overshoot and settles at its target.");
Check(!HudCard.ExceedsDragThreshold(new(5, -5)) && HudCard.ExceedsDragThreshold(new(6, 0))
    && HudCard.ExceedsDragThreshold(new(0, -6)), "Small click jitter must not start a drag; either axis can cross the threshold.");
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
Check(!unknown.Layout.Strip.Right.IsRing && unknown.Value == "—%" && ((Avalonia.Media.ISolidColorBrush)unknown.Accent).Color == Avalonia.Media.Color.Parse("#FFE66B"),
    "Unknown readings must not become a false zero or trigger a color rule.");
var ring = high.Layout.Strip.Right;
Check(ring.SweepAngle == 288 && (ring with { Percent = 100 }).SweepAngle == 360
    && (ring with { Percent = -1 }).SweepAngle == 0 && (ring with { Percent = 150 }).SweepAngle == 360
    && !(ring with { Percent = double.NaN }).HasPercent && unknown.Layout.Strip.Right.SweepAngle == 0,
    "The ring uses bounded finite percentages; non-percentage readings do not invent progress.");
Check(high.Summary.Contains(high.Title) && high.Summary.Contains(high.Detail)
    && new HudProfile { Metric = 9 }.Render(new("余额", "14.20 CNY", "已更新")).Layout.Compact.Left.Icon != high.Layout.Compact.Left.Icon,
    "Compact identity follows the metric while full template content stays available to details and accessibility.");
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

Check(HudLayout.AfterClick(HudMode.Compact, false) == HudMode.Strip
    && HudLayout.AfterClick(HudMode.Strip, false) == HudMode.Compact
    && HudLayout.AfterClick(HudMode.Detail, false) == HudMode.Detail
    && HudLayout.AfterClick(HudMode.Compact, true) == HudMode.Detail
    && HudLayout.AfterClick(HudMode.Strip, true) == HudMode.Detail
    && HudLayout.AfterClick(HudMode.Detail, true) == HudMode.Compact,
    "Single and double clicks follow all six state transitions; details always have a return path.");
var memoryLayout = new HudProfile { Metric = 3 }.Render(new("内存", "41%", "13.2 / 32 GiB", 41, Context: "可用 18.8 GiB · 物理内存")).Layout;
Check(memoryLayout.Compact is { Left.IsIcon: true, Center.Text: "41%", Right.Text: "内存" }
    && memoryLayout.Strip is { Primary.Text: "13.2 / 32 GiB", Secondary.Text: "41%", Right.IsRing: true }
    && memoryLayout.Expanded is { Primary.Text: "41%", Secondary.Text: "内存", Right.Text: "13.2 / 32 GiB" }
    && memoryLayout.Detail.Text == "可用 18.8 GiB · 物理内存",
    "Memory separates capacity/usage from the available-memory explanation.");
var networkLayout = new HudProfile { Metric = 4 }.Render(new("网速", "↓ 60.6 KiB/s", "↑ 43.0 KiB/s · 活动接口合计", SecondaryValue: "↑ 43.0 KiB/s", Context: "2 个活动接口合计")).Layout;
Check(networkLayout.Strip is { Left.IsIcon: true, Primary.Text: "↓ 60.6 KiB/s", Secondary.Text: "↑ 43.0 KiB/s", Right.IsText: true, Right.Text: "网速" }
    && networkLayout.Detail.Text == "2 个活动接口合计",
    "Network rates stay separate and concise; the full explanation is retained in details, without a duplicate icon.");
var gpuLayout = new HudProfile { Metric = 8, SensorId = "/gpu-nvidia/0/temperature/2" }.Render(new("GPU Hot Spot", "43.5 °C", "NVIDIA GPU")).Layout;
Check(gpuLayout.Compact.Right.IsIcon && gpuLayout.Compact.Right.Icon == HudDisplay.IconFor(12)
    && gpuLayout.Compact.Left.Icon != gpuLayout.Compact.Right.Icon, "GPU sensor identity is distinct from the temperature icon.");
var oldProfile = System.Text.Json.JsonSerializer.Deserialize<HudProfile>(
    """{"name":"旧方案","metric":4,"title":"{name}","layout":{"compactLeft":{"content":99}}}""",
    System.Text.Json.JsonSerializerOptions.Web)!;
Check(oldProfile.Name == "旧方案" && oldProfile.Metric == 4 && oldProfile.Title == "{name}" && oldProfile.Error == ""
    && !System.Text.Json.JsonSerializer.Serialize(oldProfile, System.Text.Json.JsonSerializerOptions.Web).Contains("\"layout\""),
    "Old user-layout fields are ignored without losing profile data and are absent on the next ordinary save.");
for (var metric = 0; metric <= 11; metric++)
{
    var layout = new HudProfile { Metric = metric }.Render(new("指标", "—", "尚无读数")).Layout;
    Check(layout.Compact.Center.Text == "—" && layout.Strip.Primary.Text == "—" && layout.Detail.IsText
        && layout.Strip.Right.IsText && !layout.Strip.Right.IsRing
        && layout.Strip.Secondary.Text == (metric == 4 ? "↑ —" : metric == 0 ? "尚无读数" : "指标"),
        "All metrics share named regions; absent percentages do not invent progress or repeat the metric icon.");
}
Console.WriteLine("PASS HUD three-state transitions, built-in regions and legacy profile compatibility");

Check(NetworkMetrics.ValidAddress("localhost", true) && NetworkMetrics.ValidAddress("::1", true)
    && !NetworkMetrics.ValidAddress("localhost:443", true) && !NetworkMetrics.ValidAddress("https://example.com/path", true), "Probe accepts a host, not a URL or port.");
Check(NetworkMetrics.ValidAddress("http://localhost/status", false)
    && !NetworkMetrics.ValidAddress("file:///C:/private", false)
    && !NetworkMetrics.ValidAddress("https://user:secret@example.com", false)
    && !NetworkMetrics.ValidAddress("https://example.com/#secret", false), "HTTP accepts only explicit web endpoints without embedded credentials or fragments.");
Check(NetworkMetrics.ValidPointer("") && NetworkMetrics.ValidPointer("/a~1b/~0key/0")
    && !NetworkMetrics.ValidPointer("/bad~2") && !NetworkMetrics.ValidPointer("field"), "Validate JSON Pointer escaping before reading.");
var jsonProfile = new HudProfile { Metric = 7, Address = "https://example.invalid/status", JsonPointer = "/a~1b/~0key/0", JsonPercentage = true };
var jsonBytes = Encoding.UTF8.GetBytes("{\"a/b\":{\"~key\":[72,null]}}");
Check(NetworkMetrics.ExtractJson(jsonBytes, jsonProfile) is { Error: null, Reading.Percent: 72 }, "JSON Pointer supports escaped keys and array indices.");
foreach (var pointer in new[] { "/a~1b/~0key/01", "/a~1b/~0key/-1", "/a~1b/~0key/2", "/a~1b/~0key/1", "/a~1b", "/missing" })
    Check(NetworkMetrics.ExtractJson(jsonBytes, jsonProfile with { JsonPointer = pointer, JsonPercentage = false }).Error is not null, "Invalid/non-scalar JSON selection must be unknown: " + pointer);
foreach (var json in new[] { "\"72\"", "101", "-1", "1e999", "not-json", new string('[', 33) + "0" + new string(']', 33) })
    Check(NetworkMetrics.ExtractJson(Encoding.UTF8.GetBytes(json), jsonProfile with { JsonPointer = "" }).Error is not null, "Invalid percentage or JSON must not become a value.");
Check(NetworkMetrics.ExtractJson(new byte[NetworkMetrics.MaxBytes + 1], jsonProfile).Error is not null, "Parser keeps the response size bound.");
Check(NetworkMetrics.ExtractJson(Encoding.UTF8.GetBytes("\"a\\nb\""), jsonProfile with { JsonPointer = "", JsonPercentage = false }).Reading.Value == "a b", "Remote text cannot inject a new display line.");
var balance = Encoding.UTF8.GetBytes("{\"balance_infos\":[{\"currency\":\"USD\",\"total_balance\":\"12.34\"}]}");
Check(NetworkMetrics.DeepSeekBalance(balance, "USD") is { Error: null, Reading.Unit: "USD" }
    && NetworkMetrics.DeepSeekBalance(balance, "CNY").Error is not null
    && NetworkMetrics.DeepSeekBalance("[]"u8.ToArray(), "USD").Error is not null, "Balance selects the exact currency and rejects malformed responses.");
Check(HudSecrets.ValidKey("test-key") && !HudSecrets.ValidKey("key\nsecret") && !HudSecrets.ValidKey(new string('a', 1025)), "Credential header rejects whitespace and unbounded input.");
Check((jsonProfile with { RefreshSeconds = 4 }).Error.Length > 0
    && new HudProfile { Metric = 9, RefreshSeconds = 59 }.Error.Length > 0
    && new HudProfile { Metric = 10, Drive = "\\\\server\\share" }.Error.Length > 0, "Refresh and local-drive limits apply to saved profiles too.");
Check(HardwareMetrics.Unit(LibreHardwareMonitor.Hardware.SensorType.Temperature) == "°C", "Hardware temperature units must not be percentages.");
var newProfiles = new HudProfiles { Profiles = [jsonProfile], ActiveId = jsonProfile.Id };
var newRoundtrip = System.Text.Json.JsonSerializer.Deserialize<HudProfiles>(System.Text.Json.JsonSerializer.Serialize(newProfiles))!.Normalize(0);
Check(newRoundtrip.Active == jsonProfile && newRoundtrip.Active.Render(new("JSON", "72", "OK", 72, "%")).Detail == "OK", "New source fields round-trip without embedding secrets.");
Check(HudProfile.ValidTemplate("{unit} {updated}", 80), "Extended template variables are accepted.");

var requests = 0;
using (var sampler = new HudSampler(new NetworkMetrics(new MemoryHandler((request, _) =>
{
    requests++;
    Check(request.Headers.Authorization is null, "Generic HTTP must never inherit a DeepSeek token.");
    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(jsonBytes) });
}))))
{
    Check((await sampler.ReadAsync(jsonProfile, false, false, "secret", default))?.Error is null, "JSON request uses in-memory handler.");
    await sampler.ReadAsync(jsonProfile, false, false, "secret", default);
    Check(requests == 1, "Carousel re-entry must honor the profile request interval.");
    sampler.ResetNetworkCache();
    await sampler.ReadAsync(jsonProfile, false, false, "new-secret", default);
    Check(requests == 2, "Changed credentials/settings invalidate cached results.");
}
using (var network = new NetworkMetrics(new MemoryHandler((request, _) =>
{
    Check(request.RequestUri!.AbsoluteUri == "https://api.deepseek.com/user/balance"
        && request.Headers.Authorization?.ToString() == "Bearer test-key", "Credential is sent only to the fixed DeepSeek HTTPS endpoint.");
    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(balance) });
})))
    Check((await network.ReadAsync(new HudProfile { Metric = 9, Currency = "USD", RefreshSeconds = 60 }, default, "test-key"))?.Error is null, "DeepSeek reads real response fields.");
using (var network = new NetworkMetrics(new MemoryHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)))))
    Check((await network.ReadAsync(jsonProfile, default))?.Error?.Contains("302") == true, "Redirects are shown as failures, not followed.");
using (var network = new NetworkMetrics(new MemoryHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    { Content = new UnknownLengthContent(new byte[NetworkMetrics.MaxBytes + 1]) }))))
    Check((await network.ReadAsync(jsonProfile, default))?.Error?.Contains("64 KiB") == true, "Streaming responses without Content-Length are bounded too.");
var cancelledRequests = 0;
using (var sampler = new HudSampler(new NetworkMetrics(new MemoryHandler(async (_, token) =>
{
    cancelledRequests++;
    await Task.Delay(Timeout.Infinite, token);
    return new HttpResponseMessage(HttpStatusCode.OK);
}))))
using (var cancellation = new CancellationTokenSource())
{
    var pending = sampler.ReadAsync(jsonProfile, false, false, "", cancellation.Token);
    cancellation.Cancel();
    Check(await pending is null, "Normal cancellation has no failure reading.");
    var next = await sampler.ReadAsync(jsonProfile, false, false, "", default);
    Check(cancelledRequests == 1 && next?.Reading.Value == "—" && next?.Reading.ObservedAt is null,
        "Cancelled carousel requests cannot bypass the refresh limit or be treated as a fresh recovered reading.");
}
Console.WriteLine("PASS HUD source validation, JSON, credentials routing, response bounds, caching and cancellation");

sealed class MemoryHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request, token);
}
sealed class UnknownLengthContent(byte[] bytes) : HttpContent
{
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
}
