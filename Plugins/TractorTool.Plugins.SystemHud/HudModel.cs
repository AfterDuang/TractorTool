using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TractorTool.Contracts;
using TractorTool.UI;

namespace TractorTool.Plugins.SystemHud;

public partial class HudModel : ObservableObject
{
    private readonly PluginContext _context;
    private readonly WindowsMetrics _metrics = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _preview = new() { Interval = TimeSpan.FromSeconds(6) };
    private HudSettings _settings = new();
    private HudWindow? _window;
    private Task _sampling = Task.CompletedTask;
    private bool _stopped, _pinned, _paused, _powerPreview, _pollWarning;
    private int _revision;
    private byte? _ac;
    private HudReading _power = new("电量", "—", "正在读取电源状态…");
    private string _samplingError = "", _powerError = "";
    private IPluginTrayItem? _visibilityItem, _pauseItem;

    public string[] Metrics { get; } = ["电量", "时钟", "CPU 使用率", "内存占用", "网络速度"];
    public string[] Anchors { get; } = ["左上", "上方居中", "右上", "左侧居中", "屏幕居中", "右侧居中", "左下", "下方居中", "右下"];
    public ObservableCollection<string> Monitors { get; } = ["主显示器"];
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private bool _alwaysVisible;
    [ObservableProperty] private bool _powerReminder = true;
    [ObservableProperty] private bool _clickThrough = true;
    [ObservableProperty] private bool _animate = true;
    [ObservableProperty] private int _metric;
    [ObservableProperty] private int _anchor = 2;
    [ObservableProperty] private int _monitor;
    [ObservableProperty] private double _scale = 1;
    [ObservableProperty] private double _offsetX;
    [ObservableProperty] private double _offsetY;
    [ObservableProperty] private string _status = "设置后点击应用；预览持续 6 秒。";
    [ObservableProperty] private HudReading _reading = new("桌面状态", "—", "等待显示");

    public HudModel(PluginContext context)
    {
        _context = context;
        try { _settings = context.Settings.Load<HudSettings>("hud").Normalize(); }
        catch (Exception ex)
        {
            Status = "设置读取失败，原文件保留。使用默认值；点击应用才会保存新设置。";
            context.Log.Write(AppLogLevel.Error, context.PluginId, Status, ex);
        }
        LoadFields();
        _timer.Tick += (_, _) => RefreshReading();
        _preview.Tick += (_, _) => { _preview.Stop(); _powerPreview = false; UpdateVisibility(); };
    }

    private void LoadFields()
    {
        Enabled = _settings.Enabled; AlwaysVisible = _settings.AlwaysVisible;
        PowerReminder = _settings.PowerReminder; ClickThrough = _settings.ClickThrough;
        Animate = _settings.Animate; Metric = _settings.Metric; Anchor = _settings.Anchor;
        Monitor = _settings.Monitor; Scale = _settings.Scale; OffsetX = _settings.OffsetX; OffsetY = _settings.OffsetY;
    }

    public void Start()
    {
        if (_stopped || _window is not null) return;
        _window = new HudWindow { DataContext = this };
        _window.PowerChanged += ReadPower;
        _window.Dismissed += Hide;
        _window.MonitorsChanged += UpdateMonitors;
        UpdateMonitors();
        try { _window.RegisterPowerEvents(); }
        catch (Exception ex) { ReportFailure("电源通知注册失败，自动提醒不可用；请在修复后重启插件。", ex); }
        ReadPower();
        _pinned = _settings.AlwaysVisible;
        ConfigureWindow();
        UpdateVisibility();
    }

    private void UpdateMonitors()
    {
        if (_window is null) return;
        var selected = Monitor;
        Monitors.Clear(); Monitors.Add("主显示器");
        for (var i = 0; i < _window.Screens.All.Count; i++)
            Monitors.Add($"{i + 1} · {_window.Screens.All[i].DisplayName ?? "显示器"}");
        Monitor = selected < Monitors.Count ? selected : 0;
    }

    public void RegisterTray(IPluginTrayMenu menu)
    {
        _visibilityItem = menu.Add("visible", new("常驻显示 HUD", IsChecked: false), _ =>
        {
            if (!_stopped) { _pinned = !_pinned; _preview.Stop(); _powerPreview = false; UpdateVisibility(); }
            return Task.CompletedTask;
        });
        _pauseItem = menu.Add("pause", new("暂停更新", IsChecked: false), _ =>
        {
            if (!_stopped) { _paused = !_paused; InvalidateSample(); ReadPower(); UpdateVisibility(); }
            return Task.CompletedTask;
        });
        menu.Add("preview", new("预览 6 秒"), _ => { Preview(); return Task.CompletedTask; });
        menu.Add("next", new("切换下一项指标"), _ =>
        {
            if (!_stopped)
            {
                _settings = _settings with { Metric = (_settings.Metric + 1) % Metrics.Length };
                Metric = _settings.Metric; InvalidateSample(); Preview();
            }
            return Task.CompletedTask;
        });
    }

    [RelayCommand]
    private void Apply()
    {
        if (_stopped) return;
        var settings = new HudSettings
        {
            Enabled = Enabled, AlwaysVisible = AlwaysVisible, PowerReminder = PowerReminder,
            ClickThrough = ClickThrough, Animate = Animate, Metric = Metric, Anchor = Anchor,
            Monitor = Monitor, Scale = Scale, OffsetX = OffsetX, OffsetY = OffsetY
        }.Normalize();
        try { _context.Settings.Save("hud", settings); }
        catch (Exception ex) { ReportFailure("设置保存失败，尚未应用。请重试。", ex); return; }
        _settings = settings; LoadFields(); _pinned = settings.AlwaysVisible;
        _preview.Stop(); _powerPreview = false; InvalidateSample();
        Status = "设置已保存。托盘可以临时显示、暂停或切换指标。";
        ConfigureWindow(); ReadPower(); UpdateVisibility();
    }

