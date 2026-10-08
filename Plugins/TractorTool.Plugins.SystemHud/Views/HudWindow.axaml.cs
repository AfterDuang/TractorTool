using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace TractorTool.Plugins.SystemHud;

public partial class HudWindow : Window
{
    // Stable transparent envelope leaves room for the spring; only the visible island receives input.
    public const double FrameWidth = 432;
    public const double FrameHeight = 208;
    private readonly List<nint> _notifications = [];
    private HudSettings _settings = new();
    private bool _closing;
    private bool _regionFailed;
    private (PixelRect Bounds, int Diameter)? _region;
    public event Action? PowerChanged;
    public event Action? Dismissed;
    public event Action? DragStarted;
    public event Action<HudSettings>? DragCompleted;
    public event Action? StateChanged;
    public event Action<Exception>? PresentationFailed;
    public bool IsDragging { get; private set; }
    public HudMode Mode => Card.Mode;
    public bool IsExpanded => Card.Mode != HudMode.Compact;

    public HudWindow()
    {
        InitializeComponent();
        Win32Properties.AddWindowStylesCallback(this, WindowStyles);
        Win32Properties.AddWndProcHookCallback(this, WndProc);
        Screens.Changed += ScreensChanged;
        ScalingChanged += (_, _) => { Reposition(); UpdateRegion(); };
        Closing += (_, e) => { if (!_closing) { e.Cancel = true; Dismissed?.Invoke(); } };
        Card.StateChanged += () =>
        {
            if (IsVisible) StateChanged?.Invoke();
        };
        Card.SizeChanged += (_, _) => { Reposition(); UpdateRegion(); };
        Card.DragRequested += press =>
        {
            if (_closing || IsDragging || _settings.ClickThrough) return;
            Card.FinishMotion();
            IsDragging = true;
            DragStarted?.Invoke();
            BeginMoveDrag(press);
        };
    }

    public void Collapse() => Card.Mode = HudMode.Compact;

    public void RegisterPowerEvents()
    {
        try
        {
            var handle = TryGetPlatformHandle()?.Handle ?? throw new InvalidOperationException("无法创建 HUD 窗口。");
            foreach (var id in new[] { "5d3e9a59-e9d5-4b00-a6bd-ff34ff516548", "a7ad8041-b45a-4cae-87a3-eecbb468a9e1" })
            {
                var guid = Guid.Parse(id);
                var registration = RegisterPowerSettingNotification(handle, ref guid, 0);
                if (registration == 0) throw new Win32Exception();
                _notifications.Add(registration);
            }
        }
        catch { UnregisterPowerEvents(); throw; }
    }

    private void UnregisterPowerEvents()
    {
        foreach (var notification in _notifications) UnregisterPowerSettingNotification(notification);
        _notifications.Clear();
    }

    private nint WndProc(nint window, uint message, nint wParam, nint lParam, ref bool handled)
    {
        // Native callbacks only post work: exceptions must never cross the WndProc boundary.
        if (message == 0x0218 && (wParam == 0x8013 || wParam == 0x12 || wParam == 0x7 || wParam == 0xA))
            Dispatcher.UIThread.Post(() => { if (!_closing) PowerChanged?.Invoke(); });
        if (message == 0x0084) // WM_NCHITTEST uses signed screen coordinates, including negative-origin monitors.
        {
            var x = unchecked((short)((long)lParam & 0xffff));
            var y = unchecked((short)(((long)lParam >> 16) & 0xffff));
            var scale = RenderScaling * _settings.Scale;
            var point = new Point((x - Position.X) / scale, (y - Position.Y) / scale);
            if (_settings.ClickThrough || !ContainsIsland(point, VisibleSize, Card.Radius)) { handled = true; return -1; }
        }
        if (message == 0x0021) { handled = true; return 3; } // MA_NOACTIVATE
        if (message == 0x0232) // WM_EXITSIZEMOVE: persist only after the native drag ends.
            Dispatcher.UIThread.Post(CompleteDrag);
        return 0;
    }

    private (uint, uint) WindowStyles(uint style, uint exStyle)
        => (style, _settings.ClickThrough ? exStyle | 0x080800A0u : (exStyle | 0x08080080u) & ~0x20u);

    public void Configure(HudSettings settings)
    {
        _settings = settings;
        if (settings.ClickThrough) Collapse();
        Card.Animate = settings.Animate;
        Width = FrameWidth * settings.Scale;
        Height = FrameHeight * settings.Scale;
        Reposition();
        UpdateRegion();
        var handle = TryGetPlatformHandle()?.Handle ?? 0;
        if (handle != 0)
        {
            var old = unchecked((uint)GetWindowLong(handle, -20));
            var updated = WindowStyles(0, old).Item2;
            Marshal.SetLastPInvokeError(0);
            if (SetWindowLong(handle, -20, unchecked((int)updated)) == 0 && Marshal.GetLastPInvokeError() != 0)
                throw new Win32Exception();
        }
    }

