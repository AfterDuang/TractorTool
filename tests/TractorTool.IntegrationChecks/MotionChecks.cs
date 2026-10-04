using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.Core;
using TractorTool.UI;

internal static class MotionChecks
{
    public static void Run(Action<string, Action> check)
    {
        void Assert(bool value) { if (!value) throw new Exception("Shared motion assertion failed"); }
        var app = Application.Current!;
        check("Motion preference preserves and restores native switch transitions, including a drag-time local change", () =>
        {
            var original = FAUISettings.AreAnimationsEnabled();
            var toggle = new ToggleSwitch();
            var expander = new Expander { Content = new TextBlock { Text = "Content" } };
            var panel = new StackPanel { Children = { toggle, expander } };
            var window = new Window { Content = panel, Width = 600, Height = 400 };
            try
            {
                Motion.Configure(app, true);
                window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Assert(toggle.IsLoaded);
                var knob = toggle.GetVisualDescendants().OfType<Grid>().Single(c => c.Name == "PART_MovingKnobs");
                var native = knob.Transitions;
                var expansion = expander.ContentTransition;
                Assert(native?.OfType<DoubleTransition>().Any(t => t.Property == Canvas.LeftProperty) == true && expansion is not null);
                var localSurfaces = toggle.GetVisualDescendants().OfType<Control>()
                    .Where(c => c.Transitions is { Count: > 0 }).ToDictionary(c => c, c => c.Transitions);
                Assert(localSurfaces.Count > 1);
                Motion.Configure(app, false);
                Assert(!FAUISettings.AreAnimationsEnabled() && expander.ContentTransition is null);
                Assert(localSurfaces.Keys.All(c => c.Transitions is null));
                Motion.Configure(app, true);
                Assert(ReferenceEquals(expander.ContentTransition, expansion));
                Assert(localSurfaces.All(p => ReferenceEquals(p.Key.Transitions, p.Value)));

                Motion.Configure(app, false);
                // Simulate the native switch removing its underlying local transitions during drag.
                knob.Transitions = null;
                Motion.Configure(app, true);
                Assert(knob.Transitions is null);
                knob.Transitions = native;
                Assert(ReferenceEquals(knob.Transitions, native));
            }
            finally { window.Close(); Motion.Configure(app, original); }
        });
        check("New windows and template children inherit disabled motion without opting into a window class", () =>
        {
            var original = FAUISettings.AreAnimationsEnabled();
            var toggle = new ToggleSwitch();
            var window = new Window { Content = toggle, Width = 400, Height = 200 };
            try
            {
                Motion.Configure(app, false);
                window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Assert(toggle.IsLoaded);
                Assert(!Motion.GetIsEnabled(window) && !Motion.GetIsEnabled(toggle));
                Assert(toggle.GetVisualDescendants().OfType<Control>().All(c => c.Transitions is null));
                var knob = toggle.GetVisualDescendants().OfType<Grid>().Single(c => c.Name == "PART_MovingKnobs");
                Motion.Configure(app, true);
                Assert(knob.Transitions?.OfType<DoubleTransition>().Any(t => t.Property == Canvas.LeftProperty) == true);
                // Plugin-local definitions must survive a preference round-trip too.
                var custom = new Transitions { new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(150) } };
                toggle.Transitions = custom;
                Motion.Configure(app, false); Assert(toggle.Transitions is null);
                Motion.Configure(app, true); Assert(ReferenceEquals(toggle.Transitions, custom));
            }
            finally { window.Close(); Motion.Configure(app, original); }
        });
    }
}
