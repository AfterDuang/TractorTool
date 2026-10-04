using Avalonia.Controls;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using TractorTool.Core.Logging;
using TractorTool.Core.Plugins;
using TractorTool.Core.Storage;
using TractorTool.UI;
using TractorTool.UI.Controls;
using TractorTool.ViewModels;
using TractorTool.Views;

internal static class NavigationChecks
{
    public static void Run(Action<string, Action> check, string output)
    {
        check("Cancelled Frame navigation retains the current page", () =>
        {
            var presenter = new PageHost();
            var current = new Border();
            Require(presenter.Present(current));
            current.AddHandler(FAFrame.NavigatingFromEvent, (_, args) => args.Cancel = true);
            var frame = (FAFrame)presenter.Child!;
            Require(!presenter.Present(new Border()));
            Require(ReferenceEquals(frame, presenter.Child) && ReferenceEquals(frame.Content, current));
            presenter.Clear();
        });

        check("Failed Frame navigation after its entry changed replaces the container", () =>
        {
            var presenter = new PageHost();
            var current = new Border();
            Require(presenter.Present(current));
            // This event runs after FAFrame has already assigned its new CurrentEntry.
            current.AddHandler(FAFrame.NavigatedFromEvent, (_, _) => throw new InvalidOperationException("fixture navigation failed"));
            var retired = presenter.Child;
            ExpectFailure(() => presenter.Present(new Border()), "fixture navigation failed");
            Require(!ReferenceEquals(retired, presenter.Child));
            var next = new Border();
            Require(presenter.Present(next));
            Require(ReferenceEquals(((FAFrame)presenter.Child!).Content, next));
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
            Require(ReferenceEquals(((FAFrame)presenter.Child!).Content, current));
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
            pluginPage.AddHandler(FAFrame.NavigatingFromEvent, (_, e) => e.Cancel = !allow);
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
                window.Width = 600; window.UpdateLayout();
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
}