    private void CompleteDrag()
    {
        if (_closing || !IsDragging) return;
        IsDragging = false;
        var screen = Screens.ScreenFromPoint(new PixelPoint(Position.X + (int)(Bounds.Width * RenderScaling / 2),
            Position.Y + (int)(Bounds.Height * RenderScaling / 2))) ?? Screens.Primary;
        if (screen is null) { DragCompleted?.Invoke(_settings); return; }
        var monitor = Screens.All.ToList().IndexOf(screen) + 1;
        var inset = FrameInset(VisibleSize, _settings.Scale * screen.Scaling);
        var islandPosition = new PixelPoint(Position.X + inset.X, Position.Y + inset.Y);
        DragCompleted?.Invoke(_settings.AtPosition(islandPosition, screen.WorkingArea, screen.Scaling, screen.DisplayName, monitor, VisibleSize));
    }

    public void Reveal()
    {
        if (IsVisible) return;
        _regionFailed = false; _region = null;
        Card.PrepareReveal();
        Show();
        Reposition();
        UpdateRegion();
        Dispatcher.UIThread.Post(() => { if (!_closing && IsVisible) Card.Reveal(); }, DispatcherPriority.Render);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (Card is not null && change.Property == IsVisibleProperty && !IsVisible) Card.FinishMotion();
    }

    private Size VisibleSize => Card.Bounds.Width > 0 ? Card.Bounds.Size : new Size(Card.Width, Card.Height);

    internal static PixelPoint FrameInset(Size size, double scale) => new(
        (int)Math.Round((FrameWidth - size.Width) * scale / 2), (int)Math.Round((FrameHeight - size.Height) * scale / 2));

    internal static bool ContainsIsland(Point point, Size size, double radius)
    {
        var x = Math.Abs(point.X - FrameWidth / 2);
        var y = Math.Abs(point.Y - FrameHeight / 2);
        if (x > size.Width / 2 || y > size.Height / 2) return false;
        radius = Math.Clamp(radius, 0, Math.Min(size.Width, size.Height) / 2);
        var dx = Math.Max(0, x - size.Width / 2 + radius);
        var dy = Math.Max(0, y - size.Height / 2 + radius);
        return dx * dx + dy * dy <= radius * radius;
    }

    private void UpdateRegion()
    {
        if (_closing || _regionFailed || TryGetPlatformHandle()?.Handle is not { } handle || handle == 0) return;
        var scale = RenderScaling * _settings.Scale;
        var size = VisibleSize;
        var inset = FrameInset(size, scale);
        // One pixel of antialiasing allowance; the large transparent envelope is never an input surface.
        var bounds = new PixelRect(inset.X - 1, inset.Y - 1, (int)Math.Ceiling(size.Width * scale) + 2, (int)Math.Ceiling(size.Height * scale) + 2);
        var diameter = (int)Math.Ceiling(Card.Radius * 2 * scale);
        if (_region == (bounds, diameter)) return;
        _region = (bounds, diameter);
        var region = CreateRoundRectRgn(bounds.X, bounds.Y, bounds.Right, bounds.Bottom, diameter, diameter);
        if (region != 0 && SetWindowRgn(handle, region, true) != 0) return; // Windows owns the successful region.
        var error = new Win32Exception(Marshal.GetLastPInvokeError());
        if (region != 0) DeleteObject(region);
        _regionFailed = true;
        // Do not repeatedly throw from layout/native callbacks or leave an invisible input blocker.
        Dispatcher.UIThread.Post(() =>
        {
            if (_closing) return;
            Hide(); Dismissed?.Invoke(); PresentationFailed?.Invoke(error);
        });
    }

    private void ScreensChanged(object? sender, EventArgs e) => Reposition();
    private void Reposition()
    {
        if (IsDragging) return;
        var index = _settings.Monitor - 1;
        var screen = _settings.HasPosition && _settings.ScreenName is { } name
            ? Screens.All.FirstOrDefault(s => s.DisplayName == name) ?? Screens.Primary
            : index >= 0 && index < Screens.All.Count ? Screens.All[index] : Screens.Primary;
        if (screen is not null)
        {
            var position = _settings.Position(screen.WorkingArea, screen.Scaling, VisibleSize);
            var inset = FrameInset(VisibleSize, _settings.Scale * screen.Scaling);
            Position = new PixelPoint(position.X - inset.X, position.Y - inset.Y);
        }
    }

    public void Shutdown()
    {
        if (_closing) return;
        _closing = true;
        Card.FinishMotion();
        UnregisterPowerEvents();
        Screens.Changed -= ScreensChanged;
        Win32Properties.RemoveWndProcHookCallback(this, WndProc);
        Win32Properties.RemoveWindowStylesCallback(this, WindowStyles);
        Close();
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern nint RegisterPowerSettingNotification(nint recipient, ref Guid setting, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterPowerSettingNotification(nint notification);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong(nint window, int index, int value);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowRgn(nint window, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint region);
}
