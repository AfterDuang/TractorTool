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
    private readonly DispatcherTimer _singleClick = new();
    private PointerPressedEventArgs? _press;
    private PixelPoint _pressPosition;
    private int? _clickCount;
    private bool _suppressClick, _motionEnabled;
    public bool Animate { get => GetValue(AnimateProperty); set => SetValue(AnimateProperty, value); }
    public HudMode Mode { get => GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    public event Action? StateChanged;
    public event Action<PointerPressedEventArgs>? DragRequested;
    internal double Radius => Island.CornerRadius.TopLeft;

    public HudCard()
    {
        InitializeComponent();
        _singleClick.Tick += (_, _) => { _singleClick.Stop(); ApplyClick(false); };
        HeaderButton.Click += (_, _) =>
        {
            if (_suppressClick) return;
            var count = _clickCount; _clickCount = null;
            _singleClick.Stop();
            if (count is null) ApplyClick(false); // Keyboard activation needs no double-click wait.
            else if (count.Value % 2 == 0) ApplyClick(true);
            else _singleClick.Start();
        };
        HeaderButton.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.Pointer.IsPrimary || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            var pending = _singleClick.IsEnabled;
            var isDouble = pending && e.ClickCount % 2 == 0;
            _singleClick.Stop();
            if (pending && !isDouble) ApplyClick(false);
            _press = e; _pressPosition = this.PointToScreen(e.GetPosition(this));
            _clickCount = isDouble ? 2 : 1; _suppressClick = false;
            _singleClick.Interval = this.GetPlatformSettings()?.GetDoubleTapTime(e.Pointer.Type) ?? TimeSpan.FromMilliseconds(500);
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
        HeaderButton.KeyDown += (_, e) =>
        {
            _singleClick.Stop(); _clickCount = null; _suppressClick = false;
            if (e.Key == Key.Down) { SetCurrentValue(ModeProperty, HudMode.Detail); e.Handled = true; }
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CancelClick(); SetCurrentValue(ModeProperty, HudMode.Compact); e.Handled = true; }
        };
        BodyScroll.DoubleTapped += (_, e) => { CancelClick(); ApplyClick(true); e.Handled = true; };
    }

    internal static bool ExceedsDragThreshold(Vector movement) => Math.Abs(movement.X) >= 6 || Math.Abs(movement.Y) >= 6;
    private void ApplyClick(bool doubleClick) => SetCurrentValue(ModeProperty, HudLayout.AfterClick(Mode, doubleClick));
    private void CancelClick() { _singleClick.Stop(); _press = null; _clickCount = null; _suppressClick = true; }

    private void UpdateState()
    {
        UpdateMotion();
        var size = HudSettings.SizeFor(Mode);
        Width = size.Width; Height = size.Height;
        Island.CornerRadius = new CornerRadius(Mode == HudMode.Detail ? 28 : size.Height / 2);
        HeaderButton.Height = Mode == HudMode.Detail ? 66 : size.Height - 2;
        HeaderGrid.ColumnDefinitions = new ColumnDefinitions(Mode switch
        {
            HudMode.Compact => "28,*,Auto",
            HudMode.Strip => "28,1.35*,*,Auto",
            _ => "36,*,Auto"
        });
        HeaderGrid.RowDefinitions = new RowDefinitions(Mode == HudMode.Detail ? "*,*" : "*");
        Grid.SetRowSpan(First, Mode == HudMode.Detail ? 2 : 1);
        Grid.SetRowSpan(Second, Mode == HudMode.Detail ? 2 : 1);
        Grid.SetColumn(Fourth, Mode == HudMode.Detail ? 2 : 3);
        Grid.SetRow(Fourth, Mode == HudMode.Detail ? 1 : 0);
        Third.MaxWidth = Mode switch { HudMode.Compact => 64, HudMode.Detail => 148, _ => double.PositiveInfinity };
        Third.FontSize = Mode == HudMode.Compact ? 12 : 14;
        Fourth.MaxWidth = 64;
        Fourth.IsVisible = Mode != HudMode.Compact;
        // Visibility follows state, never animated opacity (zero is also the start of a fade-in).
        BodyScroll.IsVisible = Mode == HudMode.Detail;
        UpdateContent();
        StateChanged?.Invoke();
    }

    private void UpdateContent()
    {
        if (DataContext is not HudDisplay display) return;
        var layout = display.Layout;
        if (Mode == HudMode.Compact)
        {
            First.DataContext = layout.Compact.Left;
            Second.DataContext = layout.Compact.Center;
            Third.DataContext = layout.Compact.Right;
        }
        else
        {
            First.DataContext = layout.Strip.Left;
            Second.DataContext = layout.Strip.Primary;
            Third.DataContext = layout.Strip.Secondary;
            Fourth.DataContext = layout.Strip.Right;
        }
        Body.DataContext = layout.Detail;
    }

    internal static SpringEasing MorphEasing() => new() { Mass = 1, Stiffness = 360, Damping = 27, InitialVelocity = 0 };
    private void UpdateMotion()
    {
        var enabled = Animate && Motion.GetIsEnabled(this) && FAUISettings.AreAnimationsEnabled()
            && this.IsAttachedToVisualTree() && IsEffectivelyVisible;
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
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (Island is null) return;
        if (change.Property == ModeProperty) UpdateState();
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
