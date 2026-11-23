using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FluentAvalonia.Core;
using SkiaSharp;
using TractorTool.Core.Logging;
using TractorTool.Core.Plugins;
using TractorTool.Core.Storage;
using TractorTool.UI;
using TractorTool.UI.Controls;
using TractorTool.ViewModels;
using TractorTool.Views;

internal static class NavigationChecks
{
    public static void Run(Action<string, Action> check, string output, MainWindow? shell = null)
    {
        check("Shell header bindings survive presenter clearing and failure recovery without errors", () =>
        {
            var originalMotion = FAUISettings.AreAnimationsEnabled();
            var originalSink = Logger.Sink;
            var logs = new BindingLog();
            MainWindow? window = null;
            Logger.Sink = logs;
            try
            {
                Motion.Configure(Application.Current!, false);
                // Headless windows share a synthetic HWND; use the suite's existing Fluent shell.
                window = shell ?? new MainWindow();
                window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                var host = window.FindControl<PageHost>("PagePresenter")!;
                var header = ((Grid)host.Parent!).Children.OfType<Grid>().Single();
                Require(host.Present(new Border()));
                var retired = host.Child!;
                retired.Opacity = 0.4;
                retired.RenderTransform = new TranslateTransform(0, 9);
                Require(header.Opacity == 0.4 && ReferenceEquals(header.RenderTransform, retired.RenderTransform));

                // Clear follows the same presenter replacement path as shutdown.
                host.Clear();
                Require(header.Opacity == 1 && header.RenderTransform is null);
                retired.Opacity = 0.2;
                retired.RenderTransform = new TranslateTransform(0, 12);
                Require(header.Opacity == 1 && header.RenderTransform is null);

                var page = new Border();
                Require(host.Present(page));
                host.Child!.Opacity = 0.6;
                host.Child.RenderTransform = new TranslateTransform(0, 6);
                Require(header.Opacity == 0.6 && ReferenceEquals(header.RenderTransform, host.Child.RenderTransform));
                page.AddHandler(PageHost.NavigatingFromEvent, (_, _) => throw new InvalidOperationException("fixture header recovery"));
                ExpectFailure(() => host.Present(new Border()), "fixture header recovery");
                Require(header.Opacity == 1 && header.RenderTransform is null);
                Require(host.Present(new Border()));
                host.Clear(); window.Close(); Dispatcher.UIThread.RunJobs();
                if (logs.Messages.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, logs.Messages));
            }
            finally
            {
                try { window?.Close(); }
                finally { Logger.Sink = originalSink; Motion.Configure(Application.Current!, originalMotion); }
            }
        });

        check("Detached nested page hosts and page models are collectible with their window still open", () =>
        {
            var originalMotion = FAUISettings.AreAnimationsEnabled();
            var presenter = new PageHost();
            var window = new Window { Content = presenter, Width = 1000, Height = 600 };
            window.Show();
            try
            {
                foreach (var animations in new[] { false, true })
                {
                    Motion.Configure(Application.Current!, animations);
                    var retired = NavigateRepeatedly(presenter, window);
                    for (var attempt = 0; attempt < 3; attempt++)
                    {
                        Dispatcher.UIThread.RunJobs();
                        window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    }
                    if (retired.Any(reference => reference.IsAlive))
                        throw new InvalidOperationException($"Animations={animations}: {retired.Count(reference => reference.IsAlive)} / {retired.Count} retired objects retained.");
                    GC.KeepAlive(window); GC.KeepAlive(presenter);
                }
            }
            finally { Motion.Configure(Application.Current!, originalMotion); presenter.Clear(); window.Close(); }
        });

        check("Rapid navigation, clearing and disabled motion leave the current page visible", () =>
        {
            var originalMotion = FAUISettings.AreAnimationsEnabled();
            var presenter = new PageHost();
            var window = new Window { Content = presenter, Width = 400, Height = 300 };
            window.Show();
            try
            {
                Motion.Configure(Application.Current!, true);
                presenter.Present(new Border()); presenter.Clear();
                presenter.Present(new Border()); presenter.Present(new Border());
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                var deadline = DateTime.UtcNow.AddSeconds(2);
                while (presenter.Child!.Opacity < 1 && DateTime.UtcNow < deadline)
                {
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Dispatcher.UIThread.RunJobs(); Thread.Sleep(10);
                }
                Require(presenter.Child!.Opacity == 1 && presenter.Child.RenderTransform!.Value.IsIdentity);
                presenter.Present(new Border());
                Motion.Configure(Application.Current!, false); // Also disable a queued entrance.
                Dispatcher.UIThread.RunJobs();
                Require(presenter.Child.Opacity == 1 && presenter.Child.Transitions is null);
                presenter.Present(new Border());
                Require(presenter.Child.Opacity == 1 && presenter.Child.RenderTransform!.Value.IsIdentity);
            }
            finally { presenter.Clear(); window.Close(); Motion.Configure(Application.Current!, originalMotion); }
        });

