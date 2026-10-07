using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.Core;
using TractorTool.UI;

namespace TractorTool.Plugins.SystemHud;

public partial class HudCard : UserControl
{
    public static readonly StyledProperty<bool> AnimateProperty = AvaloniaProperty.Register<HudCard, bool>(nameof(Animate), true);
    public static readonly StyledProperty<HudMode> ModeProperty = AvaloniaProperty.Register<HudCard, HudMode>(nameof(Mode));
    private readonly DispatcherTimer _collapseClick = new();
    private PointerPressedEventArgs? _press;
    private PixelPoint _pressPosition;
    private int? _clickCount;
    private bool _suppressClick, _motionEnabled, _canDoubleClick;
    public bool Animate { get => GetValue(AnimateProperty); set => SetValue(AnimateProperty, value); }
    public HudMode Mode { get => GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    public event Action? StateChanged;
    public event Action<PointerPressedEventArgs>? DragRequested;
    internal double Radius => Island.CornerRadius.TopLeft;

    public HudCard()
    {
        InitializeComponent();
        _collapseClick.Tick += (_, _) => { _collapseClick.Stop(); _canDoubleClick = false; ApplyClick(false); };
        HeaderButton.Click += (_, _) =>
        {
            if (_suppressClick) return;
            var count = _clickCount; _clickCount = null;
            _collapseClick.Stop();
            if (count == 2) ApplyClick(true);
            // Expansion responds on release. Only defer mouse collapse: shrinking first can
            // remove the second click's target at either end of the strip (also with motion off).
            else if (count == 1 && Mode == HudMode.Strip) _collapseClick.Start();
            else ApplyClick(false);
            _canDoubleClick = count == 1;
        };
        HeaderButton.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.Pointer.IsPrimary || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            var isDouble = _canDoubleClick && e.ClickCount % 2 == 0;
            _collapseClick.Stop();
            _canDoubleClick = false;
            _press = e; _pressPosition = this.PointToScreen(e.GetPosition(this));
            _clickCount = isDouble ? 2 : 1; _suppressClick = false;
            _collapseClick.Interval = this.GetPlatformSettings()?.GetDoubleTapTime(e.Pointer.Type) ?? TimeSpan.FromMilliseconds(500);
        }, RoutingStrategies.Tunnel);
        HeaderButton.AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (_press is not { } press || press.Pointer != e.Pointer || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            var current = this.PointToScreen(e.GetPosition(this));
            var dpi = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            if (!ExceedsDragThreshold(new((current.X - _pressPosition.X) / dpi, (current.Y - _pressPosition.Y) / dpi))) return;
            CancelClick();
            e.Pointer.Capture(null); // Cancel the native Button click before asking the desktop window to move.
            e.Handled = true;
            DragRequested?.Invoke(press);
        }, RoutingStrategies.Tunnel);
        HeaderButton.AddHandler(PointerReleasedEvent, (_, _) => _press = null, RoutingStrategies.Tunnel);
        HeaderButton.PointerCaptureLost += (_, _) => { if (_press is not null) CancelClick(); };
        HeaderButton.AddHandler(KeyDownEvent, (_, e) =>
        {
            _collapseClick.Stop(); _clickCount = null; _suppressClick = _canDoubleClick = false;
            if (e.Key == Key.Down) { SetCurrentValue(ModeProperty, HudMode.Detail); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CancelClick(); SetCurrentValue(ModeProperty, HudMode.Compact); e.Handled = true; }
        };
        BodyScroll.DoubleTapped += (_, e) => { CancelClick(); ApplyClick(true); e.Handled = true; };
    }

    internal static bool ExceedsDragThreshold(Vector movement) => Math.Abs(movement.X) >= 6 || Math.Abs(movement.Y) >= 6;
    private void ApplyClick(bool doubleClick) => SetCurrentValue(ModeProperty, HudLayout.AfterClick(Mode, doubleClick));
    private void CancelClick() { _collapseClick.Stop(); _press = null; _clickCount = null; _canDoubleClick = false; _suppressClick = true; }

    private void UpdateState()
    {
        UpdateMotion();
        var size = HudSettings.SizeFor(Mode);
        Width = size.Width; Height = size.Height;
        Island.CornerRadius = new CornerRadius(Mode == HudMode.Detail ? 28 : size.Height / 2);
        var detailed = Mode == HudMode.Detail;
        HeaderButton.Height = detailed ? 84 : size.Height - 2;
        HeaderGrid.Margin = detailed ? new Thickness(20, 10, 20, 0) : new Thickness(14, 0);
        // Keep the same definitions: replacing the collections can leave Grid's cell cache stale.
        HeaderGrid.ColumnDefinitions[0].Width = new GridLength(detailed ? 56 : 38);
        HeaderGrid.ColumnDefinitions[1].Width = new GridLength(detailed ? 1.35 : 1, GridUnitType.Star);
        HeaderGrid.ColumnDefinitions[2].Width = Mode == HudMode.Compact ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        HeaderGrid.ColumnDefinitions[3].Width = Mode == HudMode.Strip ? GridLength.Auto : new GridLength(0);
        HeaderGrid.RowDefinitions[1].Height = detailed ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        First.Margin = Second.Margin = new Thickness(0, 0, detailed ? 16 : 10, 0);
        Third.Margin = new Thickness(0, 0, Mode == HudMode.Strip ? 20 : 0, 0);
        Grid.SetRowSpan(First, detailed ? 2 : 1);
        Grid.SetRow(Second, detailed ? 1 : 0);
        Grid.SetRow(Third, detailed ? 1 : 0);
        Grid.SetColumn(Fourth, detailed ? 2 : 3);
        Third.MaxWidth = Mode == HudMode.Compact ? 64 : double.PositiveInfinity;
        Third.FontSize = Mode switch { HudMode.Compact => 12, HudMode.Strip => 16, _ => 14 };
        Fourth.MaxWidth = detailed ? 148 : 64;
        Fourth.IsVisible = Mode != HudMode.Compact;
        // Visibility follows state, never animated opacity (zero is also the start of a fade-in).
        BodyScroll.IsVisible = Mode == HudMode.Detail;
        UpdateContent();
        StateChanged?.Invoke();
    }

    private void UpdateContent()
    {
        if (DataContext is not HudDisplay display)
        {
            First.Item = Second.Item = Third.Item = Fourth.Item = Body.Item = null;
            return;
        }
        var layout = display.Layout;
        if (Mode == HudMode.Compact)
        {
            First.Item = layout.Compact.Left;
            Second.Item = layout.Compact.Center;
            Third.Item = layout.Compact.Right;
        }
        else
        {
            First.Item = layout.Strip.Left;
            Second.Item = layout.Strip.Primary;
            Third.Item = layout.Strip.Secondary;
            Fourth.Item = layout.Strip.Right;
        }
        Body.Item = layout.Detail;
    }

    internal static SpringEasing MorphEasing() => new() { Mass = 1, Stiffness = 360, Damping = 27, InitialVelocity = 0 };
    private void UpdateMotion()
    {
        var enabled = Animate && Motion.GetIsEnabled(this) && FAUISettings.AreAnimationsEnabled()
            && this.IsAttachedToVisualTree() && IsEffectivelyVisible;
        if (enabled == _motionEnabled) return;
        _motionEnabled = enabled;
        var duration = TimeSpan.FromMilliseconds(320);
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
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (Island is null) return;
        if (change.Property == ModeProperty) { CancelClick(); UpdateState(); }
        else if (change.Property == DataContextProperty) UpdateContent();
        else if (change.Property == AnimateProperty || change.Property == Motion.IsEnabledProperty) UpdateMotion();
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); UpdateState(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { base.OnDetachedFromVisualTree(e); FinishMotion(); }
    internal void FinishMotion()
    {
        CancelClick();
        Transitions = Island.Transitions = null;
        _motionEnabled = false;
    }
    internal void PrepareReveal() { FinishMotion(); Island.Opacity = 0; UpdateMotion(); }
    internal void Reveal() { UpdateMotion(); Island.Opacity = 1; }
}
