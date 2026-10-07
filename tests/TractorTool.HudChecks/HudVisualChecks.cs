using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Logging;
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
        var logs = new BindingLog();
        Logger.Sink = logs;
        var failures = new List<string>();
        var card = new HudCard { Animate = false };
        var window = new Window { Width = 440, Height = 240, Content = card, Background = Brushes.Transparent };
        window.Show();
        var layouts = new List<string>();
        try
        {
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
        Console.WriteLine("PASS HUD headless: 12 metrics, three states and returns, reading/context changes, long text, reattachment, single editor preview and wide/narrow resize; zero binding errors and overlapping regions.");

        void Pump() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
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