        check("Page text, borders and fills remain stable when the entrance fade completes", () =>
        {
            var originalMotion = FAUISettings.AreAnimationsEnabled();
            Motion.Configure(Application.Current!, false);
            var presenter = new PageHost();
            var window = new Window { Content = presenter, Background = Brushes.Black, Width = 260, Height = 110 };
            try
            {
                presenter.Present(new Border
                {
                    Margin = new Thickness(20), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                    Background = new SolidColorBrush(Color.FromRgb(50, 50, 54)), BorderBrush = Brushes.White,
                    Child = new TextBlock { Text = "文字对照 TractorTool", FontSize = 14, Foreground = Brushes.White, Margin = new Thickness(12) }
                });
                window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                // Hold position fixed to isolate the final opacity step from normal movement.
                SKColor[] Capture(double opacity)
                {
                    presenter.Child!.Opacity = opacity;
                    Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
                    using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Missing rendered frame.");
                    using var encoded = new MemoryStream();
                    frame.Save(encoded, new PngBitmapEncoderOptions());
                    using var pixels = SKBitmap.Decode(encoded.ToArray());
                    return pixels.Pixels;
                }
                var before = Capture(0.999999);
                var after = Capture(1);
                // Removing the opacity layer must not switch glyph antialiasing at the end.
                for (var i = 0; i < before.Length; i++)
                    if (Math.Abs(before[i].Red - after[i].Red) > 1 || Math.Abs(before[i].Green - after[i].Green) > 1 || Math.Abs(before[i].Blue - after[i].Blue) > 1)
                        throw new InvalidOperationException($"Final opacity changed glyph/edge pixel {i}: {before[i]} -> {after[i]}.");
                // Flat fill and border samples exclude normal corner coverage differences.
                foreach (var index in new[] { 55 * 260 + 130, 20 * 260 + 130, 89 * 260 + 130, 55 * 260 + 20, 55 * 260 + 239 })
                    if (before[index] != after[index])
                        throw new InvalidOperationException($"Final opacity changed pixel {index}: {before[index]} -> {after[index]}.");
                Require(after[55 * 260 + 130].Red == 50 && after[20 * 260 + 130].Red == 255);
            }
            finally { presenter.Clear(); window.Close(); Motion.Configure(Application.Current!, originalMotion); }
        });

        check("UI 1.5 accepts existing page contracts and rejects future versions", () =>
        {
            var compatibility = new HostCompatibility("windows", "win-x64", UiApi.Version, UiApi.AvaloniaVersion);
            var manifest = new PluginManifest { ApiVersion = "1.7", AvaloniaVersion = UiApi.AvaloniaVersion, SupportedPlatforms = ["windows"] };
            foreach (var version in new[] { "1.0", "1.1", "1.2", "1.3", "1.4", "1.5" })
                Require(compatibility.Check(manifest with { UiApiVersion = version }) is null);
            Require(compatibility.Check(manifest with { UiApiVersion = "1.6" }) is not null);
            Require(new HostCompatibility("windows", "win-x64", "1.4", UiApi.AvaloniaVersion)
                .Check(manifest with { UiApiVersion = "1.5" }) is not null);
            Require(new HostCompatibility("windows", "win-x64", "1.3", UiApi.AvaloniaVersion)
                .Check(manifest with { UiApiVersion = "1.4" }) is not null);
        });

        check("Cancelled navigation retains the current page", () =>
        {
            var presenter = new PageHost();
            var current = new Border();
            Require(presenter.Present(current));
            current.AddHandler(PageHost.NavigatingFromEvent, (_, args) => args.Cancel = true);
            var container = (Border)presenter.Child!;
            Require(!presenter.Present(new Border()));
            Require(ReferenceEquals(container, presenter.Child) && ReferenceEquals(container.Child, current));
            presenter.Clear();
        });

