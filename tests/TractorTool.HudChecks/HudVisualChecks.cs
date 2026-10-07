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
                    3 => new HudReading("内存", "41%", "13.2 / 32 GiB", 41, Context: "可用 18.8 GiB · 物理内存"),
                    4 => new HudReading("网速", "↓ 551.1 KiB/s", "↑ 48.2 KiB/s · 活动接口合计", SecondaryValue: "↑ 48.2 KiB/s",
                        Context: "2 个活动接口的合计流量\n包含虚拟网卡，转发流量可能重复计入。"),
                    _ => new HudReading("指标", "43.5 °C", "硬件设备与采样说明")
                };
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
                layouts.Add($"{metric}/{mode}: " + string.Join("; ", slots.Zip(bounds, (s, b) => $"{s.Name}={b}")));
                for (var i = 0; i < bounds.Length; i++)
                {
                    if (bounds[i].Width <= 0 || bounds[i].Height <= 0 || !new Rect(card.Bounds.Size).Contains(bounds[i]))
                        failures.Add($"{metric}/{mode}: {slots[i].Name} is empty or outside the island: {bounds[i]}");
                    for (var j = i + 1; j < bounds.Length; j++)
                        if (bounds[i].Intersects(bounds[j])) failures.Add($"{metric}/{mode}: {slots[i].Name} overlaps {slots[j].Name}");
                }
                if (metric is 3 or 4)
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
        Console.WriteLine("PASS HUD headless: light/dark hover and press, immediate expansion, strip-end double clicks, keyboard, drag cancellation and collapse timer; 12 metrics/three states, context/long text/reattachment and single responsive editor preview; zero binding errors or layout failures.");

        void Pump() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }

        void CheckInput()
        {
            card.DataContext = new HudProfile { Metric = 4 }.Render(new("网速", "↓ 7.1 KiB/s", "活动接口合计",
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
            foreach (var edge in new[] { 20d, HudSettings.SizeFor(HudMode.Strip).Width - 20 })
            {
                card.Mode = HudMode.Strip; Pump();
                Click(new Point(1, 1)); // Reset the platform click sequence outside the island.
                var end = header.TranslatePoint(new Point(edge, 20), window)!.Value;
                Click(end);
                if (card.Mode != HudMode.Strip) failures.Add("The strip must keep the second-click target at its ends.");
                Click(end);
                if (card.Mode != HudMode.Detail) failures.Add("Double click at a strip end must enter details.");
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
            WaitForCollapse();
            if (card.Mode != HudMode.Compact) failures.Add($"A single strip click must finish collapsing (actual: {card.Mode}).");
            card.Mode = HudMode.Strip; Pump();
            Click(new Point(1, 1)); Click(stripPoint);
            card.Mode = HudMode.Compact; Pump(); // Hide/click-through can explicitly collapse the desktop card.
            WaitForCollapse();
            if (card.Mode != HudMode.Compact) failures.Add("An explicit state change must cancel the pending collapse, not reopen the island.");
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
            void WaitForCollapse()
            {
                var delay = window.GetPlatformSettings()!.GetDoubleTapTime(PointerType.Mouse) + TimeSpan.FromMilliseconds(80);
                Wait(delay);
            }
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
