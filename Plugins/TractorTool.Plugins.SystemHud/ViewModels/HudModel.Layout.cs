using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TractorTool.Plugins.SystemHud;

public partial class HudModel
{
    private readonly HudSlotEditor[] _layoutSlots =
    [
        new("左侧"), new("中间"), new("右侧"),
        new("区域 1 · 左侧"), new("区域 2 · 主信息"), new("区域 3 · 次信息"), new("区域 4 · 右侧"),
        new("下方详情", true)
    ];
    public string[] LayoutSections { get; } = ["默认 · 三个区域", "长条 · 四个区域", "详细 · 四个区域 + 详情"];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleLayoutSlots), nameof(LayoutHint), nameof(LayoutPreviewMode))]
    private int _layoutSection;
    public HudMode LayoutPreviewMode
    {
        get => (HudMode)Math.Clamp(LayoutSection, 0, 2);
        set => LayoutSection = Math.Clamp((int)value, 0, 2);
    }
    public IEnumerable<HudSlotEditor> VisibleLayoutSlots => LayoutSection switch
    {
        0 => _layoutSlots.Take(3), 1 => _layoutSlots.Skip(3).Take(4), _ => _layoutSlots.Skip(7)
    };
    public string LayoutHint => LayoutSection == 2
        ? "上方沿用长条的四项内容，重新分布为左侧、中央和右侧上下两格；下方显示详情。"
        : "每格独立选择内容。自定义文字可使用下方列出的变量；环形进度没有百分比时显示图标。";

    private HudLayout DraftLayout => new()
    {
        CompactLeft = _layoutSlots[0].Spec, CompactCenter = _layoutSlots[1].Spec, CompactRight = _layoutSlots[2].Spec,
        StripLeft = _layoutSlots[3].Spec, StripPrimary = _layoutSlots[4].Spec,
        StripSecondary = _layoutSlots[5].Spec, StripRight = _layoutSlots[6].Spec, Detail = _layoutSlots[7].Spec
    };

    private void InitializeLayoutEditors()
    {
        foreach (var slot in _layoutSlots) slot.PropertyChanged += LayoutSlotChanged;
    }

    private void LayoutSlotChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_loadingEditor && e.PropertyName is nameof(HudSlotEditor.ContentIndex) or nameof(HudSlotEditor.IconIndex) or nameof(HudSlotEditor.Template))
            UpdateEditor();
    }

    private void LoadLayout(HudLayout layout)
    {
        var slots = layout.Header(false).Concat(layout.Header(true)).Append(layout.Detail).ToArray();
        for (var i = 0; i < slots.Length; i++) _layoutSlots[i].Load(slots[i]);
    }

    [RelayCommand]
    private void ResetLayout()
    {
        _loadingEditor = true;
        LoadLayout(new HudLayout());
        _loadingEditor = false;
        UpdateEditor();
    }
}

public partial class HudSlotEditor(string name, bool isDetail = false) : ObservableObject
{
    public static string[] ContentNames { get; } = ["不显示", "图标", "标题", "数值", "说明", "百分比", "环形进度", "完整信息", "自定义文字"];
    public static string[] IconNames { get; } = ["跟随指标", "电池", "时钟", "CPU", "内存", "网速", "网络延迟", "HTTP", "JSON", "温度", "余额", "磁盘", "运行时间", "GPU"];
    public string Name { get; } = name;
    public bool IsDetail { get; } = isDetail;
    public int TemplateLimit => IsDetail ? 320 : 120;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustom), nameof(HasIcon))]
    private int _contentIndex;
    [ObservableProperty] private int _iconIndex;
    [ObservableProperty] private string _template = "";
    public bool IsCustom => ContentIndex == (int)HudContent.Custom;
    public bool HasIcon => ContentIndex is (int)HudContent.Icon or (int)HudContent.Progress;
    public HudSlot Spec => new((HudContent)ContentIndex, IconIndex - 1, Template ?? "");
    public void Load(HudSlot slot) { ContentIndex = (int)slot.Content; IconIndex = slot.Icon + 1; Template = slot.Template; }
}
