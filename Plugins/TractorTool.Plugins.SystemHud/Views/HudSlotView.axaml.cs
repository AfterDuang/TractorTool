using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace TractorTool.Plugins.SystemHud;

// One renderer for every region and every metric; it never samples data or owns gestures.
public partial class HudSlotView : UserControl
{
    public static readonly StyledProperty<HudSlotDisplay?> ItemProperty =
        AvaloniaProperty.Register<HudSlotView, HudSlotDisplay?>(nameof(Item));
    public HudSlotDisplay? Item { get => GetValue(ItemProperty); set => SetValue(ItemProperty, value); }
    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<HudSlotView, double>(nameof(IconSize), 24);
    public double IconSize { get => GetValue(IconSizeProperty); set => SetValue(IconSizeProperty, value); }
    public static readonly StyledProperty<TextWrapping> TextWrappingProperty =
        AvaloniaProperty.Register<HudSlotView, TextWrapping>(nameof(TextWrapping), TextWrapping.NoWrap);
    public TextWrapping TextWrapping { get => GetValue(TextWrappingProperty); set => SetValue(TextWrappingProperty, value); }
    public static readonly StyledProperty<TextAlignment> TextAlignmentProperty =
        AvaloniaProperty.Register<HudSlotView, TextAlignment>(nameof(TextAlignment));
    public TextAlignment TextAlignment { get => GetValue(TextAlignmentProperty); set => SetValue(TextAlignmentProperty, value); }
    public HudSlotView() => InitializeComponent();
}
