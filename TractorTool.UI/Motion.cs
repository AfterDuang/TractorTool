using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Threading;
using FluentAvalonia.Core;

namespace TractorTool.UI;

/// <summary>Shared motion preference for host and plugin controls, including template children.</summary>
public sealed class Motion : AvaloniaObject
{
    private Motion() { }

    public static readonly TimeSpan ThemeDuration = TimeSpan.FromMilliseconds(220);
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Motion, Control, bool>("IsEnabled", true, inherits: true);

    private static readonly ConditionalWeakTable<Control, Suppression> Suppressions = new();

    static Motion() => IsEnabledProperty.Changed.AddClassHandler<Control>((control, _) =>
    {
        if (GetIsEnabled(control))
        {
            if (Suppressions.TryGetValue(control, out var suppression))
            {
                Suppressions.Remove(control);
                suppression.Dispose();
            }
        }
        else
        {
            Suppressions.GetValue(control, static c => new Suppression(c));
        }
    });

    public static bool GetIsEnabled(Control control) => control.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Control control, bool value) => control.SetValue(IsEnabledProperty, value);

    /// <summary>Call before changing theme resources; new controls resolve the same preference.</summary>
    public static void Configure(Application application, bool enabled)
    {
        Dispatcher.UIThread.VerifyAccess();
        FAUISettings.SetAnimationsEnabledAtAppLevel(enabled);
        application.Resources["AppMotionEnabled"] = enabled;
    }

    private sealed class Suppression : IDisposable
    {
        private readonly IDisposable? _transitions;
        private readonly IDisposable? _expansion;

        public Suppression(Control control)
        {
            // Template-local Transitions outrank style setters. A reversible override preserves
            // the native value underneath, including ToggleSwitch's temporary removal during drag.
            _transitions = control.SetValue(Animatable.TransitionsProperty, null, BindingPriority.Animation);
            // Expander captures a running IPageTransition internally. This disables future
            // expansions; an already-started short CrossFade completes under its own lifetime.
            if (control is Expander expander)
                _expansion = expander.SetValue(Expander.ContentTransitionProperty, null, BindingPriority.Animation);
        }

        public void Dispose()
        {
            _expansion?.Dispose();
            _transitions?.Dispose();
        }
    }
}
