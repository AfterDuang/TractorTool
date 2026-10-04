using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using FluentAvalonia.Core;
using FluentAvalonia.Styling;
using TractorTool.Core.Logging;
using TractorTool.Core.Storage;
using TractorTool.Services;
using TractorTool.ViewModels;
using TractorTool.Views;

internal static class AppearanceChecks
{
    public static void Run(Action<string, Action> check, Action<Task> pump, string output)
    {
        foreach (var failFirst in new[] { false, true })
        {
            check($"Appearance coalesces rapid changes and drains latest on exit (first write fails: {failFirst})", () =>
            {
                var paths = Paths(output, "rapid-" + failFirst);
                using var started = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                var writes = 0;
                var store = new JsonFileStore<HostSettings>(Path.Combine(paths.ConfigRoot, "settings.json"), next =>
                {
                    HostSettings.Validate(next);
                    if (Interlocked.Increment(ref writes) != 1) return;
                    started.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("fixture writer blocked");
                    if (failFirst) throw new IOException("fixture first write failed");
                });
                var initial = new HostSettings { DisabledPlugins = ["Test.KeepDisabled"] };
                ThemeService.Apply(initial);
                var vm = new SettingsViewModel(paths, store, initial, new AppLog(paths.LogRoot));
                try
                {
                    Require(!File.Exists(store.FilePath) && vm.SaveCompletion.IsCompleted);
                    vm.SelectedTheme = vm.Themes.Single(t => t.Id == "Dark");
                    var completion = vm.SaveCompletion;
                    Require(Application.Current!.RequestedThemeVariant == ThemeVariant.Dark);
                    Require(started.Wait(TimeSpan.FromSeconds(5)) && !completion.IsCompleted);
                    for (var i = 0; i < 20; i++)
                        vm.SelectedTheme = vm.Themes.Single(t => t.Id == (i % 2 == 0 ? "Light" : "Dark"));
                    vm.SelectedTheme = vm.Themes.Single(t => t.Id == "Light");
                    vm.SelectedAccent = vm.Accents.Single(a => a.Value == "#0F766E");
                    vm.EnableAnimations = false;
                    Require(Application.Current.RequestedThemeVariant == ThemeVariant.Light);
                    Require(Application.Current.Styles.OfType<FluentAvaloniaTheme>().Single().CustomAccentColor == Color.Parse("#0F766E"));
                    Require(!FAUISettings.AreAnimationsEnabled());
                    var shutdown = vm.BeginShutdown();
                    Require(ReferenceEquals(completion, shutdown) && !shutdown.IsCompleted);
                    // A queued snapshot, rather than mutable bound controls, owns the exit write.
                    vm.SelectedTheme = vm.Themes.Single(t => t.Id == "Dark");
                    release.Set();
                    pump(shutdown);
                    Require(writes == 2 && !vm.HasSaveError && !vm.IsSaving);
                    var saved = new JsonFileStore<HostSettings>(store.FilePath).Load();
                    Require(saved.Theme == "Light" && saved.Accent == "#0F766E" && !saved.EnableAnimations);
                    Require(saved.DisabledPlugins.SequenceEqual(initial.DisabledPlugins));
                    Require(Application.Current.RequestedThemeVariant == ThemeVariant.Light);
                    ThemeService.Apply(saved);
                    var reopened = new SettingsViewModel(paths, store, saved, new AppLog(paths.LogRoot));
                    Require(reopened.SelectedTheme.Id == "Light" && !reopened.EnableAnimations && writes == 2);
                }
                finally { release.Set(); pump(vm.BeginShutdown()); }
            });
        }

        check("Appearance write failure preserves file and applied theme; retry persists latest", () =>
        {
            var paths = Paths(output, "retry");
            var file = Path.Combine(paths.ConfigRoot, "settings.json");
            var reader = new JsonFileStore<HostSettings>(file);
            reader.Save(new());
            var original = File.ReadAllBytes(file);
            var failWrite = true;
            var writes = 0;
            var store = new JsonFileStore<HostSettings>(file, next =>
            {
                HostSettings.Validate(next);
                Interlocked.Increment(ref writes);
                if (Volatile.Read(ref failWrite)) throw new IOException("fixture storage unavailable");
            });
            var vm = new SettingsViewModel(paths, store, reader.Load(), new AppLog(paths.LogRoot));
            var view = new SettingsAppearanceView();
            var errorPanel = view.FindControl<WrapPanel>("SaveErrorPanel")!;
            // VS Code preview constructs the view without a runtime data context.
            Require(!errorPanel.IsVisible);
            view.DataContext = vm;
            Require(vm.SaveError.Length == 0 && !errorPanel.IsVisible);
            vm.SelectedTheme = vm.Themes.Single(t => t.Id == "Dark");
            pump(vm.SaveCompletion);
            Require(writes == 1 && vm.HasSaveError && vm.RetrySaveCommand.CanExecute(null));
            Require(errorPanel.IsVisible && vm.SaveError.Contains("未保存"));
            view.DataContext = null;
            Require(!errorPanel.IsVisible);
            view.DataContext = vm;
            Require(errorPanel.IsVisible);
            Require(Application.Current!.RequestedThemeVariant == ThemeVariant.Dark);
            Require(File.ReadAllBytes(file).SequenceEqual(original));
            var retryButton = errorPanel.Children.OfType<Button>().Single();
            Require(ReferenceEquals(retryButton.Command, vm.RetrySaveCommand) && retryButton.Command!.CanExecute(null));
            Volatile.Write(ref failWrite, false);
            retryButton.Command!.Execute(null);
            pump(vm.SaveCompletion);
            Require(writes == 2 && !vm.HasSaveError && reader.Load().Theme == "Dark");
            Require(vm.SaveError.Length == 0 && !errorPanel.IsVisible);
            Require(!vm.RetrySaveCommand.CanExecute(null));
            pump(vm.BeginShutdown());
        });

        check("Appearance exit retries a failed write once and does not loop", () =>
        {
            var paths = Paths(output, "failed-exit");
            var writes = 0;
            var store = new JsonFileStore<HostSettings>(Path.Combine(paths.ConfigRoot, "settings.json"), _ =>
            {
                Interlocked.Increment(ref writes);
                throw new IOException("fixture persistent failure");
            });
            var vm = new SettingsViewModel(paths, store, new(), new AppLog(paths.LogRoot));
            vm.EnableAnimations = false;
            pump(vm.SaveCompletion);
            Require(writes == 1 && vm.HasSaveError);
            var shutdown = vm.BeginShutdown();
            pump(shutdown);
            Require(writes == 2 && vm.HasSaveError && !vm.IsSaving);
            Require(ReferenceEquals(shutdown, vm.BeginShutdown()));
            vm.RetrySaveCommand.Execute(null);
            Require(writes == 2 && !vm.RetrySaveCommand.CanExecute(null));
        });

        check("Transient null selections do not apply or persist until both choices are valid", () =>
        {
            var paths = Paths(output, "null-selection");
            var store = new JsonFileStore<HostSettings>(Path.Combine(paths.ConfigRoot, "settings.json"));
            var initial = new HostSettings { Theme = "Dark", Accent = "#123456" };
            ThemeService.Apply(initial);
            var vm = new SettingsViewModel(paths, store, initial, new AppLog(paths.LogRoot));
            Require(vm.Accents.Contains(vm.SelectedAccent) && !File.Exists(store.FilePath));
            var theme = vm.SelectedTheme;
            var accent = vm.SelectedAccent;
            var completion = vm.SaveCompletion;
            vm.SelectedTheme = null!;
            vm.SelectedAccent = null!;
            vm.EnableAnimations = false;
            vm.SelectedTheme = theme;
            Require(ReferenceEquals(completion, vm.SaveCompletion) && !File.Exists(store.FilePath));
            Require(Application.Current!.RequestedThemeVariant == ThemeVariant.Dark);
            vm.SelectedAccent = accent;
            pump(vm.SaveCompletion);
            var saved = store.Load();
            Require(saved.Theme == "Dark" && saved.Accent == "#123456" && !saved.EnableAnimations);
            pump(vm.BeginShutdown());
        });

        check("Theme notification reentry preserves the final choice", () =>
        {
            var paths = Paths(output, "reentry");
            var store = new JsonFileStore<HostSettings>(Path.Combine(paths.ConfigRoot, "settings.json"));
            ThemeService.Apply(new() { Theme = "Light" });
            var vm = new SettingsViewModel(paths, store, new() { Theme = "Light" }, new AppLog(paths.LogRoot));
            var changed = false;
            void OnThemeChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
            {
                if (changed || args.Property != Application.RequestedThemeVariantProperty) return;
                changed = true;
                vm.SelectedTheme = vm.Themes.Single(t => t.Id == "Light");
            }
            Application.Current!.PropertyChanged += OnThemeChanged;
            try
            {
                vm.SelectedTheme = vm.Themes.Single(t => t.Id == "Dark");
                pump(vm.SaveCompletion);
                Require(changed && vm.SelectedTheme.Id == "Light");
                Require(Application.Current.RequestedThemeVariant == ThemeVariant.Light && store.Load().Theme == "Light");
            }
            finally { Application.Current.PropertyChanged -= OnThemeChanged; pump(vm.BeginShutdown()); }
        });

        check("Theme application error is contained and is distinct from storage failure", () =>
        {
            var paths = Paths(output, "apply-error");
            var store = new JsonFileStore<HostSettings>(Path.Combine(paths.ConfigRoot, "settings.json"));
            ThemeService.Apply(new() { Theme = "Light" });
            var vm = new SettingsViewModel(paths, store, new() { Theme = "Light" }, new AppLog(paths.LogRoot));
            vm.SelectedTheme = new("invalid", "invalid");
            Require(vm.HasApplyError && !vm.HasSaveError && !File.Exists(store.FilePath));
            Require(Application.Current!.RequestedThemeVariant == ThemeVariant.Light);
            vm.SelectedTheme = vm.Themes.Single(t => t.Id == "Dark");
            pump(vm.SaveCompletion);
            Require(!vm.HasApplyError && store.Load().Theme == "Dark");
            pump(vm.BeginShutdown());
        });
    }

    private static AppPaths Paths(string output, string name)
    {
        var paths = new AppPaths(Path.Combine(output, "appearance-" + name, "app"),
            Path.Combine(output, "appearance-" + name, "profile"));
        paths.EnsureDataDirectories();
        return paths;
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Appearance check failed");
    }
}
