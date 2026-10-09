using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TractorTool.Contracts;
using TractorTool.UI;

namespace TractorTool.Plugins.SystemHud;

public partial class HudModel : ObservableObject
{
    private readonly PluginContext _context;
    private readonly HudSampler _sampler = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _preview = new() { Interval = TimeSpan.FromSeconds(6) };
    private HudSettings _settings = new();
    private HudWindow? _window;
    private Task _sampling = Task.CompletedTask;
    private Task? _stopTask;
    private CancellationTokenSource? _sampleCancellation;
    private bool _stopped, _ready, _pinned, _paused, _powerPreview, _pollWarning, _networkWarning;
    private int _revision;
    private byte? _ac;
    private bool _resumePreviewAfterInteraction;
    private HudReading _power = new("电量", "—", "正在读取电源状态…");
    private string _powerError = "";
    private readonly Dictionary<Guid, string> _samplingErrors = [];
    private IPluginTrayItem? _visibilityItem;

    public string[] Metrics { get; } = ["电量", "时钟", "CPU 使用率", "内存占用", "网络速度", "网络延迟（ICMP/TCP）", "HTTP 服务", "HTTP/JSON 指标", "硬件传感器（GPU / 温度等）", "DeepSeek 余额", "磁盘空间", "系统运行时间"];
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private bool _alwaysVisible;
    [ObservableProperty] private bool _powerReminder = true;
    [ObservableProperty] private bool _clickThrough;
    [ObservableProperty] private bool _animate = true;
    [ObservableProperty] private int _metric;
    [ObservableProperty] private double _scale = 1;
    [ObservableProperty] private string _status = "拖动桌面 HUD 可自动保存位置。";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStartupError))]
    private string _startupError = "";
    public bool HasStartupError => StartupError.Length != 0;
    public bool CanControl => _ready && !_stopped && _settings.Enabled;
    public bool MotionEnabled => _settings.Animate;
    public string PlacementHint => _settings.ClickThrough ? "点击穿透已开启；在插件设置中关闭后可点击或拖动桌面 HUD。" : "单击切换默认与长条；双击展开详情，再次双击回到默认。按住顶部拖动，松手保存位置。";
    public bool IsHudVisible
    {
        get => !_stopped && _window?.IsVisible == true;
        set { if (value != IsHudVisible) ToggleVisibility(); }
    }
    public bool IsPaused
    {
        get => _paused;
        set { if (value != IsPaused) TogglePause(); }
    }
    public string RuntimeState => HasStartupError ? "启动失败" : _stopped ? "已停止" : !_ready ? "正在启动"
        : !_settings.Enabled ? "已关闭 · 可在插件设置中启用" : _paused ? "已暂停"
        : _window?.IsVisible == true ? "正在显示" : "待命 · 等待电源变化或手动显示";

    public HudModel(PluginContext context)
    {
        _context = context;
        try { _settings = context.Settings.Load<HudSettings>("hud").Normalize(); }
        catch (Exception ex)
        {
            Status = "设置读取失败，原文件保留。使用默认值；点击保存才会写入新设置。";
            context.Log.Write(AppLogLevel.Error, context.PluginId, Status, ex);
        }
        LoadPreferences();
        LoadSecrets();
        InitializeProfiles();
        _timer.Tick += (_, _) => { _timer.Stop(); RefreshReading(); };
        _preview.Tick += (_, _) => { EndPreview(); UpdateVisibility(); };
    }

    private void LoadPreferences()
    {
        Enabled = _settings.Enabled; AlwaysVisible = _settings.AlwaysVisible;
        PowerReminder = _settings.PowerReminder; ClickThrough = _settings.ClickThrough;
        Animate = _settings.Animate; HardwareEnabled = _settings.HardwareEnabled; Scale = _settings.Scale;
    }

    public void Start()
    {
        if (_stopped || _window is not null) return;
        _window = new HudWindow { DataContext = this };
        _window.PowerChanged += ReadPower;
        _window.Dismissed += Hide;
        _window.DragStarted += UpdateVisibility;
        _window.DragCompleted += SavePosition;
        _window.StateChanged += UpdateVisibility;
        _window.PresentationFailed += PresentationFailed;
        try { _window.RegisterPowerEvents(); }
        catch (Exception ex) { ReportFailure("电源通知注册失败，自动提醒不可用；请在修复后重启插件。", ex); }
        ReadPower();
        _pinned = _settings.AlwaysVisible;
        ConfigureWindow();
        _ready = true;
        UpdateVisibility();
    }

    public void StartupFailed(Exception ex)
    {
        StartupError = "HUD 未能启动，请查看日志中的错误详情，修复后重启软件。 " + ex.Message;
        _ready = false;
        UpdateControls();
    }

    private void UpdateInteraction()
    {
        if (_window is { IsDragging: true } or { IsExpanded: true })
        {
            _resumePreviewAfterInteraction |= _preview.IsEnabled;
            _preview.Stop(); _rotation.Stop();
        }
        else
        {
            if (_resumePreviewAfterInteraction) _preview.Start();
            _resumePreviewAfterInteraction = false;
        }
    }

    private void SavePosition(HudSettings placement)
    {
        if (_stopped) return;
        // Merge only the position, never commit unsaved preference/profile edits.
        var settings = _settings with { ScreenName = placement.ScreenName, Monitor = placement.Monitor,
            PositionX = placement.PositionX, PositionY = placement.PositionY };
        if (settings != _settings && Save(settings)) Status = "HUD 位置已保存。";
        ConfigureWindow(); // Failed saves restore the last persisted position.
        UpdateVisibility();
    }

    public void RegisterTray(IPluginTrayMenu menu)
    {
        _visibilityItem = menu.Add("visible", new("显示 HUD", IsEnabled: false, IsChecked: false), _ => { ToggleVisibility(); return Task.CompletedTask; });
        UpdateControls();
    }

    [RelayCommand]
    private void ApplyPreferences()
    {
        if (Save(_settings with
        {
            Enabled = Enabled, AlwaysVisible = AlwaysVisible, PowerReminder = PowerReminder,
            ClickThrough = ClickThrough, Animate = Animate, HardwareEnabled = HardwareEnabled, Scale = Scale
        }))
        {
            LoadPreferences(); _pinned = _settings.AlwaysVisible; OnPropertyChanged(nameof(CanScanSensors)); ScanSensorsCommand.NotifyCanExecuteChanged();
            EndPreview(); ApplySavedSettings();
            OnPropertyChanged(nameof(PlacementHint));
        }
    }

    private bool Save(HudSettings settings)
    {
        settings = settings.Normalize();
        try { _context.Settings.Save("hud", settings); }
        catch (Exception ex) { ReportFailure("设置保存失败，尚未应用。请重试。", ex); return false; }
        // Position saves share this path; only applying preferences may invalidate sampling.
        _settings = settings; OnPropertyChanged(nameof(MotionEnabled));
        Status = "已保存。";
        return true;
    }

    private void ApplySavedSettings()
    {
        if (_ready && !_stopped) { ConfigureWindow(); ReadPower(); UpdateVisibility(); }
    }

    private void ConfigureWindow()
    {
        try { _window?.Configure(_settings); }
        catch (Exception ex) { ReportFailure("HUD 窗口设置未能完整应用。", ex); }
    }

    private void PresentationFailed(Exception ex) => ReportFailure("HUD 窗口轮廓更新失败，已隐藏；可重试显示。", ex);

    private void ToggleVisibility()
    {
        if (!CanControl) return;
        _pinned = _window?.IsVisible != true;
        EndPreview(); UpdateVisibility();
    }

    private void TogglePause()
    {
        if (!CanControl) return;
        _paused = !_paused; InvalidateSample(); ReadPower(); UpdateVisibility();
    }

    private void Hide()
    {
        _pinned = false; EndPreview(); UpdateVisibility();
    }

    private void ReadPower()
    {
        if (_stopped || _paused) return;
        try
        {
            var power = WindowsMetrics.Power();
            var changed = _ac is { } old && old != power.Ac && old != 255 && power.Ac != 255;
            _ac = power.Ac; _power = power.Reading with { ObservedAt = DateTimeOffset.Now, Unit = "%" }; _powerError = "";
            if (changed && _settings.Enabled && _settings.PowerReminder)
            {
                _powerPreview = true; _preview.Stop(); _preview.Start();
                _context.Log.Write(_context.PluginId, "电源状态变化：" + _power.Detail);
                InvalidateSample(); UpdateVisibility();
            }
            else if (_window?.IsVisible == true && EffectiveProfile.Metric == 0) SetReading(_power);
        }
        catch (Exception ex)
        {
            _power = new("电量", "—", HudSampler.FailureText(ex));
            if (_window?.IsVisible == true && EffectiveProfile.Metric == 0) SetReading(_power);
            if (_powerError != ex.Message) { _powerError = ex.Message; ReportFailure("电源状态读取失败。", ex); }
        }
    }

    private void UpdateVisibility()
    {
        if (_stopped || _window is null) return;
        UpdateInteraction();
        UpdateRotation(false, _window.Mode, _window.IsDragging);
        if (_window.IsDragging) return;
        var visible = _settings.Enabled && (_pinned || _preview.IsEnabled || _resumePreviewAfterInteraction);
        _timer.Stop();
        if (!visible) { _window.Hide(); _window.Collapse(); InvalidateSample(); UpdateControls(); return; }
        _window.Reveal();
        UpdateControls();
        if (!_paused)
        {
            RefreshReading();
            UpdateRotation(true, _window.Mode, _window.IsDragging);
        }
    }

    private void UpdateControls()
    {
        OnPropertyChanged(nameof(CanControl)); OnPropertyChanged(nameof(RuntimeState));
        OnPropertyChanged(nameof(IsHudVisible)); OnPropertyChanged(nameof(IsPaused));
        ScanSensorsCommand.NotifyCanExecuteChanged();
        NextProfileCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(RotationStatus));
        _visibilityItem?.Update(new("显示 HUD", IsEnabled: CanControl, IsChecked: IsHudVisible));
    }

    // Never mutate the counters while the worker is reading them. Reset at the next sample instead.
    private bool _resetCounters = true;
    private void InvalidateSample() { _revision++; _resetCounters = true; _sampleCancellation?.Cancel(); }

    private void RefreshReading()
    {
        if (_stopped || _paused || _window?.IsVisible != true) return;
        var profile = EffectiveProfile;
        var metric = profile.Metric;
        if (metric == 0) { SetReading(_power); return; }
        if (!_sampling.IsCompleted) return;
        _timer.Stop();
        if (profile.IsNetwork && !_networkWarning)
        {
            _networkWarning = true;
            _context.Log.Write(AppLogLevel.Warning, _context.PluginId,
                "开发优化：网络探测和 HTTP/JSON 采用方案指定间隔请求，来源未提供推送通知；仅显示当前方案时启用，隐藏/暂停/切换即取消。后续优先接入来源提供的事件通知。");
        }
        else if (!profile.IsNetwork && metric is not (1 or 11) && !_pollWarning)
        {
            _pollWarning = true;
            _context.Log.Write(AppLogLevel.Warning, _context.PluginId,
                "开发优化：性能、传感器和磁盘按方案设置的间隔采样（0.2–600 秒）；只采集当前指标，隐藏/暂停停止，不重叠读取。来源暂无数值变化通知，后续按性能证据评估优化。");
        }
        var reset = _resetCounters; _resetCounters = false;
        _sampleCancellation = new();
        _sampling = SampleAsync(profile, _revision, reset, _sampleCancellation);
    }

    private async Task SampleAsync(HudProfile profile, int revision, bool reset, CancellationTokenSource cancellation)
    {
        try
        {
            HudReading reading;
            string? error = null;
            try
            {
                var result = await _sampler.ReadAsync(profile, reset, _settings.HardwareEnabled, _deepSeekKey, cancellation.Token);
                if (result is null) return;
                (reading, error) = result.Value;
            }
            catch (Exception ex)
            {
                error = profile.IsNetwork ? "网络指标读取失败，请检查配置或重试。" : HudSampler.FailureText(ex);
                reading = new(Metrics[profile.Metric], "—", error);
            }
            if (_stopped || revision != _revision || _window?.IsVisible != true) return;
            SetReading(reading);
            _samplingErrors.TryGetValue(profile.Id, out var previous);
            if (error is not null)
            {
                if (previous != error)
                    _context.Log.Write(AppLogLevel.Warning, _context.PluginId, $"方案“{profile.Name}”读取失败：{error}");
                _samplingErrors[profile.Id] = error;
            }
            else if (reading.ObservedAt is not null && _samplingErrors.Remove(profile.Id))
                _context.Log.Write(_context.PluginId, $"方案“{profile.Name}”读取已恢复。");
        }
        finally
        {
            if (ReferenceEquals(_sampleCancellation, cancellation)) _sampleCancellation = null;
            cancellation.Dispose();
            // A new profile can become visible while the cancelled request is still completing.
            if (!_stopped && revision != _revision) Dispatcher.UIThread.Post(RefreshReading);
            else if (!_stopped && !_paused && _window?.IsVisible == true)
            {
                _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, _sampler.GetRefreshDelay(profile).TotalMilliseconds));
                _timer.Start();
            }
        }
    }

    private void ReportFailure(string message, Exception ex)
    {
        Status = message + " " + HudSampler.FailureText(ex);
        _context.Log.Write(AppLogLevel.Warning, _context.PluginId, Status, ex);
    }

    public Task Stop()
    {
        if (_stopTask is not null) return _stopTask;
        _stopped = true; _ready = false; InvalidateSample();
        UpdateControls();
        _timer.Stop(); _preview.Stop(); _rotation.Stop();
        if (_window is { } window)
        {
            window.PowerChanged -= ReadPower; window.Dismissed -= Hide;
            window.DragStarted -= UpdateVisibility; window.DragCompleted -= SavePosition;
            window.StateChanged -= UpdateVisibility;
            window.PresentationFailed -= PresentationFailed;
            window.Shutdown(); _window = null;
        }
        return _stopTask = FinishStopAsync();
    }

    private async Task FinishStopAsync()
    {
        try { await Task.WhenAll(_sampling, _sensorScan); }
        finally
        {
            try { await Task.Run(_sampler.Dispose); }
            finally { _deepSeekKey = ""; KeyInput = ""; }
        }
    }
}
