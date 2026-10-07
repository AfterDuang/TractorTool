using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.Styling;
using TractorTool.Contracts;
using TractorTool.Plugins.SystemHud;

// No host lifetime, disk settings, native HUD window, sampling, credentials or network requests.
internal static class HudVisualChecks
{
    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        AppBuilder.Configure<Application>().UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        Application.Current.Styles.Add(new FluentAvaloniaTheme());
        Application.Current.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://TractorTool.UI/"))
            { Source = new Uri("avares://TractorTool.UI/Styles/ThemeResources.axaml") });
        Application.Current.Styles.Add(new StyleInclude(new Uri("avares://TractorTool.UI/"))
            { Source = new Uri("avares://TractorTool.UI/Styles/SharedStyles.axaml") });
        var logs = new BindingLog();
        Logger.Sink = logs;
        var failures = new List<string>();
        var card = new HudCard { Animate = false };
        var window = new Window { Width = 440, Height = 240, Content = card, Background = Brushes.Transparent };
        window.Show();
        var layouts = new List<string>();
        try
        {
            CheckInput();
            foreach (var metric in Enumerable.Range(0, 12))
            foreach (var mode in new[] { HudMode.Compact, HudMode.Strip, HudMode.Detail, HudMode.Strip, HudMode.Compact })
            {
                var profile = new HudProfile { Metric = metric, SensorId = "/gpu-nvidia/0/temperature/2" };
                var reading = metric switch
                {
                    1 => new HudReading("时钟", "00:41:55", "2026/10/08 星期四"),
                    2 => new HudReading("CPU", "8%", "处理器使用率 · 每秒采样", 8),
                    3 => new HudReading("内存", "41%", "13.2 / 32 GiB", 41, Context: "可用 18.8 GiB · 物理内存"),
                    4 => new HudReading("网速", "↓ 551.1 KiB/s", "↑ 48.2 KiB/s · 活动接口合计", SecondaryValue: "↑ 48.2 KiB/s",
                        Context: "2 个活动接口的合计流量\n包含虚拟网卡，转发流量可能重复计入。"),
                    9 => new HudReading("DeepSeek 余额", "14.20 CNY", "更新于 23:07:01"),
                    _ => new HudReading("指标", "43.5 °C", "硬件设备与采样说明")
                };
                if (metric != 1) reading = reading with { ObservedAt = new DateTimeOffset(2026, 10, 7, 23, 7, 1, TimeSpan.FromHours(8)) };
                var display = profile.Render(reading);
                card.DataContext = display;
                card.Mode = mode;
                Pump();
                var slots = new[] { "First", "Second", "Third", "Fourth" }
                    .Select(name => card.FindControl<HudSlotView>(name)!)
                    .Where(s => s.IsVisible && s.Item is { } item && (!item.IsText || item.Text.Length > 0)).ToArray();
                if (!Equals(card.FindControl<HudSlotView>("Second")!.Foreground, display.Accent))
                    failures.Add($"{metric}/{mode}: primary accent binding did not update");
                var bounds = slots.Select(s => new Rect(s.TranslatePoint(default, card)!.Value, s.Bounds.Size)).ToArray();
                if (mode == HudMode.Strip)
                {
                    if (Math.Abs(bounds[0].Center.X - card.Bounds.Height / 2) > 1 || card.Bounds.Width != HudSettings.SizeFor(HudMode.Strip).Width)
                        failures.Add($"{metric}: strip icon must nest concentrically inside the capsule end and width must remain fixed.");
                    if (slots[^1].Item is { IsRing: true } or { IsIcon: true }
                        && Math.Abs(card.Bounds.Width - bounds[^1].Center.X - card.Bounds.Height / 2) > 1)
                        failures.Add($"{metric}: trailing ring/icon must be concentric with the capsule end, not use text padding.");
                    foreach (var slot in slots.Where(s => s.Item!.IsText))
                        if (slot.GetVisualDescendants().OfType<TextBlock>().Any(t => t.TextLayout.TextLines.Any(line => line.HasCollapsed)))
                            failures.Add($"{metric}: normal strip content must remain readable, not be truncated to save space.");
                }
                layouts.Add($"{metric}/{mode} ({card.Bounds.Size}): " + string.Join("; ", slots.Zip(bounds, (s, b) => $"{s.Name}={b}")));
                for (var i = 0; i < bounds.Length; i++)
                {
                    if (bounds[i].Width <= 0 || bounds[i].Height <= 0 || !new Rect(card.Bounds.Size).Contains(bounds[i]))
                        failures.Add($"{metric}/{mode}: {slots[i].Name} is empty or outside the island: {bounds[i]}");
                    for (var j = i + 1; j < bounds.Length; j++)
                        if (bounds[i].Intersects(bounds[j])) failures.Add($"{metric}/{mode}: {slots[i].Name} overlaps {slots[j].Name}");
                }
                // Skia arc bounds have subpixel approximation error; allow .05 DIP, not a clipped pixel.
                foreach (var shape in card.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Shape>().Where(s => s.IsEffectivelyVisible))
                    if (shape.RenderedGeometry is { } geometry
                        && !new Rect(shape.Bounds.Size).Inflate(.05).Contains(geometry.GetRenderBounds(new Pen(shape.Stroke, shape.StrokeThickness))))
                        failures.Add($"{metric}/{mode}: {shape.GetType().Name} stroke extends outside its canvas: {geometry.GetRenderBounds(new Pen(shape.Stroke, shape.StrokeThickness))} vs {shape.Bounds.Size}.");
                if (mode == HudMode.Detail)
                {
                    foreach (var slot in slots.Where(s => s.Item!.IsText))
                        if (slot.GetVisualDescendants().OfType<TextBlock>().Any(t => t.TextLayout.TextLines.Any(line => line.HasCollapsed)))
                            failures.Add($"{metric}: normal detail content must be shown in full, including the metric name.");
                    Rect Region(string name)
                    {
                        var slot = card.FindControl<HudSlotView>(name)!;
                        return new Rect(slot.TranslatePoint(default, card)!.Value, slot.Bounds.Size);
                    }
                    var first = Region("First"); var second = Region("Second");
                    var third = Region("Third"); var fourth = Region("Fourth");
                    if (first.Right > third.Left || Math.Abs(first.Center.Y - third.Center.Y) > 1
                        || (card.FindControl<HudSlotView>("Fourth")!.IsVisible
                            ? Math.Abs(second.Right - fourth.Right) > 1 || fourth.Bottom > second.Top
                            : Math.Abs(first.Center.Y - second.Center.Y) > 1)
                        || third.Right > second.Left)
                        failures.Add($"{metric}: details must place the icon left of the name with aligned centers, separate from trailing readings.");
                    var body = card.FindControl<ScrollViewer>("BodyScroll")!;
                    var updated = card.FindControl<TextBlock>("Updated")!;
                    var bodyBounds = new Rect(body.TranslatePoint(default, card)!.Value, body.Bounds.Size);
                    var updatedBounds = new Rect(updated.TranslatePoint(default, card)!.Value, updated.Bounds.Size);
                    if (body.IsVisible && updated.IsVisible
                        && (Math.Abs(bodyBounds.Bottom - updatedBounds.Bottom) > 1 || bodyBounds.Right > updatedBounds.Left))
                        failures.Add("Explanation must stay in its left column; the reserved time column must align to its bottom without overlap.");
                    if (body.IsVisible && bounds.Any(b => b.Intersects(bodyBounds))
                        || updated.IsVisible && (body.IsVisible && bodyBounds.Intersects(updatedBounds)
                            || !new Rect(card.Bounds.Size).Contains(updatedBounds)))
                        failures.Add($"{metric}: expanded header, explanation and timestamp must have separate space.");
                    var timeCopies = (updated.IsVisible && updated.Text!.Contains("23:07:01") ? 1 : 0)
                        + (body.IsVisible && display.Layout.Detail.Text.Contains("23:07:01") ? 1 : 0);
                    if (reading.ObservedAt.HasValue ? timeCopies != 1 : updated.IsVisible)
                        failures.Add($"{metric}: observation time must appear once, including a source-provided update message.");
                    var lastBottom = updated.IsVisible ? updatedBounds.Bottom : bodyBounds.Bottom;
                    if (card.Bounds.Height - lastBottom > 14 || body.IsVisible && updated.IsVisible && updatedBounds.Top - bodyBounds.Bottom > 8)
                        failures.Add($"{metric}: unused footer/body space must not stretch the details: bottom={card.Bounds.Height - lastBottom}, gap={updatedBounds.Top - bodyBounds.Bottom}.");
                    if (display.Layout.Detail.Text.Contains('\n')) failures.Add($"{metric}: built-in explanation must wrap naturally, not force a line break.");
                }
                if (metric is 1 or 2 or 3 or 4 or 9)
                {
                    if (display.Layout.Detail.Text.Contains(reading.Value)
                        || reading.SecondaryValue is { } secondary && display.Layout.Detail.Text.Contains(secondary))
                        failures.Add($"{metric}: detail repeats the header reading");
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    using var frame = window.CaptureRenderedFrame();
                    frame?.Save(Path.Combine(output, $"metric-{metric}-{mode}.png"), PngBitmapEncoderOptions.Default);
                }
                // Exercise inherited contexts, reading replacement, empty states and reattachment.
                card.DataContext = null;
                Pump();
                card.DataContext = profile.Render(reading with { Value = "—", Percent = null });
                Pump();
                card.DataContext = profile.Render(reading with { Value = new string('8', 80), Detail = new string('长', 160) });
                Pump();
                if (slots.Any(s => !s.ClipToBounds)) failures.Add($"{metric}/{mode}: long content can escape its region");
                window.Content = null; window.Content = card;
                Pump();
            }
            // Live readings must never resize a strip, including digit/unit changes and unknown values.
            card.Mode = HudMode.Strip;
            foreach (var (download, upload) in new[] { ("↓ 73.7 KiB/s", "↑ 1.8 KiB/s"), ("↓ 999.9 MiB/s", "↑ 999.9 MiB/s"), ("↓ 1.2 MiB/s", "↑ 43.0 KiB/s"), ("↓ —", "↑ —") })
            {
                card.DataContext = new HudProfile { Metric = 4 }.Render(new("网速", download, "接口说明", SecondaryValue: upload)); Pump();
                if (card.Bounds.Size != HudSettings.SizeFor(HudMode.Strip))
                    failures.Add("Live strip readings must not change the width or height.");
                foreach (var name in new[] { "Second", "Third" })
                    if (card.FindControl<HudSlotView>(name)!.GetVisualDescendants().OfType<TextBlock>().Any(t => t.TextLayout.TextLines.Any(line => line.HasCollapsed)))
                        failures.Add($"Live strip reading {download}/{upload} must remain readable without truncation.");
                if (download == "↓ 73.7 KiB/s")
                {
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    using var frame = window.CaptureRenderedFrame();
                    frame?.Save(Path.Combine(output, "network-strip-live.png"), PngBitmapEncoderOptions.Default);
                }
            }
            // Optional regions must collapse during reading replacement, without requiring a mode change.
            card.Mode = HudMode.Detail;
            var clock = new HudProfile { Metric = 1 }.Render(new("时钟", "00:41:55", "2026/10/08 星期四"));
            card.DataContext = clock; Pump();
            var clockHeight = card.Bounds.Height;
            card.DataContext = clock with { Layout = clock.Layout with { Detail = clock.Layout.Detail with { Text = "" } } }; Pump();
            if (card.Bounds.Height >= clockHeight || card.FindControl<Border>("DetailDivider")!.IsVisible)
                failures.Add("Empty detail content must remove the divider and reduce the height.");
            card.DataContext = clock; Pump();
            if (Math.Abs(card.Bounds.Height - clockHeight) > 1 || clockHeight >= 130)
                failures.Add($"Clock details must restore to a compact content height without empty auxiliary/time rows ({clockHeight} -> {card.Bounds.Height}).");
            card.DataContext = clock with { Updated = "更新于 00:41:55" }; Pump();
            if (Math.Abs(card.Bounds.Height - clockHeight) > 1 || !card.FindControl<TextBlock>("Updated")!.IsVisible)
                failures.Add("A fitting observation time must appear beside the short explanation without adding an empty row.");
            card.DataContext = clock with { Layout = clock.Layout with { Detail = clock.Layout.Detail with { Text = new string('长', 400) } } }; Pump();
            var longBody = card.FindControl<ScrollViewer>("BodyScroll")!;
            if (card.Bounds.Height > HudSettings.SizeFor(HudMode.Detail).Height || longBody.Extent.Height <= longBody.Viewport.Height)
                failures.Add("Long explanations must scroll within the maximum detail height.");
            foreach (var metric in new[] { 0, 3 })
            {
                const string explanation = "自定义说明必须完整保留在可滚动的详情区域。\n用户模板的主动换行仍然保留。";
                card.DataContext = new HudProfile { Metric = metric, Detail = explanation }.Render(new("指标", "41%", "读数说明", 41));
                card.Mode = HudMode.Detail; Pump();
                if (card.FindControl<HudSlotView>("Body")!.Item?.Text != explanation)
                    failures.Add("A custom explanation must remain in the scrollable body.");
            }
            var memory = new MemorySettings();
            var model = new HudModel(new("HUD.UI.Checks", null!, memory, memory, default));
            try
            {
                var view = new HudView { DataContext = model };
                window.Content = view; window.Height = 800;
                foreach (var width in new[] { 1200, 800, 1200, 800 })
                {
                    window.Width = width; Pump();
                    var previews = view.GetVisualDescendants().OfType<HudCard>().ToArray();
                    if (previews.Length != 1) throw new InvalidOperationException("The editor must have exactly one preview.");
                    model.Metric = width == 1200 ? 4 : 3;
                    model.TitleTemplate = "编辑中的指标";
                    Pump();
                    if (!ReferenceEquals(previews[0].DataContext, model.EditorDisplay))
                        failures.Add("The left preview does not follow the editor draft.");
                }
            }
            finally
            {
                window.Content = null;
                var stopping = model.Stop();
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!stopping.IsCompleted && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
                if (!stopping.IsCompleted) throw new TimeoutException("Headless model disposal timed out.");
                stopping.GetAwaiter().GetResult();
            }
        }
        catch (Exception ex) { failures.Add(ex.ToString()); }
        window.Close();
        File.WriteAllLines(Path.Combine(output, "bindings.txt"), logs.Messages);
        File.WriteAllLines(Path.Combine(output, "layout.txt"), layouts);
        File.WriteAllLines(Path.Combine(output, "failures.txt"), failures);
        if (logs.Messages.Count != 0 || failures.Count != 0)
            throw new InvalidOperationException($"HUD UI checks: {logs.Messages.Count} binding warnings/errors, {failures.Count} layout failures. See {output}.");
        Console.WriteLine("PASS HUD headless: light/dark hover and press, immediate expansion/collapse including strip ends, central double clicks, keyboard and drag cancellation; 12 metrics/three states, context/long text/reattachment and single responsive editor preview; zero binding errors or layout failures.");

        void Pump() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }

        void CheckInput()
        {
            card.DataContext = new HudProfile { Metric = 4 }.Render(new("网速", "↓ 7.1 KiB/s", "活动接口合计",
                ObservedAt: new DateTimeOffset(2026, 10, 7, 23, 7, 1, TimeSpan.FromHours(8)),
                SecondaryValue: "↑ 1.0 KiB/s", Context: "40 个活动接口 · 含虚拟网卡\n转发流量可能重复计入。"));
            var header = card.FindControl<Button>("HeaderButton")!;
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            foreach (var mode in new[] { HudMode.Compact, HudMode.Strip, HudMode.Detail })
            {
                Application.Current!.RequestedThemeVariant = theme;
                card.Mode = mode; Pump();
                var point = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), window)!.Value;
                window.MouseMove(point); Pump();
                CheckSurface("hover");
                if (mode == HudMode.Strip) CheckInkCenter();
                Capture($"{theme}-{mode}-hover");
                var island = card.FindControl<Border>("Island")!;
                var normalBackground = island.Background;
                window.MouseDown(point, MouseButton.Left); Pump();
                CheckSurface("pressed");
                if (Equals(island.Background, normalBackground)) failures.Add("Press feedback must affect the island as a whole.");
                Capture($"{theme}-{mode}-pressed");
                window.MouseMove(new Point(1, 1), RawInputModifiers.LeftMouseButton);
                window.MouseUp(new Point(1, 1), MouseButton.Left); Pump();
            }
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            card.Mode = HudMode.Compact; Pump();
            // Raw mouse events exercise the Button, not just the pure state mapping.
            var center = header.TranslatePoint(new Point(header.Bounds.Width / 2, 20), window)!.Value;
            Click(center);
            if (card.Mode != HudMode.Strip) failures.Add("Single click must change state on release, without waiting for the double-click timeout.");
            Click(center);
            if (card.Mode != HudMode.Detail) failures.Add("Second click must continue into details.");
            foreach (var atRight in new[] { false, true })
            {
                card.Mode = HudMode.Strip; Pump();
                Click(new Point(1, 1)); // Reset the platform click sequence outside the island.
                var end = header.TranslatePoint(new Point(atRight ? header.Bounds.Width - 20 : 20, 20), window)!.Value;
                Click(end);
                if (card.Mode != HudMode.Compact) failures.Add("Clicking either strip end must collapse on release without a timer.");
            }
            card.Mode = HudMode.Strip; Pump();
            Click(new Point(1, 1));
            var stripCenter = header.TranslatePoint(new Point(header.Bounds.Width / 2, 20), window)!.Value;
            Click(stripCenter);
            if (card.Mode != HudMode.Compact) failures.Add("The central strip click must also collapse immediately.");
            Click(stripCenter);
            if (card.Mode != HudMode.Detail) failures.Add("A second click on the visible island must enter details after immediate collapse.");
            foreach (var target in new Control[] { header, card.FindControl<ScrollViewer>("BodyScroll")!, card.FindControl<TextBlock>("Updated")! })
            {
                card.Mode = HudMode.Compact; card.Mode = HudMode.Detail; Pump();
                Click(new Point(1, 1));
                var point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
                Click(point); Click(point);
                if (card.Mode != HudMode.Compact) failures.Add($"Double click in {target.Name} must return from details.");
            }
            // Keyboard and cancellation remain available after mouse input.
            header.Focus();
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Pump();
            if (card.Mode != HudMode.Compact) failures.Add("Escape must return to compact after mouse input.");
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None); Pump();
            if (card.Mode != HudMode.Strip) failures.Add("Keyboard activation must work immediately after Escape.");
            card.Mode = HudMode.Compact; Pump();
            var drags = 0;
            void Drag(PointerPressedEventArgs _) => drags++;
            card.DragRequested += Drag;
            window.MouseDown(center, MouseButton.Left);
            window.MouseMove(center + new Vector(10, 0), RawInputModifiers.LeftMouseButton);
            window.MouseUp(center + new Vector(10, 0), MouseButton.Left); Pump();
            card.DragRequested -= Drag;
            if (drags != 1 || card.Mode != HudMode.Compact) failures.Add("Dragging must request one move and must not activate a click.");
            Click(center);
            if (card.Mode != HudMode.Strip) failures.Add("The click after dragging must be a single click.");
            Click(new Point(1, 1));
            var stripPoint = header.TranslatePoint(new Point(120, 20), window)!.Value;
            Click(stripPoint);
            if (card.Mode != HudMode.Compact) failures.Add($"A single strip click must finish collapsing (actual: {card.Mode}).");
            card.Mode = HudMode.Strip; Pump();
            Click(new Point(1, 1)); Click(stripPoint);
            card.Mode = HudMode.Strip; Pump(); // An external mode change must clear the old double-click candidate.
            Click(stripPoint);
            if (card.Mode != HudMode.Compact) failures.Add("An explicit state change must reset the click sequence.");
            card.Animate = true; Pump();
            if (card.Transitions is { Count: > 0 })
            {
                card.Mode = HudMode.Strip; Wait(TimeSpan.FromMilliseconds(80)); Capture("motion-strip");
                card.Mode = HudMode.Detail; Wait(TimeSpan.FromMilliseconds(80)); Capture("motion-detail");
                card.Mode = HudMode.Compact; Wait(TimeSpan.FromMilliseconds(420)); Capture("motion-return");
                if (Math.Abs(card.Bounds.Width - HudSettings.CardWidth) > 1 || card.FindControl<Border>("Island")!.Opacity != 1)
                    failures.Add("An interrupted morph must return to a visible compact island.");
                Console.WriteLine("PASS HUD interrupted morph: strip -> detail -> compact, content remains visible at rest.");
            }
            else Console.WriteLine("SKIP HUD animated frames: the current animation preference disables transitions.");
            card.Animate = false;

            void Click(Point point) { window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Pump(); }
            void Wait(TimeSpan delay)
            {
                using var timeout = new CancellationTokenSource(delay);
                Dispatcher.UIThread.MainLoop(timeout.Token);
                Pump();
            }
            void Capture(string name)
            {
                using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Headless renderer produced no frame.");
                frame.Save(Path.Combine(output, name + ".png"), PngBitmapEncoderOptions.Default);
            }
            void CheckSurface(string state)
            {
                foreach (var child in header.GetVisualDescendants())
                {
                    var background = child switch { Border b => b.Background, ContentPresenter p => p.Background, _ => null };
                    if (background is ISolidColorBrush brush && brush.Color.A != 0)
                        failures.Add($"{card.Mode}/{state}: nested button surface is opaque ({child.GetType().Name}).");
                }
            }
            void CheckInkCenter()
            {
                foreach (var scale in new[] { 1d, 1.5, 2 })
                {
                    using var frame = new RenderTargetBitmap(new PixelSize((int)(card.Bounds.Width * scale), (int)(card.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
                    frame.Render(card);
                    frame.Save(Path.Combine(output, $"strip-{Application.Current!.RequestedThemeVariant}-{scale:0.0}x.png"), PngBitmapEncoderOptions.Default);
                    using var stream = new MemoryStream();
                    frame.Save(stream, PngBitmapEncoderOptions.Default); stream.Position = 0;
                    using var bitmap = SkiaSharp.SKBitmap.Decode(stream);
                    var center = card.Bounds.Height / 2 * scale;
                    foreach (var name in new[] { "Second", "Third", "Fourth" })
                    {
                        var slot = card.FindControl<HudSlotView>(name)!;
                        var bounds = new Rect(slot.TranslatePoint(default, card)!.Value, slot.Bounds.Size) * scale;
                        var top = bitmap.Height; var bottom = -1;
                        for (var y = (int)Math.Ceiling(bounds.Top); y < bounds.Bottom; y++)
                        for (var x = (int)Math.Ceiling(bounds.Left); x < bounds.Right; x++)
                        {
                            var pixel = bitmap.GetPixel(x, y);
                            if (Math.Max(pixel.Red, Math.Max(pixel.Green, pixel.Blue)) < 140) continue;
                            top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                        }
                        var offset = ((top + bottom + 1) / 2d - center) / scale;
                        layouts.Add($"Strip/{scale}x/{name}: visible ink offset from island centre = {offset:0.##} DIP");
                        if (bottom < top || Math.Abs(offset) > .75)
                            failures.Add($"Strip/{scale}x/{name}: text is not optically centered ({offset:0.##} DIP).");
                    }
                }
            }
        }
    }

    private sealed class MemorySettings : IPluginSettingsStore, IAppLog
    {
        public T Load<T>(string name) where T : IVersionedSettings, new()
            => typeof(T) == typeof(HudSettings) ? (T)(IVersionedSettings)new HudSettings { Animate = false } : new T();
        public void Save<T>(string name, T value) where T : IVersionedSettings, new() => throw new InvalidOperationException("UI preview must not save settings.");
        public void Write(string source, string message, Exception? error = null) => throw new InvalidOperationException(message, error);
    }

    private sealed class BindingLog : ILogSink
    {
        public List<string> Messages { get; } = [];
        public bool IsEnabled(LogEventLevel level, string area) => area == "Binding" && level >= LogEventLevel.Warning;
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
            => Log(level, area, source, messageTemplate, []);
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        {
            if (IsEnabled(level, area)) Messages.Add(messageTemplate + " | " + string.Join(" | ", propertyValues.Select(v => v?.ToString())));
        }
    }
}
