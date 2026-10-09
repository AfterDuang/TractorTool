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
using TractorTool.UI;

// No host lifetime, disk settings, native HUD window, real sampling, credentials or network requests.
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
                    2 => new HudReading("CPU", "8%", "处理器使用率", 8),
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
                if (model.HasDraftChanges) failures.Add("Loading lowercase saved colors must not create unsaved edits.");
                var view = new HudView { DataContext = model };
                window.Content = view; window.Height = 800;
                var tray = new TrayCapture();
                model.RegisterTray(tray);
                Pump();
                var visibilityToggle = view.FindControl<ToggleSwitch>("VisibilityToggle")!;
                var pauseToggle = view.FindControl<ToggleSwitch>("PauseToggle")!;
                model.IsHudVisible = true; model.IsPaused = true;
                if (view.FindControl<Grid>("RuntimeControls")!.Children.Count != 2
                    || model.IsHudVisible || model.IsPaused
                    || visibilityToggle.IsChecked != model.IsHudVisible || pauseToggle.IsChecked != model.IsPaused
                    || visibilityToggle.IsEffectivelyEnabled || pauseToggle.IsEffectivelyEnabled
                    || tray.State is not { Text: "显示 HUD", IsEnabled: false, IsChecked: false })
                    failures.Add("Runtime controls must bind actual state and stay disabled before startup; tray must retain only visibility.");
                view.FindControl<Expander>("AppearanceExpander")!.IsExpanded = true;
                foreach (var width in new[] { 1600, 1200, 800, 600, 1200 })
                {
                    window.Width = width; Pump();
                    var previews = view.GetVisualDescendants().OfType<HudCard>().ToArray();
                    if (previews.Length != 1) throw new InvalidOperationException("The editor must have exactly one preview.");
                    model.Metric = width == 1200 ? 4 : 3;
                    model.TitleTemplate = "编辑中的指标";
                    Pump();
                    if (!ReferenceEquals(previews[0].DataContext, model.EditorDisplay))
                        failures.Add("The left preview does not follow the editor draft.");
                    var rotation = view.FindControl<StackPanel>("RotationPanel")!;
                    if (!rotation.GetVisualAncestors().Contains(view.FindControl<ScrollViewer>("PreviewScroll")))
                        failures.Add("Rotation controls must remain in the left preview pane at both widths.");
                    if (!ReferenceEquals(rotation.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("card")),
                        previews[0].GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("card"))))
                        failures.Add("Preview and shared rotation controls must use the same card.");
                    var appearance = view.FindControl<Grid>("AppearanceGrid")!;
                    var textSettings = view.FindControl<StackPanel>("TextSettings")!;
                    var colorSettings = view.FindControl<StackPanel>("ColorSettings")!;
                    if (appearance.Bounds.Width >= 640
                        ? colorSettings.Bounds.Left < textSettings.Bounds.Right || Math.Abs(colorSettings.Bounds.Top - textSettings.Bounds.Top) > 1
                        : colorSettings.Bounds.Top < textSettings.Bounds.Bottom)
                        failures.Add("Text and color groups must align side by side when wide and stack without overlap when narrow.");
                }
                model.RevertProfileCommand.Execute(null);
                model.Metric = 3; model.RuleEnabled = true; model.Comparison = 1; model.Threshold = 80;
                view.FindControl<Expander>("AppearanceExpander")!.IsExpanded = true;
                var normalPicker = view.FindControl<ColorPicker>("AccentPicker")!;
                var rulePicker = view.FindControl<ColorPicker>("RulePicker")!;
                normalPicker.SetCurrentValue(ColorView.ColorProperty, Color.Parse("#30D5C8"));
                rulePicker.SetCurrentValue(ColorView.ColorProperty, Color.Parse("#FF7891")); Pump();
                if (model.AccentColor != Color.Parse("#30D5C8") || model.RuleColor != Color.Parse("#FF7891")
                    || ((ISolidColorBrush)model.EditorDisplay.Accent).Color != model.AccentColor)
                    failures.Add("Visual color selection must update the draft and its preview.");
                model.Threshold = 64; Pump();
                if (((ISolidColorBrush)model.EditorDisplay.Accent).Color != model.RuleColor
                    || !model.ColorRuleHint.Contains("内存占用高于或等于 64%"))
                    failures.Add("The inclusive threshold and its plain-language description must agree.");
                model.Metric = 4; Pump();
                if (model.SupportsColorRule || ((ISolidColorBrush)model.EditorDisplay.Accent).Color != model.AccentColor)
                    failures.Add("Network speed must not show or apply a percentage color rule.");
                model.Metric = 3; model.SaveProfileCommand.Execute(null); Pump();
                if (memory.Saved.Active.Color != "#30D5C8" || memory.Saved.Active.RuleColor != "#FF7891" || model.HasDraftChanges)
                    failures.Add("Picked colors must persist as compatible RGB strings and reload without dirtying the editor.");
                CheckTypedNumbers();

                var savedProfile = memory.Saved.Active;
                model.TitleTemplate = "尚未保存的名称";
                var selected = model.SelectedProfile;
                var editor = model.EditorDisplay;
                view.FindControl<ToggleSwitch>("RotationToggle")!.SetCurrentValue(ToggleSwitch.IsCheckedProperty, true);
                model.RotationSeconds = 3; Pump();
                if (!memory.Saved.Rotate || memory.Saved.Seconds != 3 || memory.Saved.Active != savedProfile || !model.HasDraftChanges)
                    failures.Add("Rotation settings must save immediately without saving or discarding profile edits.");
                var writes = memory.Writes;
                model.UpdateRotation(true, HudMode.Strip, false);
                WaitForProfile("CPU");
                WaitForProfile("Net");
                if (memory.Writes != writes || model.SelectedProfile != selected || !ReferenceEquals(model.EditorDisplay, editor))
                    failures.Add("Rotation must wrap through saved profiles without writing configuration or moving the draft selection.");
                model.UpdateRotation(true, HudMode.Detail, false);
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3.2))) Dispatcher.UIThread.MainLoop(timeout.Token);
                Pump();
                if (model.ActiveProfileName != "Net") failures.Add("Opening details must pause automatic rotation.");
                model.UpdateRotation(true, HudMode.Strip, false);
                WaitForProfile("CPU");
                model.UpdateRotation(false, HudMode.Strip, false);
                if (model.RotationStatus.StartsWith("正在轮播")) failures.Add("Hiding the HUD must stop rotation.");

                memory.FailSave = true;
                model.RotateProfiles = false; Pump();
                if (!model.RotateProfiles || !memory.Saved.Rotate || model.SelectedProfile != selected || !model.HasDraftChanges
                    || !model.Status.Contains("已恢复原设置") || memory.Errors.Count != 1)
                    failures.Add("A failed rotation save must restore its toggle and preserve the running profile and draft.");
                memory.FailSave = false;
                model.RotateProfiles = false;
                model.RevertProfileCommand.Execute(null);
                model.RuleEnabled = true; model.Comparison = 1; model.Threshold = 80;
                window.Height = 960;
                foreach (var width in new[] { 1600, 1200, 800, 600 })
                {
                    window.Width = width; Pump();
                    view.FindControl<ScrollViewer>("ProfileScroll")!.Offset = new Vector(0, 450); Pump();
                    if (width < 956) { view.FindControl<Grid>("AppearanceGrid")!.BringIntoView(); Pump(); }
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    using var frame = window.CaptureRenderedFrame();
                    frame?.Save(Path.Combine(output, $"editor-color-rotation-{width}.png"), PngBitmapEncoderOptions.Default);
                    if (width == 1200)
                    {
                        var button = normalPicker.GetVisualDescendants().OfType<Button>().First();
                        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Pump();
                        if (button.Flyout is not Flyout { IsOpen: true, Content: Control popup })
                            failures.Add("Clicking the color swatch must open the visual picker.");
                        else
                        {
                            using var bitmap = new RenderTargetBitmap(PixelSize.FromSize(popup.Bounds.Size, 1), new Vector(96, 96));
                            bitmap.Render(popup);
                            bitmap.Save(Path.Combine(output, "color-picker-popup.png"), PngBitmapEncoderOptions.Default);
                            button.Flyout.Hide(); Pump();
                        }
                    }
                }
                CheckPositionSave();
                CheckProfileReadings();
                Task.Run(CheckInitialReadings).GetAwaiter().GetResult();
                Task.Run(HudSamplingChecks.Run).GetAwaiter().GetResult();
                CheckSamplingSettings();
                if (failures.Count == 0) Console.WriteLine("PASS HUD editor: visual colors, inclusive rule, independent rotation saves/rollback, timed Net -> CPU -> Net, detail pause/strip resume and draft isolation.");

                void CheckSamplingSettings()
                {
                    model.RevertProfileCommand.Execute(null);
                    var input = view.FindControl<NumericUpDown>("SamplingInterval")!;
                    var writes = memory.Writes;
                    foreach (var seconds in new[] { .2, 600d })
                    {
                        foreach (var width in new[] { 1200, 600 })
                        {
                            window.Width = width; Pump(); input.BringIntoView(); Pump();
                            var editor = input.GetVisualDescendants().OfType<TextBox>().Single();
                            editor.Focus(); editor.SelectAll();
                            window.KeyTextInput(seconds.ToString(System.Globalization.CultureInfo.CurrentCulture));
                            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                            window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None); Pump();
                            if (model.RefreshSeconds != seconds || memory.Writes != writes || !model.HasDraftChanges)
                                failures.Add("Typed sampling intervals must update only the current draft until saved.");
                            if (!model.SamplingHint.Contains(seconds == .2 ? "每秒约 5 次" : "每 10 分钟一次"))
                                failures.Add("Sampling guidance must translate seconds into an understandable frequency.");
                            using var frame = window.CaptureRenderedFrame();
                            frame?.Save(Path.Combine(output, $"sampling-{seconds}-{width}.png"), PngBitmapEncoderOptions.Default);
                        }
                    }
                    model.SaveProfileCommand.Execute(null); Pump();
                    if (memory.Saved.Active.SamplingSeconds != 600 || model.HasDraftChanges)
                        failures.Add("Saving a profile must persist its own sampling interval.");
                    model.CopyProfileCommand.Execute(null); Pump();
                    if (model.RefreshSeconds != 600 || memory.Saved.Active.SamplingSeconds != 600)
                        failures.Add("Copying must preserve the saved interval.");
                    model.RefreshSeconds = .2;
                    model.RevertProfileCommand.Execute(null);
                    if (model.RefreshSeconds != 600 || model.HasDraftChanges)
                        failures.Add("Reverting must restore the saved interval.");
                    model.SelectedProfile = model.Profiles.Single(p => p.Name == "CPU");
                    if (model.RefreshSeconds != 1) failures.Add("A different profile must retain its independent interval.");
                    model.Metric = 0; Pump();
                    if (input.IsEffectivelyVisible || !model.SamplingHint.Contains("系统事件"))
                        failures.Add("Battery must explain its event-driven updates instead of offering unused polling settings.");
                    Console.WriteLine("CHECK sampling editor: actual keyboard input at 0.2/600 seconds, 1200/600px, draft/save/copy/revert isolation and battery events.");
                }

                void CheckTypedNumbers()
                {
                    var interval = view.FindControl<NumericUpDown>("RotationInterval")!;
                    foreach (var text in new[] { "12", "300", "5" })
                    {
                        Type(interval, text);
                        if (model.RotationSeconds != int.Parse(text) || memory.Saved.Seconds != int.Parse(text))
                            failures.Add($"Typing {text} seconds must persist without pressing a spinner.");
                    }
                    foreach (var text in new[] { "2", "301", "12.5", "abc" })
                    {
                        Type(interval, text);
                        if (model.RotationSeconds != 5 || memory.Saved.Seconds != 5)
                            failures.Add("Invalid/fractional intervals must retain the last valid setting.");
                    }
                    interval.BringIntoView(); Pump();
                    var secondsEditor = interval.GetVisualDescendants().OfType<TextBox>().Single();
                    secondsEditor.Focus(); secondsEditor.SelectAll();
                    var beforeWrites = memory.Writes;
                    var beforeErrors = memory.Errors.Count;
                    window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
                    window.KeyReleaseQwerty(PhysicalKey.Backspace, RawInputModifiers.None); Pump();
                    if (secondsEditor.Text != "" || memory.Writes != beforeWrites || memory.Errors.Count != beforeErrors)
                        failures.Add("Temporarily clearing the interval must not save zero, reset the editor or log an error.");
                    window.KeyTextInput("1"); window.KeyTextInput("2");
                    view.FindControl<ToggleSwitch>("RotationToggle")!.Focus(); Pump();
                    if (model.RotationSeconds != 12 || memory.Saved.Seconds != 12)
                        failures.Add("Deleting then typing seconds one digit at a time must save on blur.");
                    var increase = interval.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_IncreaseButton");
                    var point = increase.TranslatePoint(new Point(increase.Bounds.Width / 2, increase.Bounds.Height / 2), window)!.Value;
                    window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Pump();
                    if (model.RotationSeconds != 13 || memory.Saved.Seconds != 13)
                        failures.Add("Spinner increment must continue to save after manual typing.");
                    Type(view.FindControl<NumericUpDown>("ColorThreshold")!, "72.5");
                    if (model.Threshold != 72.5) failures.Add("Typing a percentage must not treat it as a fraction or require spinner buttons.");
                    model.RevertProfileCommand.Execute(null);
                    Console.WriteLine("CHECK typed numbers: seconds Enter/blur, bounds, fractions and percentage units.");

                    void Type(NumericUpDown input, string text)
                    {
                        input.BringIntoView(); Pump();
                        var editor = input.GetVisualDescendants().OfType<TextBox>().Single();
                        editor.Focus(); editor.SelectAll();
                        window.KeyTextInput(text); Pump();
                        if (input == interval && int.TryParse(text, out var seconds) && seconds is >= 3 and <= 300
                            && memory.Saved.Seconds != seconds)
                            failures.Add("Valid typed seconds must save immediately, before Enter or focus loss.");
                        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                        window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                        view.FindControl<ToggleSwitch>("RotationToggle")!.Focus(); Pump();
                    }
                }

                void CheckProfileReadings()
                {
                    const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                    var activate = typeof(HudModel).GetMethod("ActivateProfile", flags)!;
                    var publish = typeof(HudModel).GetMethod("SetReading", flags)!;
                    var net = new HudProfile { Name = "Net", Metric = 4 };
                    var cpu = new HudProfile { Name = "CPU", Metric = 2 };
                    activate.Invoke(model, [net]);
                    publish.Invoke(model, [new HudReading("网速", "↓ 12 KiB/s", "有效网速", SecondaryValue: "↑ 1 KiB/s")]);
                    var previous = model.Display;
                    activate.Invoke(model, [cpu]);
                    publish.Invoke(model, [new HudReading("CPU", "—", "基线") { IsInitializing = true }]);
                    if (!ReferenceEquals(previous, model.Display)) failures.Add("Switching and baseline reads must preserve the entire previous display.");
                    publish.Invoke(model, [new HudReading("CPU", "8%", "有效读数", 8)]);
                    if (model.Display.Title != "CPU" || model.Display.Value != "8%" || model.Display.ProfileId != cpu.Id)
                        failures.Add("A ready reading must replace identity and content together.");
                    publish.Invoke(model, [new HudReading("CPU", "—", "读取失败")]);
                    if (model.Display.Value != "—" || model.Display.Detail != "读取失败") failures.Add("Real failures must remain visible instead of masquerading as pending sampling.");
                    Console.WriteLine("CHECK profile replacement: preserve complete old content while initializing, publish ready/error results.");
                }

                async Task CheckInitialReadings()
                {
                    var pending = new HudReading("CPU", "—", "基线") { IsInitializing = true };
                    var ready = new HudReading("CPU", "20%", "有效读数", 20);
                    var calls = 0;
                    var result = await HudSampler.ReadReadyAsync(() => ++calls == 1 ? pending : ready, default);
                    if (calls != 2 || result != ready) failures.Add("Initial rates must get one follow-up read before publication.");
                    calls = 0;
                    await HudSampler.ReadReadyAsync(() => { calls++; return ready; }, default);
                    if (calls != 1) failures.Add("Normal sampling must not perform extra reads.");
                    calls = 0;
                    result = await HudSampler.ReadReadyAsync(() => { calls++; return pending; }, default);
                    if (calls != 2 || !result.IsInitializing) failures.Add("A still-pending source must be bounded to two reads, without a busy loop.");
                    using var cancelled = new CancellationTokenSource();
                    calls = 0;
                    try
                    {
                        await HudSampler.ReadReadyAsync(() => { calls++; cancelled.Cancel(); return pending; }, cancelled.Token);
                        failures.Add("Cancelling during warm-up must cancel the follow-up.");
                    }
                    catch (OperationCanceledException) { }
                    if (calls != 1) failures.Add("Cancelled warm-up must not touch the source again.");
                    Console.WriteLine("CHECK initial sampling: in-memory source, one bounded warm-up, ordinary single read and cancellation.");
                }

                void CheckPositionSave()
                {
                    // Exercise the drag callback without starting a native window or real sampler.
                    const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                    var save = typeof(HudModel).GetMethod("SavePosition", flags)!;
                    var reset = typeof(HudModel).GetField("_resetCounters", flags)!;
                    var revision = typeof(HudModel).GetField("_revision", flags)!;
                    var pending = typeof(HudModel).GetField("_sampleCancellation", flags)!;
                    using var request = new CancellationTokenSource();
                    pending.SetValue(model, request); reset.SetValue(model, false);
                    var beforeRevision = revision.GetValue(model);
                    var beforeDisplay = model.Display;
                    var beforeProfile = memory.Saved;
                    var beforeSettings = memory.Hud;
                    model.Scale = 1.5; model.TitleTemplate = "未保存的拖动草稿";
                    var draftDisplay = model.EditorDisplay;
                    try
                    {
                        save.Invoke(model, [new HudSettings { ScreenName = "test-screen", Monitor = 2, PositionX = .3, PositionY = .4, Scale = 2 }]);
                        var placed = beforeSettings with { ScreenName = "test-screen", Monitor = 2, PositionX = .3, PositionY = .4 };
                        if (memory.Hud != placed || memory.Saved != beforeProfile || model.Scale != 1.5
                            || !model.HasDraftChanges || !ReferenceEquals(draftDisplay, model.EditorDisplay))
                            failures.Add("Dragging must save only position and preserve unsaved preferences/profile edits.");
                        var count = memory.Writes;
                        save.Invoke(model, [placed]);
                        if (memory.Writes != count) failures.Add("An unchanged drag position must not be saved again.");
                        memory.FailSave = true;
                        save.Invoke(model, [placed with { PositionX = .8 }]);
                        memory.FailSave = false;
                        var current = (HudSettings)typeof(HudModel).GetField("_settings", flags)!.GetValue(model)!;
                        if (memory.Hud != placed || current != placed || !model.Status.Contains("设置保存失败"))
                            failures.Add("Failed position saves must retain the last persisted position and report the failure.");
                        if (request.IsCancellationRequested || (bool)reset.GetValue(model)! || !Equals(beforeRevision, revision.GetValue(model))
                            || !ReferenceEquals(beforeDisplay, model.Display))
                            failures.Add("Dragging must not cancel in-flight sampling, invalidate counter baselines or clear the current reading.");
                        model.ApplyPreferencesCommand.Execute(null);
                        if (!request.IsCancellationRequested || !(bool)reset.GetValue(model)!)
                            failures.Add("Applying preferences must still invalidate sampling when settings change.");
                        Console.WriteLine("CHECK HUD position: saved/unchanged/failed placement, draft isolation and sampling continuity.");
                    }
                    finally { memory.FailSave = false; pending.SetValue(model, null); }
                }

                void WaitForProfile(string name)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4.5));
                    void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
                    { if (e.PropertyName == nameof(HudModel.ActiveProfileName) && model.ActiveProfileName == name) timeout.Cancel(); }
                    model.PropertyChanged += Changed;
                    try { if (model.ActiveProfileName != name) Dispatcher.UIThread.MainLoop(timeout.Token); }
                    finally { model.PropertyChanged -= Changed; }
                    Pump();
                    if (model.ActiveProfileName != name) failures.Add($"Rotation timer did not advance to {name}.");
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
                var content = card.FindControl<Grid>("ContentGrid")!;
                var cpu = new HudProfile { Metric = 2 };
                var net = new HudProfile { Metric = 4 };
                card.DataContext = cpu.Render(new("CPU", "8%", "有效 CPU 读数", 8));
                if (content.Opacity >= 1 || content.Opacity <= 0) failures.Add("Profile entry must start visible, with a short fade.");
                Wait(TimeSpan.FromMilliseconds(60)); Capture("profile-entry-midpoint");
                if (content.Opacity is <= .65 or >= 1) failures.Add("Profile entry must have a rendered intermediate animation frame.");
                Wait(TimeSpan.FromMilliseconds(220));
                card.DataContext = cpu.Render(new("CPU", "9%", "普通每秒更新", 9)); Pump();
                if (content.Opacity != 1 || content.RenderTransform?.Value != Matrix.Identity)
                    failures.Add("Ordinary readings in the same profile must not restart entry motion.");
                card.DataContext = net.Render(new("网速", "↓ 12 KiB/s", "切换网速"));
                card.DataContext = cpu.Render(new("CPU", "10%", "快速切换最终读数", 10));
                Wait(TimeSpan.FromMilliseconds(300)); Capture("profile-entry-final");
                if (content.Opacity != 1 || content.RenderTransform?.Value != Matrix.Identity || ((HudDisplay)card.DataContext).Value != "10%")
                    failures.Add("Rapid switches must settle on the latest complete reading.");
                card.DataContext = net.Render(new("网速", "↓ 14 KiB/s", "关闭动效"));
                card.Animate = false; Pump();
                if (content.Opacity != 1 || content.RenderTransform?.Value != Matrix.Identity)
                    failures.Add("Disabling motion must immediately restore full opacity and neutral position.");
                card.Animate = true;
                Motion.SetIsEnabled(card, false);
                card.DataContext = cpu.Render(new("CPU", "11%", "减少动态效果", 11)); Pump();
                if (content.Transitions is not null || content.Opacity != 1)
                    failures.Add("Reduced motion must suppress profile transitions.");
                Motion.SetIsEnabled(card, true);
                card.DataContext = net.Render(new("网速", "↓ 15 KiB/s", "动画中离开"));
                window.Content = null; Pump(); window.Content = card; Pump();
                if (content.Transitions is not null || content.Opacity != 1 || content.RenderTransform?.Value != Matrix.Identity)
                    failures.Add("Detaching and reattaching mid-transition must not leave faded or displaced content.");
                Console.WriteLine("CHECK profile motion: intermediate frame, no per-tick replay, rapid switch, reduced motion and detach cleanup.");
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

    private sealed class TrayCapture : IPluginTrayMenu, IPluginTrayItem
    {
        public PluginTrayItemState? State { get; private set; }
        public IPluginTrayItem Add(string id, PluginTrayItemState state, Func<CancellationToken, Task> execute)
        {
            if (id != "visible" || State is not null) throw new InvalidOperationException("Retired tray commands must not be registered.");
            State = state;
            return this;
        }
        public void Update(PluginTrayItemState state) => State = state;
    }

    private sealed class MemorySettings : IPluginSettingsStore, IAppLog
    {
        public HudProfiles Saved { get; private set; }
        public HudSettings Hud { get; private set; } = new() { Animate = false };
        public bool FailSave { get; set; }
        public int Writes { get; private set; }
        public List<string> Errors { get; } = [];
        public MemorySettings()
        {
            var first = new HudProfile { Name = "Net", Metric = 4, Color = "#ffe66b", RuleColor = "#ff8a90" };
            Saved = new() { Profiles = [first, new() { Name = "CPU", Metric = 2 }], ActiveId = first.Id };
        }
        public T Load<T>(string name) where T : IVersionedSettings, new()
            => typeof(T) == typeof(HudSettings) ? (T)(IVersionedSettings)Hud
                : typeof(T) == typeof(HudProfiles) ? (T)(IVersionedSettings)Saved : new T();
        public void Save<T>(string name, T value) where T : IVersionedSettings, new()
        {
            if (FailSave) throw new IOException("Simulated save failure.");
            if (value is HudProfiles profiles) Saved = profiles;
            else if (value is HudSettings hud) Hud = hud;
            else throw new InvalidOperationException("This check may only save in-memory HUD settings/profiles.");
            Writes++;
        }
        public void Write(string source, string message, Exception? error = null) => Errors.Add(message);
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