        check("Navigation callback failure leaves a usable fresh container", () =>
        {
            var presenter = new PageHost();
            var current = new Border();
            Require(presenter.Present(current));
            current.AddHandler(PageHost.NavigatingFromEvent, (_, _) => throw new InvalidOperationException("fixture navigation failed"));
            var retired = presenter.Child;
            ExpectFailure(() => presenter.Present(new Border()), "fixture navigation failed");
            Require(!ReferenceEquals(retired, presenter.Child));
            var next = new Border();
            Require(presenter.Present(next));
            Require(ReferenceEquals(((Border)presenter.Child!).Child, next));
            presenter.Clear();
        });

        check("Page attachment failure leaves a usable fresh navigation container", () =>
        {
            var presenter = new PageHost();
            var window = new Window { Content = presenter };
            window.Show();
            try
            {
                Require(presenter.Present(new Border()));
                var faulty = new Border();
                faulty.AttachedToLogicalTree += (_, _) => throw new InvalidOperationException("fixture attachment failed");
                ExpectFailure(() => presenter.Present(faulty), "fixture attachment failed");
                var detaching = new Border();
                Require(presenter.Present(detaching));
                detaching.DetachedFromLogicalTree += (_, _) => throw new InvalidOperationException("fixture detachment failed");
                ExpectFailure(() => presenter.Present(new Border()), "fixture detachment failed");
                Require(presenter.Present(new Border()));
            }
            finally { presenter.Clear(); window.Close(); }
        });

        check("Already owned page is rejected without changing the displayed page", () =>
        {
            var presenter = new PageHost();
            var current = new Border();
            Require(presenter.Present(current));
            var foreign = new Border();
            var owner = new Border { Child = foreign };
            ExpectFailure(() => presenter.Present(foreign), "独立页面");
            Require(ReferenceEquals(owner.Child, foreign));
            Require(ReferenceEquals(((Border)presenter.Child!).Child, current));
            presenter.Clear();
        });

        check("Nested settings preserve cancellation and factory failure does not dispose a visible page", () =>
        {
            var root = Path.Combine(output, "settings-navigation-" + Guid.NewGuid().ToString("N"));
            var paths = new AppPaths(Path.Combine(root, "app"), Path.Combine(root, "profile")); paths.EnsureDataDirectories();
            var vm = new SettingsViewModel(paths, new(Path.Combine(paths.ConfigRoot, "settings.json")), new(), new AppLog(paths.LogRoot));
            var lifetime = new PageLifetimeProbe();
            var pluginPage = new Border { DataContext = lifetime };
            var allow = false;
            pluginPage.AddHandler(PageHost.NavigatingFromEvent, (_, e) => e.Cancel = !allow);
            var category = new SettingsPage("Plugin", () => pluginPage); vm.AddPluginPage(category); vm.SelectedPage = category;
            var settings = new SettingsView { DataContext = vm };
            var outer = new PageHost(); var window = new Window { Content = outer, Width = 1000, Height = 600 };
            window.Show();
            try
            {
                Require(outer.Present(settings)); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Require(!outer.Present(new Border()) && lifetime.LeaveCount == 0);
                vm.SelectedPage = new("Broken", () => throw new InvalidOperationException("fixture factory failure"));
                Require(vm.SelectedPage == category && lifetime.LeaveCount == 0 && pluginPage.Parent is not null);
                allow = true;
                Require(outer.Present(new Border())); window.UpdateLayout();
                // Exercise the same explicit lifetime cleanup as MainViewModel (Unloaded is idempotent).
                settings.OnNavigatedFrom();
                Require(lifetime.LeaveCount == 1);
            }
            finally { settings.OnNavigatedFrom(); vm.ExternalTools.Dispose(); window.Close(); }
        });

