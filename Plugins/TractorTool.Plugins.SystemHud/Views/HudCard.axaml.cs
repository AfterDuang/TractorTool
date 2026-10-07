using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.VisualTree;
using FluentAvalonia.Core;
using TractorTool.UI;

namespace TractorTool.Plugins.SystemHud;

public partial class HudCard : UserControl
{
    public static readonly StyledProperty<bool> AnimateProperty = AvaloniaProperty.Register<HudCard, bool>(nameof(Animate), true);
    private bool _expanded, _motionEnabled;
    public bool Animate { get => GetValue(AnimateProperty); set => SetValue(AnimateProperty, value); }
    public event Action? ExpansionChanged;
    internal Control[] DragSurfaces => [SummaryButton, CollapseButton];
    internal double Radius => Island.CornerRadius.TopLeft;
    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value) return;
            var restoreFocus = IsKeyboardFocusWithin;
            _expanded = value;
            UpdateMotion();
            // Changing transition targets interrupts/reverses from their current values.
            Width = value ? HudSettings.ExpandedWidth : HudSettings.CardWidth;
            Height = value ? HudSettings.ExpandedHeight : HudSettings.CardHeight;
            Island.CornerRadius = new CornerRadius(value ? 28 : 22);
            ShowScene(SummaryButton, !value);
            ShowScene(Details, value);
            if (restoreFocus) (value ? CollapseButton : SummaryButton).Focus();
            ExpansionChanged?.Invoke();
        }
    }

    public HudCard()
    {
        InitializeComponent();
        SummaryButton.Click += (_, _) => IsExpanded = true;
        CollapseButton.Click += (_, _) => IsExpanded = false;
        foreach (var scene in new Control[] { SummaryButton, Details })
            scene.PropertyChanged += (_, e) =>
            {
                if (e.Property == OpacityProperty && scene.Opacity == 0) scene.IsVisible = false;
            };
    }

    private static void ShowScene(Control scene, bool visible)
    {
        scene.IsEnabled = scene.IsHitTestVisible = visible;
        if (visible) scene.IsVisible = true;
        scene.Opacity = visible ? 1 : 0;
    }

    internal static SpringEasing MorphEasing() => new() { Mass = 1, Stiffness = 360, Damping = 27, InitialVelocity = 0 };

    private void UpdateMotion()
    {
        var enabled = Animate && Motion.GetIsEnabled(this) && FAUISettings.AreAnimationsEnabled()
            && this.IsAttachedToVisualTree() && IsEffectivelyVisible;
        // Native, finite transitions: no animation timer, worker or host clock override.
        if (enabled == _motionEnabled) return;
        _motionEnabled = enabled;
        var duration = TimeSpan.FromMilliseconds(420);
        Transitions = enabled ? new Transitions
        {
            new DoubleTransition { Property = WidthProperty, Duration = duration, Easing = MorphEasing() },
            new DoubleTransition { Property = HeightProperty, Duration = duration, Easing = MorphEasing() }
        } : null;
        Island.Transitions = enabled ? new Transitions
        {
            new CornerRadiusTransition { Property = Border.CornerRadiusProperty, Duration = duration, Easing = MorphEasing() },
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(180) }
        } : null;
        foreach (var scene in new Control[] { SummaryButton, Details })
            scene.Transitions = enabled ? new Transitions
            {
                new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(140), Easing = new CubicEaseOut() }
            } : null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (Island is not null && (change.Property == AnimateProperty || change.Property == Motion.IsEnabledProperty)) UpdateMotion();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); UpdateMotion(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        FinishMotion();
    }

    internal void FinishMotion()
    {
        Transitions = Island.Transitions = SummaryButton.Transitions = Details.Transitions = null;
        _motionEnabled = false;
    }

    internal void PrepareReveal() { FinishMotion(); Island.Opacity = 0; UpdateMotion(); }
    internal void Reveal() { UpdateMotion(); Island.Opacity = 1; }
}
