using Avalonia;
using System.Net;
using System.Text;
using TractorTool.Plugins.SystemHud;

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
Check(new HudSettings { Anchor = 8 }.Position(area, 1) == new PixelPoint(-380, 1000), "Bottom-right respects secondary-monitor origin and inset.");
Check(new HudSettings { Anchor = 0, OffsetX = -1000, OffsetY = -1000 }.Position(area, 1) == new PixelPoint(-1920, 0), "Offsets cannot lose the HUD off-screen.");
Check(new HudSettings { Anchor = 8, Scale = 2 }.Position(new(0, 0, 400, 200), 2) == new PixelPoint(0, 0), "Oversized HUD clamps to the available origin.");
var dragged = new HudSettings().AtPosition(new(-1300, 500), area, 1.5, "secondary", 2);
Check(dragged.Position(area, 1.5) == new PixelPoint(-1300, 500), "Dragging must round-trip physical position on a negative-origin, scaled monitor.");
var restored = System.Text.Json.JsonSerializer.Deserialize<HudSettings>(System.Text.Json.JsonSerializer.Serialize(dragged))!;
Check(restored == dragged && restored.Position(area, 1.5) == new PixelPoint(-1300, 500), "Saved drag position and monitor must survive restart.");
var bottom = new HudSettings().AtPosition(new(5000, 5000), area, 1, "secondary", 2);
Check(bottom.Position(area, 1) == new PixelPoint(-360, 1020) && bottom.Position(new(0, 0, 1280, 720), 2) == new PixelPoint(560, 600),
    "Dragging outside the work area and restoring on another DPI must keep the entire HUD visible.");
Check(bottom.Position(new(0, 0, 100, 100), 2) == new PixelPoint(0, 0), "A work area smaller than the HUD must clamp safely.");
Check(!new HudSettings().ClickThrough && !new HudSettings { PositionX = double.NaN, PositionY = .5 }.Normalize().HasPosition
    && !new HudSettings { PositionX = .5 }.Normalize().HasPosition,
    "New HUDs must allow dragging; corrupt or partial positions must fall back to legacy placement.");
Check(System.Text.Json.JsonSerializer.Deserialize<HudSettings>("{\"ClickThrough\":true,\"Anchor\":8}") is { ClickThrough: true, HasPosition: false },
    "Existing explicit click-through preferences and anchor-only configurations remain compatible.");
Check(bottom.Position(area, 1, expanded: true) == new PixelPoint(-360, 856)
    && bottom.Position(new(0, 0, 1280, 720), 2, expanded: true) == new PixelPoint(560, 272)
    && bottom.Position(area, 1) == new PixelPoint(-360, 1020),
    "Expansion fits above the screen edge without rewriting the compact anchor, including high DPI.");
Check(dragged.Position(area, 1.5, expanded: true) == dragged.Position(area, 1.5)
    && bottom.Position(new(0, 0, 100, 100), 2, expanded: true) == new PixelPoint(0, 0),
    "Expansion keeps an interior anchor and safely clamps a work area smaller than the panel.");
var expandedDrop = new HudSettings().AtPosition(new(5000, 5000), area, 1, "secondary", 2, expanded: true);
Check(expandedDrop.Position(area, 1, expanded: true) == new PixelPoint(-360, 856)
    && expandedDrop.Position(area, 1) == new PixelPoint(-360, 856),
    "Dragging an expanded panel past the bottom must persist the clamped location without a later collapse jump.");
Check(!HudWindow.ExceedsDragThreshold(new(5, -5)) && HudWindow.ExceedsDragThreshold(new(6, 0))
    && HudWindow.ExceedsDragThreshold(new(0, -6)), "Small click jitter must not start a drag; either axis can cross the threshold.");
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
Check(high.SweepAngle == 288 && (high with { Percent = 100 }).SweepAngle == 360
    && (high with { Percent = -1 }).SweepAngle == 0 && (high with { Percent = 150 }).SweepAngle == 360
    && !(high with { Percent = double.NaN }).HasPercent && unknown.SweepAngle == 0,
    "The ring uses bounded finite percentages; non-percentage readings do not invent progress.");
Check(high.Summary.Contains(high.Title) && high.Summary.Contains(high.Detail)
    && new HudProfile { Metric = 9 }.Render(new("余额", "14.20 CNY", "已更新")).Icon != high.Icon,
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
