using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace TractorTool.Plugins.SystemHud;

// One renderer for every region and every metric; it never samples data or owns gestures.
public partial class HudSlotView : UserControl
{
    public static readonly StyledProperty<TextWrapping> TextWrappingProperty =
        AvaloniaProperty.Register<HudSlotView, TextWrapping>(nameof(TextWrapping), TextWrapping.NoWrap);
    public TextWrapping TextWrapping { get => GetValue(TextWrappingProperty); set => SetValue(TextWrappingProperty, value); }
    public HudSlotView() => InitializeComponent();
}
