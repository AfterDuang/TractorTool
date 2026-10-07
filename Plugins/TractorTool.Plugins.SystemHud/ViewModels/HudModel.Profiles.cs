using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TractorTool.Contracts;

namespace TractorTool.Plugins.SystemHud;

public partial class HudModel
{
    private HudProfiles _profiles = new();
    private HudProfile _activeProfile = new();
    private HudProfile? _draftPreview;
    private static readonly HudProfile PowerProfile = new();
    private readonly DispatcherTimer _rotation = new();
    private bool _loadingEditor;
    public ObservableCollection<HudProfile> Profiles { get; } = [];
    public string[] Comparisons { get; } = ["低于或等于", "高于或等于"];
    [ObservableProperty] private HudProfile? _selectedProfile;
    [ObservableProperty] private string _profileName = "";
    [ObservableProperty] private string _titleTemplate = "{label}";
    [ObservableProperty] private string _valueTemplate = "{value}";
    [ObservableProperty] private string _detailTemplate = "{detail}";
    [ObservableProperty] private string _accentColor = "#FFE66B";
    [ObservableProperty] private bool _ruleEnabled;
    [ObservableProperty] private int _comparison;
    [ObservableProperty] private double _threshold = 20;
    [ObservableProperty] private string _ruleColor = "#FF8A90";
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _jsonPointer = "";
    [ObservableProperty] private int _refreshSeconds = 30;
    [ObservableProperty] private bool _jsonPercentage;
    [ObservableProperty] private int _probeProtocol;
    [ObservableProperty] private int _port = 443;
    [ObservableProperty] private string _sensorId = "";
    [ObservableProperty] private string _currency = "CNY";
    [ObservableProperty] private string _drive = "C:\\";
    [ObservableProperty] private bool _rotateProfiles;
    [ObservableProperty] private int _rotationSeconds = 10;
    [ObservableProperty] private string _profileError = "";
    [ObservableProperty] private bool _hasDraftChanges;
    [ObservableProperty] private HudDisplay _editorDisplay = PowerProfile.Render(new("电量", "72%", "示例：正在使用电池", 72));
    [ObservableProperty] private HudDisplay _display = PowerProfile.Render(new("桌面状态", "—", "等待显示"));
    private HudDisplay _activeDisplay = PowerProfile.Render(new("桌面状态", "—", "等待显示"));
    public string ActiveProfileName => _activeProfile.Name;
    public bool CanSwitchProfile => !HasDraftChanges;
    public bool CanAddProfile => CanSwitchProfile && Profiles.Count < 32;
    public bool CanDeleteProfile => CanSwitchProfile && Profiles.Count > 1;
    public bool CanSaveProfile => SelectedProfile is not null && ProfileError.Length == 0;
    public bool CanPreviewProfile => CanSaveProfile && CanControl && !_paused;
    public bool IsNetworkMetric => Metric is 5 or 6 or 7 or 9;
    public bool HasRequestAddress => Metric is >= 5 and <= 7;
    public bool IsProbeMetric => Metric == 5;
    public bool IsTcpProbe => Metric == 5 && ProbeProtocol == 1;
    public bool IsHardwareMetric => Metric == 8;
    public bool IsDeepSeekMetric => Metric == 9;
    public bool IsDiskMetric => Metric == 10;
    public bool IsJsonMetric => Metric == 7;
    public string AddressHint => Metric == 5 ? "主机名或 IP，例如 192.168.1.1" : "HTTP/HTTPS 地址，例如 http://localhost:8080/status";
    private HudProfile EffectiveProfile => _powerPreview ? PowerProfile : _draftPreview ?? _activeProfile;
    private HudProfile Draft => new()
    {
        Id = SelectedProfile?.Id ?? Guid.Empty, Name = (ProfileName ?? "").Trim(), Metric = Metric,
        Title = TitleTemplate, Value = ValueTemplate, Detail = DetailTemplate, Color = AccentColor, Layout = DraftLayout,
        RuleEnabled = RuleEnabled, Comparison = Comparison, Threshold = Threshold, RuleColor = RuleColor,
        Address = (Address ?? "").Trim(), JsonPointer = JsonPointer, RefreshSeconds = RefreshSeconds, JsonPercentage = JsonPercentage,
        ProbeProtocol = ProbeProtocol, Port = Port, SensorId = SensorId, Currency = Currency, Drive = Drive
    };