        check("Settings descendants retain category selection and breadcrumbs return to the parent", () =>
        {
            var root = Path.Combine(output, "settings-hierarchy-" + Guid.NewGuid().ToString("N"));
            var paths = new AppPaths(Path.Combine(root, "app"), Path.Combine(root, "profile")); paths.EnsureDataDirectories();
            var vm = new SettingsViewModel(paths, new(Path.Combine(paths.ConfigRoot, "settings.json")), new(), new AppLog(paths.LogRoot));
            var view = new SettingsView { DataContext = vm };
            var window = new Window { Content = view, Width = 1000, Height = 600 };
            window.Show();
            try
            {
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Require(vm.SoftwareSettings.Count == 2 && vm.GeneralPages.Count == 3);
                foreach (var child in vm.GeneralPages)
                {
                    vm.NavigateCommand.Execute(child); window.UpdateLayout();
                    Require(vm.SelectedPage == child && vm.SelectedCategory == vm.SoftwareSettings[0]);
                    Require(vm.Breadcrumbs.Count == 2 && vm.Breadcrumbs[1].IsCurrent && !vm.Breadcrumbs[0].IsCurrent);
                    vm.NavigateCommand.Execute(vm.Breadcrumbs[0].Page);
                    Require(vm.SelectedPage == vm.SoftwareSettings[0] && vm.Breadcrumbs.Count == 1);
                }
                vm.NavigateCommand.Execute(vm.AppearancePage);
                window.Width = 600; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                var compact = view.FindControl<ComboBox>("CompactNavigation")!;
                Require(compact.IsVisible && ReferenceEquals(compact.SelectedItem, vm.SoftwareSettings[0]));
                compact.SelectedItem = vm.SoftwareSettings[1];
                Require(vm.SelectedPage == vm.SoftwareSettings[1] && vm.Breadcrumbs.Count == 1);
            }
            finally { view.OnNavigatedFrom(); vm.ExternalTools.Dispose(); window.Close(); }
        });

        check("View model commits and cleans up only after presentation succeeds", () =>
        {
            var root = Path.Combine(output, "navigation-" + Guid.NewGuid().ToString("N"));
            var paths = new AppPaths(Path.Combine(root, "app"), Path.Combine(root, "profile"));
            paths.EnsureDataDirectories();
            var log = new AppLog(paths.LogRoot);
            var host = new PluginHost(paths, new("windows", "win-x64", UiApi.Version, UiApi.AvaloniaVersion), log);
            var settings = new SettingsViewModel(paths, new(Path.Combine(paths.ConfigRoot, "settings.json")), new(), log);
            var vm = new MainViewModel(paths, host, log, settings, () => Task.CompletedTask);
            try
            {
                var current = vm.CurrentPage!;
                var lifetime = new PageLifetimeProbe();
                current.DataContext = lifetime;
                var allow = true;
                var calls = 0;
                vm.AttachPagePresenter(_ => { calls++; return allow; }, () => { });
                var selected = vm.SelectedNavigation;
                allow = false;
                vm.ShowSettingsCommand.Execute(null);
                Require(ReferenceEquals(vm.CurrentPage, current) && vm.PageTitle == "主页");
                Require(ReferenceEquals(vm.SelectedNavigation, selected) && lifetime.LeaveCount == 0);
                allow = true;
                vm.ShowSettingsCommand.Execute(null);
                Require(vm.PageTitle == "设置" && lifetime.LeaveCount == 1);
                vm.Detach();
                var before = calls;
                vm.ShowPluginsCommand.Execute(null);
                Require(vm.CurrentPage is null && calls == before);
            }
            finally { vm.Dispose(); host.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Navigation regression assertion failed.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<WeakReference> NavigateRepeatedly(PageHost presenter, Window window)
    {
        var retired = new List<WeakReference>();
        for (var i = 0; i < 40; i++)
        {
            var model = new object();
            var leaf = new Border { DataContext = model };
            var nested = new PageHost();
            var page = new Border { Child = nested };
            nested.Present(leaf);
            presenter.Present(page);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            retired.AddRange([new(page), new(nested), new(leaf), new(model)]);
            // Settings explicitly clears its nested host; logs/video leave with the outer page.
            if (i % 2 == 0) nested.Clear();
            presenter.Present(new Border());
        }
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        return retired;
    }

    private static void ExpectFailure(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error.ToString().Contains(message, StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("Expected failure was not observed: " + message);
    }

    private sealed class PageLifetimeProbe : IPluginPageLifetime
    {
        public int LeaveCount { get; private set; }
        public void OnNavigatedFrom() => LeaveCount++;
    }

    private sealed class BindingLog : ILogSink
    {
        public List<string> Messages { get; } = [];
        public bool IsEnabled(LogEventLevel level, string area) => area == LogArea.Binding && level >= LogEventLevel.Warning;
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
            => Log(level, area, source, messageTemplate, []);
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        {
            if (IsEnabled(level, area)) Messages.Add(messageTemplate + " | " + string.Join(" | ", propertyValues));
        }
    }
}
