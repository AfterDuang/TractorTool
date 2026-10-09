using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TractorTool.Plugins.SystemHud;

public partial class HudModel
{
    private string _deepSeekKey = "";
    private Task _sensorScan = Task.CompletedTask;
    public ObservableCollection<SensorChoice> Sensors { get; } = [];
    [ObservableProperty] private SensorChoice? _selectedSensor;
    [ObservableProperty] private bool _hardwareEnabled;
    [ObservableProperty] private string _sensorStatus = "先在插件设置启用硬件传感器，再检测并选择需要显示的一项。";
    [ObservableProperty] private string _keyInput = "";
    [ObservableProperty] private string _keyStatus = "尚未配置 API Key。";
    public bool CanScanSensors => !_stopped && _settings.HardwareEnabled;
    public string[] ProbeProtocols { get; } = ["ICMP Ping", "TCP 连接"];
    public string[] Currencies { get; } = ["CNY", "USD"];

    private void LoadSecrets()
    {
        try
        {
            var secret = _context.Settings.Load<HudSecrets>("credentials");
            if (secret.DeepSeekKey.Length == 0) return;
            _deepSeekKey = HudSecrets.Unprotect(secret.DeepSeekKey);
            if (!HudSecrets.ValidKey(_deepSeekKey)) { _deepSeekKey = ""; throw new InvalidDataException(); }
            KeyStatus = "已保存 API Key（当前 Windows 用户加密）。";
        }
        catch { KeyStatus = "无法读取原密钥，请重新输入并保存。原凭据文件保留。"; }
    }

    [RelayCommand]
    private void SaveKey()
    {
        var key = KeyInput.Trim();
        if (!HudSecrets.ValidKey(key)) { KeyStatus = "请输入有效 API Key，不包含空白或控制字符，最多 1024 字符。"; return; }
        try { _context.Settings.Save("credentials", new HudSecrets { DeepSeekKey = HudSecrets.Protect(key) }); }
        catch { KeyStatus = "密钥保存失败，原密钥未改变，请重试。"; return; }
        _deepSeekKey = key; KeyInput = ""; KeyStatus = "已保存 API Key（当前 Windows 用户加密）。";
        _sampler.ResetCache(); InvalidateSample(); UpdateVisibility();
    }

    [RelayCommand]
    private void ClearKey()
    {
        try { _context.Settings.Save("credentials", new HudSecrets()); }
        catch { KeyStatus = "密钥清除失败，请重试。"; return; }
        _deepSeekKey = ""; KeyInput = ""; KeyStatus = "已清除 API Key。";
        _sampler.ResetCache(); InvalidateSample(); UpdateVisibility();
    }

    partial void OnSelectedSensorChanged(SensorChoice? value)
    {
        if (value is not null) SensorId = value.Id;
    }

    private void SelectSavedSensor()
    {
        if (SensorId.Length != 0 && !Sensors.Any(s => s.Id == SensorId)) Sensors.Add(new(SensorId, "已保存的传感器 · " + SensorId));
        SelectedSensor = Sensors.FirstOrDefault(s => s.Id == SensorId);
    }

    [RelayCommand(CanExecute = nameof(CanScanSensors))]
    private Task ScanSensors() => _sensorScan = ScanSensorsCoreAsync();

    private async Task ScanSensorsCoreAsync()
    {
        if (!CanScanSensors) return;
        SensorStatus = "正在检测 CPU / GPU 传感器…";
        try
        {
            var choices = await _sampler.ScanSensorsAsync();
            if (_stopped) return;
            var selected = SensorId;
            Sensors.Clear();
            foreach (var choice in choices) Sensors.Add(choice);
            SensorId = selected; SelectSavedSensor();
            SensorStatus = choices.Length == 0 ? "没有可用传感器。请以管理员身份运行，并检查设备驱动及 PawnIO。"
                : $"找到 {choices.Length} 项。温度等项目可能需要管理员权限和 PawnIO；未读到值会明确提示。";
        }
        catch (Exception ex) { if (!_stopped) SensorStatus = "检测失败：" + HudSampler.FailureText(ex); }
    }
}