    private void InitializeProfiles()
    {
        InitializeLayoutEditors();
        try { _profiles = _context.Settings.Load<HudProfiles>("profiles").Normalize(_settings.Metric); }
        catch (Exception ex)
        {
            Status = "方案读取失败，使用默认方案；原文件保留，点击保存才会写入新方案。";
            _context.Log.Write(AppLogLevel.Error, _context.PluginId, Status, ex);
            _profiles = new HudProfiles().Normalize(_settings.Metric);
        }
        ActivateProfile(_profiles.Active);
        foreach (var profile in _profiles.Profiles) Profiles.Add(profile);
        SelectedProfile = _activeProfile;
        _rotation.Tick += (_, _) => AdvanceProfile();
    }

    partial void OnSelectedProfileChanged(HudProfile? value) { if (value is not null) LoadProfile(value); }
    partial void OnMetricChanged(int value)
    {
        if (!_loadingEditor && value == 9 && RefreshSeconds < 60) RefreshSeconds = 60;
    }
    private void LoadProfile(HudProfile profile)
    {
        _loadingEditor = true;
        ProfileName = profile.Name; Metric = profile.Metric;
        TitleTemplate = profile.Title; ValueTemplate = profile.Value; DetailTemplate = profile.Detail;
        LoadLayout(profile.Layout);
        AccentColor = profile.Color; RuleEnabled = profile.RuleEnabled; Comparison = profile.Comparison;
        Threshold = profile.Threshold; RuleColor = profile.RuleColor;
        Address = profile.Address; JsonPointer = profile.JsonPointer; RefreshSeconds = profile.RefreshSeconds; JsonPercentage = profile.JsonPercentage;
        ProbeProtocol = profile.ProbeProtocol; Port = profile.Port; SensorId = profile.SensorId; Currency = profile.Currency; Drive = profile.Drive;
        SelectSavedSensor();
        RotateProfiles = _profiles.Rotate; RotationSeconds = _profiles.Seconds;
        _loadingEditor = false;
        UpdateEditor();
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_loadingEditor && e.PropertyName is nameof(ProfileName) or nameof(Metric) or nameof(TitleTemplate)
            or nameof(ValueTemplate) or nameof(DetailTemplate) or nameof(AccentColor) or nameof(RuleEnabled)
            or nameof(Comparison) or nameof(Threshold) or nameof(RuleColor) or nameof(RotateProfiles) or nameof(RotationSeconds)
            or nameof(Address) or nameof(JsonPointer) or nameof(RefreshSeconds) or nameof(JsonPercentage)
            or nameof(ProbeProtocol) or nameof(Port) or nameof(SensorId) or nameof(Currency) or nameof(Drive)) UpdateEditor();
    }

    private void UpdateEditor()
    {
        var draft = Draft;
        ProfileError = draft.Error.Length != 0 ? draft.Error : RotationSeconds is < 3 or > 300 ? "轮播间隔为 3–300 秒。" : "";
        HasDraftChanges = draft != SelectedProfile || RotateProfiles != _profiles.Rotate || RotationSeconds != _profiles.Seconds;
        if (ProfileError.Length == 0) EditorDisplay = draft.Render(Metric switch
        {
            0 => new("电量", "72%", "示例：正在使用电池", 72),
            1 => new("时钟", "12:34:56", "示例：2026/10/07 星期三"),
            2 => new("CPU", "36%", "示例：处理器使用率", 36),
            3 => new("内存", "64%", "示例：10.2 / 16.0 GiB", 64),
            4 => new("网速", "↓ 1.2 MiB/s", "示例：↑ 128 KiB/s · 活动接口合计"),
            5 => new("网络延迟", "24 ms", "示例：ICMP 往返"),
            6 => new("HTTP 服务", "85 ms", "示例：HTTP 200 · 响应头耗时"),
            7 => new("JSON 指标", JsonPercentage ? "72" : "就绪", "示例：选定字段的值", JsonPercentage ? 72 : null),
            8 => new("硬件传感器", "48 °C", "示例：CPU 温度", Unit: "°C"),
            9 => new("DeepSeek 余额", "10.00 " + Currency, "示例：账户余额", Unit: Currency),
            10 => new("磁盘空间", "256.0 GiB", "示例：共 512.0 GiB", 50, "GiB"),
            _ => new("系统运行时间", "1 天 02:30:00", "示例：系统会话运行时长")
        });
        OnPropertyChanged(nameof(IsNetworkMetric)); OnPropertyChanged(nameof(IsJsonMetric)); OnPropertyChanged(nameof(AddressHint));
        OnPropertyChanged(nameof(HasRequestAddress)); OnPropertyChanged(nameof(IsProbeMetric)); OnPropertyChanged(nameof(IsTcpProbe));
        OnPropertyChanged(nameof(IsHardwareMetric)); OnPropertyChanged(nameof(IsDeepSeekMetric)); OnPropertyChanged(nameof(IsDiskMetric));
        OnPropertyChanged(nameof(CanSwitchProfile));
        AddProfileCommand.NotifyCanExecuteChanged(); CopyProfileCommand.NotifyCanExecuteChanged(); DeleteProfileCommand.NotifyCanExecuteChanged();
        SaveProfileCommand.NotifyCanExecuteChanged(); PreviewProfileCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanAddProfile))]
    private void AddProfile() => CommitProfiles([.. _profiles.Profiles, new HudProfile { Name = $"方案 {Profiles.Count + 1}" }]);
    [RelayCommand(CanExecute = nameof(CanAddProfile))]
    private void CopyProfile()
    {
        if (SelectedProfile is not { } profile) return;
        var name = profile.Name.Length > 35 ? profile.Name[..35] : profile.Name;
        CommitProfiles([.. _profiles.Profiles, profile with { Id = Guid.NewGuid(), Name = name + " 副本" }]);
    }
    // Add/copy commits first, so a failed save cannot leave a phantom profile in the list.
    private void CommitProfiles(HudProfile[] profiles) => SaveProfiles(_profiles with { Profiles = profiles, ActiveId = profiles[^1].Id });
    [RelayCommand(CanExecute = nameof(CanDeleteProfile))]
    private void DeleteProfile()
    {
        if (SelectedProfile is not { } selected) return;
        var remaining = _profiles.Profiles.Where(p => p.Id != selected.Id).ToArray();
        SaveProfiles(_profiles with { Profiles = remaining, ActiveId = remaining[0].Id });
    }
    [RelayCommand]
    private void RevertProfile() { if (SelectedProfile is { } profile) LoadProfile(profile); }
    [RelayCommand(CanExecute = nameof(CanSaveProfile))]
    private void SaveProfile()
    {
        if (!CanSaveProfile) return;
        var draft = Draft;
        SaveProfiles(_profiles with { Profiles = _profiles.Profiles.Select(p => p.Id == draft.Id ? draft : p).ToArray(),
            ActiveId = draft.Id, Rotate = RotateProfiles, Seconds = RotationSeconds });
    }

    private void SaveProfiles(HudProfiles settings)
    {
        try { _context.Settings.Save("profiles", settings); }
        catch (Exception ex) { ReportFailure("方案保存失败，尚未应用。", ex); return; }
        _profiles = settings;
        _sampler.ResetNetworkCache();
        foreach (var id in _samplingErrors.Keys.Where(id => !settings.Profiles.Any(p => p.Id == id)).ToArray()) _samplingErrors.Remove(id);
        // Retain unchanged list entries; replace only edited entries to preserve selection/focus.
        for (var i = Profiles.Count - 1; i >= 0; i--) if (!settings.Profiles.Any(p => p.Id == Profiles[i].Id)) Profiles.RemoveAt(i);
        for (var i = 0; i < settings.Profiles.Length; i++)
            if (i == Profiles.Count) Profiles.Add(settings.Profiles[i]);
            else if (Profiles[i] != settings.Profiles[i]) Profiles[i] = settings.Profiles[i];
        SelectedProfile = settings.Active; LoadProfile(settings.Active);
        EndPreview(); ActivateProfile(settings.Active); UpdateVisibility();
        Status = "方案已保存并使用。轮播顺序与方案列表一致。";
    }

    [RelayCommand(CanExecute = nameof(CanPreviewProfile))]
    private void PreviewProfile()
    {
        if (!CanPreviewProfile) return;
        _draftPreview = Draft; _powerPreview = false; InvalidateSample();
        _preview.Stop(); _preview.Start(); UpdateVisibility();
    }
    private void AdvanceProfile(bool preview = false)
    {
        if (preview) { EndPreview(); _preview.Start(); }
        ActivateProfile(_profiles.Next(_activeProfile.Id)); UpdateVisibility();
    }
    private void ActivateProfile(HudProfile profile)
    {
        _activeProfile = profile;
        _activeDisplay = profile.Render(new(Metrics[profile.Metric], "—", "等待新读数"));
        Display = _activeDisplay;
        InvalidateSample(); OnPropertyChanged(nameof(ActiveProfileName));
    }
    private void SetReading(HudReading reading)
    {
        Display = EffectiveProfile.Render(reading);
        if (!_powerPreview && _draftPreview is null) _activeDisplay = Display;
    }
    private void EndPreview()
    {
        _preview.Stop(); _resumePreviewAfterInteraction = false; _powerPreview = false; _draftPreview = null;
        Display = _activeDisplay; InvalidateSample();
    }
}
