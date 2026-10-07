using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace TractorTool.Plugins.SystemHud;

public partial class HudWindow : Window
{
    private readonly List<nint> _notifications = [];
    private HudSettings _settings = new();
    private bool _closing;
    private PointerPressedEventArgs? _press;
    private Point _pressPosition;
    public event Action? PowerChanged;
    public event Action? Dismissed;
    public event Action? DragStarted;
    public event Action<HudSettings>? DragCompleted;
    public event Action? ExpansionChanged;
    public bool IsDragging { get; private set; }
    public bool IsExpanded => Card.IsExpanded;

    public HudWindow()
    {
        InitializeComponent();
        Win32Properties.AddWindowStylesCallback(this, WindowStyles);
        Win32Properties.AddWndProcHookCallback(this, WndProc);
        Screens.Changed += ScreensChanged;
        Closing += (_, e) => { if (!_closing) { e.Cancel = true; Dismissed?.Invoke(); } };
        Card.ExpansionChanged += () =>
        {
            Height = (IsExpanded ? HudSettings.ExpandedHeight : HudSettings.CardHeight) * _settings.Scale;
            Reposition();
            if (IsVisible) ExpansionChanged?.Invoke();
        };
        // Only the summary owns the gesture; detail controls keep normal pointer/keyboard input.
        Card.DragSurface.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (_closing || IsDragging || _settings.ClickThrough || !e.Pointer.IsPrimary || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            _press = e;
            _pressPosition = e.GetPosition(this);
        }, RoutingStrategies.Tunnel);
        Card.DragSurface.AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (_press is not { } press || press.Pointer != e.Pointer || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
                || !ExceedsDragThreshold(e.GetPosition(this) - _pressPosition)) return;
            _press = null;
            IsDragging = true;
            DragStarted?.Invoke();
            e.Handled = true;
            // Releasing Button capture cancels its click before entering the native move loop.
            e.Pointer.Capture(null);
            BeginMoveDrag(press);
        }, RoutingStrategies.Tunnel);
        Card.DragSurface.AddHandler(PointerReleasedEvent, (_, _) => _press = null, RoutingStrategies.Tunnel);
        Card.DragSurface.PointerCaptureLost += (_, _) => _press = null;
    }

    internal static bool ExceedsDragThreshold(Vector movement) => Math.Abs(movement.X) >= 6 || Math.Abs(movement.Y) >= 6;
    public void Collapse() => Card.IsExpanded = false;

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
        if (_settings.ClickThrough && message == 0x0084) { handled = true; return -1; }
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
        Width = HudSettings.CardWidth * settings.Scale;
        Height = (IsExpanded ? HudSettings.ExpandedHeight : HudSettings.CardHeight) * settings.Scale;
        Card.Transitions = settings.Animate ? new Transitions
        {
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(220) }
        } : null;
        Reposition();
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
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) { DragCompleted?.Invoke(_settings); return; }
        var monitor = Screens.All.ToList().IndexOf(screen) + 1;
        DragCompleted?.Invoke(_settings.AtPosition(Position, screen.WorkingArea, screen.Scaling, screen.DisplayName, monitor, IsExpanded));
    }

    public void Reveal()
    {
        if (IsVisible) return;
        Card.Opacity = _settings.Animate ? 0 : 1;
        Show();
        Reposition();
        Dispatcher.UIThread.Post(() => { if (!_closing && IsVisible) Card.Opacity = 1; }, DispatcherPriority.Render);
    }

    private void ScreensChanged(object? sender, EventArgs e) => Reposition();
    private void Reposition()
    {
        if (IsDragging) return;
        var index = _settings.Monitor - 1;
        var screen = _settings.HasPosition && _settings.ScreenName is { } name
            ? Screens.All.FirstOrDefault(s => s.DisplayName == name) ?? Screens.Primary
            : index >= 0 && index < Screens.All.Count ? Screens.All[index] : Screens.Primary;
        if (screen is not null) Position = _settings.Position(screen.WorkingArea, screen.Scaling, IsExpanded);
    }

    public void Shutdown()
    {
        if (_closing) return;
        _closing = true;
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
}
