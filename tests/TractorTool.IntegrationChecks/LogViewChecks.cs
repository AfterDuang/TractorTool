using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TractorTool.Core.Logging;
using TractorTool.Core.Storage;
using TractorTool.ViewModels;
using TractorTool.Views;

internal static class LogViewChecks
{
    public static void Run(Action<string, Action> check, string output)
    {
        check("Log view follows appended rows only at the bottom and keeps selection while reading above", () =>
        {
            var paths = new AppPaths(Path.Combine(output, "log-view-app"), Path.Combine(output, "log-view-" + Guid.NewGuid().ToString("N")));
            paths.EnsureDataDirectories();
            var log = new AppLog(paths.LogRoot);
            using var tasks = new TasksViewModel(null, _ => { }, id => id, _ => false);
            var vm = new LogsViewModel(paths, log, tasks);
            for (var i = 0; i < 80; i++) log.Write("Fixture", "row " + i);
            vm.Refresh();
            var view = new LogEntriesView { DataContext = vm };
            var window = new Window { Content = view, Width = 1000, Height = 640 };
            void Layout()
            {
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            }
            try
            {
                window.Show(); Layout();
                var list = view.FindControl<ListBox>("LogList")!;
                var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
                bool AtEnd() => scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 2;
                Require(scroll.Extent.Height > scroll.Viewport.Height && AtEnd());
                log.Write("Fixture", "newest at bottom"); vm.Refresh(); Layout();
                Require(AtEnd() && vm.Entries.Last().Summary == "newest at bottom");
                scroll.Offset = new Vector(0, scroll.Offset.Y / 2); Layout();
                var offset = scroll.Offset.Y;
                vm.SelectedEntry = vm.Entries[40];
                log.Write("Fixture", "keep reading"); vm.Refresh(); Layout();
                Require(!AtEnd() && Math.Abs(scroll.Offset.Y - offset) < 2 && vm.SelectedEntry == vm.Entries[40]);
                scroll.ScrollToEnd(); Layout();
                log.Write("Fixture", "follow resumed"); vm.Refresh(); Layout();
                Require(AtEnd());
                vm.IsPaused = true;
                log.Write("Fixture", "collected while paused"); vm.Refresh(); Layout();
                Require(vm.Entries.Last().Summary == "follow resumed");
                vm.IsPaused = false; Layout();
                Require(AtEnd() && vm.Entries.Last().Summary == "collected while paused");
                Require(view.GetVisualDescendants().OfType<Expander>().Single().IsExpanded == false);
            }
            finally { window.Close(); }
        });
    }

    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Log view assertion failed."); }
}
