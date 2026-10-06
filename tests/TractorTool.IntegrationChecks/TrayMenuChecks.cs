using Avalonia.Controls;
using Avalonia.Threading;
using TractorTool.Contracts;
using TractorTool.Core.Logging;
using TractorTool.Core.Plugins;
using TractorTool.Services;
using TractorTool.UI;

internal static class TrayMenuChecks
{
    public static void Run(Action<string, Action> check, Action<Task> pump, string output)
    {
        var log = new AppLog(Path.Combine(output, "tray-checks-" + Guid.NewGuid().ToString("N")));
        PluginRecord Record(string id, string version = "1.5") => new("unused", new PluginManifest
        {
            Id = id, Name = id, UiApiVersion = version
        }, PluginState.Loaded, "fixture");
        void Assert(bool value) { if (!value) throw new Exception("Tray assertion failed"); }
        void Click(NativeMenuItem item) => ((INativeMenuItemExporterEventsImplBridge)item).RaiseClicked();
        NativeMenuItem Child(TrayService tray, string id, int index = 0) => (NativeMenuItem)tray.Menu.Items
            .OfType<NativeMenuItem>().Single(i => i.Header == id).Menu!.Items[index];
        check("Tray groups are isolated, failed registration rolls back, and legacy UI cannot use the new API", () =>
        {
            using var tray = new TrayService(() => { }, () => Task.CompletedTask, log, disabled: true);
            var plugin = new Fixture(menu => menu.Add("same", new("Action"), _ => Task.CompletedTask));
            var first = tray.Register(Record("First"), plugin)!;
            var second = tray.Register(Record("Second"), plugin)!;
            Assert(tray.Menu.Items.OfType<NativeMenuItem>().Select(i => i.Header)
                .SequenceEqual(new[] { "显示 TractorTool", "First", "Second", "退出" }));
            var rejected = false;
            try { tray.Register(Record("Old", "1.4"), plugin); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected);
            rejected = false;
            try { tray.Register(Record("Broken"), new Fixture(menu =>
            {
                menu.Add("duplicate", new("One"), _ => Task.CompletedTask);
                menu.Add("duplicate", new("Two"), _ => Task.CompletedTask);
            })); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected && tray.Menu.Items.Count == 6);
            pump(first.DisposeAsync().AsTask()); Assert(Child(tray, "Second").Header == "Action");
            pump(second.DisposeAsync().AsTask()); Assert(tray.Menu.Items.Count == 3);
        });
        check("Tray state updates are dispatched, disabled and busy items cannot execute, shutdown awaits the action", () =>
        {
            using var tray = new TrayService(() => { }, () => Task.CompletedTask, log, disabled: true);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            IPluginTrayItem handle = null!; var calls = 0; CancellationToken stopping = default;
            var resource = tray.Register(Record("State"), new Fixture(menu => handle = menu.Add("action", new("Action"), async token =>
            {
                calls++; stopping = token; await release.Task; token.ThrowIfCancellationRequested();
            })))!;
            var item = Child(tray, "State");
            pump(Task.Run(() => handle.Update(new("Disabled", false, true)))); Dispatcher.UIThread.RunJobs();
            Assert(item.Header == "Disabled" && item.IsChecked && !item.IsEnabled);
            Click(item); Assert(calls == 0);
            handle.Update(new("Run", true, false)); Click(item); Click(item);
            Assert(calls == 1 && !item.IsEnabled && !item.IsChecked);
            var shutdown = resource.DisposeAsync().AsTask();
            try
            {
                Dispatcher.UIThread.RunJobs();
                Assert(stopping.IsCancellationRequested && !shutdown.IsCompleted && tray.Menu.Items.Count == 3);
                handle.Update(new("Ignored")); Click(item); Assert(calls == 1);
            }
            finally { release.TrySetResult(); pump(shutdown); }
            Assert(!log.Snapshot().Entries.Any(e => e.Source == "State" && e.Level == AppLogLevel.Error));
        });
        check("Tray callback failures and unexpected cancellation remain errors and leave the menu usable", () =>
        {
            using var tray = new TrayService(() => { }, () => Task.CompletedTask, log, disabled: true);
            var resource = tray.Register(Record("Errors"), new Fixture(menu =>
            {
                menu.Add("error", new("Error"), _ => throw new InvalidOperationException("tray failure"));
                menu.Add("timeout", new("Timeout"), _ => throw new OperationCanceledException("unexpected cancellation"));
            }))!;
            Click(Child(tray, "Errors")); Click(Child(tray, "Errors", 1));
            Assert(log.Snapshot().Entries.Count(e => e.Source == "Errors" && e.Level == AppLogLevel.Error) == 2);
            Assert(Child(tray, "Errors").IsEnabled && Child(tray, "Errors", 1).IsEnabled);
            pump(resource.DisposeAsync().AsTask());
        });
    }

    private sealed class Fixture(Action<IPluginTrayMenu> register) : IPlugin, IPluginTrayModule
    {
        public void RegisterTrayMenu(IPluginTrayMenu menu) => register(menu);
        public ValueTask InitializeAsync(PluginContext context, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask StopAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
