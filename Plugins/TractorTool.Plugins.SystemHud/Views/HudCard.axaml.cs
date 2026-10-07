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
    private PointerPressedEventArgs? _press;
    private PixelPoint _pressPosition;
    private int? _clickCount;
    private bool _suppressClick, _motionEnabled, _canDoubleClick, _sizeUpdateQueued;
    public bool Animate { get => GetValue(AnimateProperty); set => SetValue(AnimateProperty, value); }
    public HudMode Mode { get => GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    public event Action? StateChanged;
    public event Action<PointerPressedEventArgs>? DragRequested;
    internal double Radius => Island.CornerRadius.TopLeft;

    public HudCard()
    {
        InitializeComponent();
        HeaderButton.Click += (_, _) =>
        {
            if (_suppressClick) return;
            var count = _clickCount; _clickCount = null;
            ApplyClick(count == 2);
            _canDoubleClick = count == 1;
        };
        HeaderButton.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.Pointer.IsPrimary || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            var isDouble = _canDoubleClick && e.ClickCount % 2 == 0;
            _canDoubleClick = false;
            _press = e; _pressPosition = this.PointToScreen(e.GetPosition(this));
            _clickCount = isDouble ? 2 : 1; _suppressClick = false;
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
            _clickCount = null; _suppressClick = _canDoubleClick = false;
            if (e.Key == Key.Down) { SetCurrentValue(ModeProperty, HudMode.Detail); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CancelClick(); SetCurrentValue(ModeProperty, HudMode.Compact); e.Handled = true; }
        };
        BodyScroll.DoubleTapped += (_, e) => { CancelClick(); ApplyClick(true); e.Handled = true; };
        Updated.DoubleTapped += (_, e) => { CancelClick(); ApplyClick(true); e.Handled = true; };
    }

    internal static bool ExceedsDragThreshold(Vector movement) => Math.Abs(movement.X) >= 6 || Math.Abs(movement.Y) >= 6;
    private void ApplyClick(bool doubleClick) => SetCurrentValue(ModeProperty, HudLayout.AfterClick(Mode, doubleClick));
    private void CancelClick() { _press = null; _clickCount = null; _canDoubleClick = false; _suppressClick = true; }

    private void UpdateState()
    {
        UpdateMotion();
        var size = HudSettings.SizeFor(Mode);
        Width = size.Width;
        Island.CornerRadius = new CornerRadius(Mode == HudMode.Detail ? 28 : size.Height / 2);
        var detailed = Mode == HudMode.Detail;
        if (detailed) HeaderGrid.Margin = new Thickness(20, 12);
        // Keep the same definitions: replacing the collections can leave Grid's cell cache stale.
        HeaderGrid.ColumnDefinitions[0].Width = new GridLength(detailed ? 44 : 34);
        HeaderGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
        HeaderGrid.ColumnDefinitions[2].Width = Mode switch
        {
            HudMode.Strip => new GridLength(1, GridUnitType.Star), _ => GridLength.Auto
        };
        HeaderGrid.ColumnDefinitions[2].MaxWidth = detailed ? 210 : double.PositiveInfinity;
        HeaderGrid.ColumnDefinitions[3].Width = Mode == HudMode.Strip ? GridLength.Auto : new GridLength(0);
        HeaderGrid.RowDefinitions[0].Height = detailed ? new GridLength(28) : new GridLength(1, GridUnitType.Star);
        HeaderGrid.RowDefinitions[1].Height = detailed ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        First.Margin = new Thickness(0, 0, detailed ? 0 : 10, 0);
        First.HorizontalAlignment = detailed ? Avalonia.Layout.HorizontalAlignment.Left : Avalonia.Layout.HorizontalAlignment.Stretch;
        First.IconSize = detailed ? 32 : 24;
        Second.Margin = new Thickness(0, 0, detailed ? 0 : 10, 0);
        Grid.SetColumn(Second, detailed ? 2 : 1);
        Grid.SetRowSpan(First, detailed ? 2 : 1);
        Grid.SetColumn(Third, detailed ? 1 : 2);
        Grid.SetRowSpan(Third, detailed ? 2 : 1);
        Grid.SetColumn(Fourth, detailed ? 2 : 3);
        Second.FontSize = detailed ? 24 : 18;
        Second.TextAlignment = detailed ? Avalonia.Media.TextAlignment.Right : Avalonia.Media.TextAlignment.Left;
        Third.MaxWidth = Mode == HudMode.Compact ? 64 : double.PositiveInfinity;
        Third.FontSize = Mode switch { HudMode.Compact => 12, HudMode.Strip => 16, _ => 26 };
        Third.FontWeight = detailed ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal;
        Third.Foreground = detailed ? Avalonia.Media.Brushes.WhiteSmoke : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#BDBDC5"));
        Third.TextAlignment = detailed ? Avalonia.Media.TextAlignment.Left : Avalonia.Media.TextAlignment.Right;
        Fourth.FontSize = detailed ? 15 : 12;
        Fourth.Margin = new Thickness(detailed ? 14 : 0, 0, 0, 0);
        Fourth.MaxWidth = detailed ? double.PositiveInfinity : 64;
        UpdateContent();
        StateChanged?.Invoke();
    }

    private void UpdateContent()
    {
        if (DataContext is not HudDisplay display)
        {
            First.Item = Second.Item = Third.Item = Fourth.Item = Body.Item = null;
            Updated.Text = null;
            QueueSizeUpdate();
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
            var header = Mode == HudMode.Detail ? layout.Expanded : layout.Strip;
            First.Item = header.Left;
            Second.Item = header.Primary;
            Third.Item = header.Secondary;
            Fourth.Item = header.Right;
        }
        Body.Item = layout.Detail;
        Updated.Text = display.Updated;
        QueueSizeUpdate();
    }

    private void QueueSizeUpdate()
    {
        if (_sizeUpdateQueued) return;
        _sizeUpdateQueued = true;
        // Slot bindings must finish propagating before measuring their new text.
        Dispatcher.UIThread.Post(() => { _sizeUpdateQueued = false; UpdateSize(); }, DispatcherPriority.Loaded);
    }

    private void UpdateSize()
    {
        var size = HudSettings.SizeFor(Mode);
        var detailed = Mode == HudMode.Detail;
        var hasAuxiliary = Fourth.Item is { } item && (!item.IsText || !string.IsNullOrWhiteSpace(item.Text));
        HeaderButton.Height = detailed ? hasAuxiliary ? 86 : 66 : size.Height - 2;
        Grid.SetRow(Second, detailed && hasAuxiliary ? 1 : 0);
        Grid.SetRowSpan(Second, detailed && !hasAuxiliary ? 2 : 1);
        Fourth.IsVisible = Mode == HudMode.Strip || detailed && hasAuxiliary;
        Third.Margin = new Thickness(0, 0, Mode == HudMode.Strip && hasAuxiliary ? 12 : 0, 0);
        BodyScroll.IsVisible = detailed && !string.IsNullOrWhiteSpace(Body.Item?.Text);
        Updated.IsVisible = detailed && !string.IsNullOrWhiteSpace(Updated.Text);
        DetailDivider.IsVisible = BodyScroll.IsVisible || Updated.IsVisible;
        FooterGrid.IsVisible = DetailDivider.IsVisible;
        ContentGrid.Margin = new Thickness(0, 0, 0, detailed ? 12 : 0);
        if (!detailed)
        {
            Height = size.Height;
            var trailing = Mode == HudMode.Strip && hasAuxiliary ? Fourth.Item : Third.Item;
            var trailingHalfWidth = trailing is { IsRing: true } ? 14 : trailing is { IsIcon: true } ? 12 : 0;
            HeaderGrid.Margin = new Thickness(size.Height / 2 - 14, 0, size.Height / 2 - trailingHalfWidth - 2, 0);
            return;
        }

        // Measure at the target width so animation frames don't change text wrapping or the target height.
        var available = new Size(size.Width - 2, double.PositiveInfinity);
        Updated.Margin = new Thickness(BodyScroll.IsVisible && Updated.IsVisible ? 16 : 0, 0, 0, 0);
        Updated.Measure(Size.Infinity);
        var footerWidth = available.Width - FooterGrid.Margin.Left - FooterGrid.Margin.Right;
        var fixedHeight = 2 + HeaderButton.Height + ContentGrid.Margin.Bottom
            + (FooterGrid.IsVisible ? 1 + FooterGrid.Margin.Top : 0);
        var bodyHeight = 0d;
        if (BodyScroll.IsVisible)
        {
            Body.Measure(new Size(Math.Max(0, footerWidth - Updated.DesiredSize.Width), double.PositiveInfinity));
            BodyScroll.MaxHeight = Math.Min(Body.DesiredSize.Height,
                Math.Max(0, size.Height - fixedHeight));
            bodyHeight = BodyScroll.MaxHeight;
        }
        Height = fixedHeight + Math.Max(bodyHeight, Updated.DesiredSize.Height);
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