    private void ConfigureWindow()
    {
        try { _window?.Configure(_settings); }
        catch (Exception ex) { ReportFailure("HUD 窗口设置未能完整应用。", ex); }
    }

    [RelayCommand]
    private void Preview()
    {
        if (_stopped || _window is null) return;
        if (!_settings.Enabled) { Status = "请先启用 HUD 并应用设置。"; return; }
        _powerPreview = false; _preview.Stop(); _preview.Start();
        UpdateVisibility();
    }

    private void Hide()
    {
        _pinned = false; _preview.Stop(); _powerPreview = false; UpdateVisibility();
    }

    private void ReadPower()
    {
        if (_stopped || _paused) return;
        try
        {
            var power = WindowsMetrics.Power();
            var changed = _ac is { } old && old != power.Ac && old != 255 && power.Ac != 255;
            _ac = power.Ac; _power = power.Reading; _powerError = "";
            if (changed && _settings.Enabled && _settings.PowerReminder)
            {
                _powerPreview = true; _preview.Stop(); _preview.Start();
                _context.Log.Write(_context.PluginId, "电源状态变化：" + _power.Detail);
                InvalidateSample(); UpdateVisibility();
            }
            else if (_window?.IsVisible == true && (_powerPreview || _settings.Metric == 0)) Reading = _power;
        }
        catch (Exception ex)
        {
            _power = new("电量", "—", FailureText(ex));
            if (_window?.IsVisible == true && (_powerPreview || _settings.Metric == 0)) Reading = _power;
            if (_powerError != ex.Message) { _powerError = ex.Message; ReportFailure("电源状态读取失败。", ex); }
        }
    }

    private void UpdateVisibility()
    {
        if (_stopped || _window is null) return;
        var visible = _settings.Enabled && (_pinned || _preview.IsEnabled);
        _visibilityItem?.Update(new("常驻显示 HUD", IsEnabled: _settings.Enabled, IsChecked: _pinned));
        _pauseItem?.Update(new("暂停更新", IsEnabled: _settings.Enabled, IsChecked: _paused));
        _timer.Stop();
        if (!visible) { _window.Hide(); InvalidateSample(); return; }
        _window.Reveal();
        if (!_paused)
        {
            RefreshReading();
            if (!_powerPreview && _settings.Metric != 0) _timer.Start();
        }
    }

    // Never mutate the counters while the worker is reading them. Reset at the next sample instead.
    private bool _resetCounters = true;
    private void InvalidateSample() { _revision++; _resetCounters = true; }

    private void RefreshReading()
    {
        if (_stopped || _paused || _window?.IsVisible != true) return;
        var metric = _powerPreview ? 0 : _settings.Metric;
        if (metric == 0) { Reading = _power; return; }
        if (metric == 1)
        {
            var now = DateTimeOffset.Now;
            Reading = new("时钟", now.ToString("HH:mm:ss"), now.ToString("yyyy/MM/dd dddd")); return;
        }
        if (!_sampling.IsCompleted) return;
        if (!_pollWarning)
        {
            _pollWarning = true;
            _context.Log.Write(AppLogLevel.Warning, _context.PluginId,
                "开发优化：CPU/内存/网速没有可直接替代连续数值采样的变化通知，暂用 1 秒采样；仅显示当前指标，隐藏/暂停即停止。后续按性能证据评估优化，勿提高轮询频率。");
        }
        var reset = _resetCounters; _resetCounters = false;
        _sampling = SampleAsync(metric, _revision, reset);
    }

    private async Task SampleAsync(int metric, int revision, bool reset)
    {
        HudReading reading;
        Exception? error = null;
        try { reading = await Task.Run(() => { if (reset) _metrics.Reset(); return _metrics.Read(metric); }); }
        catch (Exception ex) { error = ex; reading = new(Metrics[metric], "—", FailureText(ex)); }
        if (_stopped || revision != _revision || _window?.IsVisible != true) return;
        Reading = reading;
        if (error is not null && _samplingError != error.Message) ReportFailure("性能采样失败。", error);
        else if (error is null && _samplingError.Length != 0) _context.Log.Write(_context.PluginId, "性能采样已恢复。");
        _samplingError = error?.Message ?? "";
    }

    private static string FailureText(Exception ex) => ex is UnauthorizedAccessException or Win32Exception { NativeErrorCode: 5 }
        ? "需要管理员权限，请退出后以管理员身份运行 TractorTool。" : ex.Message;
    private void ReportFailure(string message, Exception ex)
    {
        Status = message + " " + FailureText(ex);
        _context.Log.Write(AppLogLevel.Warning, _context.PluginId, Status, ex);
    }

    public Task Stop()
    {
        if (_stopped) return _sampling;
        _stopped = true; _revision++;
        _timer.Stop(); _preview.Stop();
        if (_window is { } window)
        {
            window.PowerChanged -= ReadPower; window.Dismissed -= Hide; window.MonitorsChanged -= UpdateMonitors;
            window.Shutdown(); _window = null;
        }
        return _sampling;
    }
}
