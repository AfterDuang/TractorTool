using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace TractorTool.Plugins.SystemHud;

public partial class HudWindow : Window
{
    private readonly List<nint> _notifications = [];
    private HudSettings _settings = new();
    private bool _closing;
    public event Action? PowerChanged;
    public event Action? Dismissed;
    public event Action? MonitorsChanged;

    public HudWindow()
    {
        InitializeComponent();
        Win32Properties.AddWindowStylesCallback(this, WindowStyles);
        Win32Properties.AddWndProcHookCallback(this, WndProc);
        Screens.Changed += ScreensChanged;
        Closing += (_, e) => { if (!_closing) { e.Cancel = true; Dismissed?.Invoke(); } };
        PointerPressed += (_, e) => { if (!_settings.ClickThrough && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) Dismissed?.Invoke(); };
    }

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
        return 0;
    }

    private (uint, uint) WindowStyles(uint style, uint exStyle)
        => (style, _settings.ClickThrough ? exStyle | 0x080800A0u : (exStyle | 0x08080080u) & ~0x20u);

    public void Configure(HudSettings settings)
    {
        _settings = settings;
        Width = 320 * settings.Scale; Height = 124 * settings.Scale;
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

    public void Reveal()
    {
        if (IsVisible) return;
        Card.Opacity = _settings.Animate ? 0 : 1;
        Show();
        Reposition();
        Dispatcher.UIThread.Post(() => { if (!_closing && IsVisible) Card.Opacity = 1; }, DispatcherPriority.Render);
    }

    private void ScreensChanged(object? sender, EventArgs e) { Reposition(); MonitorsChanged?.Invoke(); }
    private void Reposition()
    {
        var index = _settings.Monitor - 1;
        var screen = index >= 0 && index < Screens.All.Count ? Screens.All[index] : Screens.Primary;
        if (screen is not null) Position = _settings.Position(screen.WorkingArea, screen.Scaling);
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
