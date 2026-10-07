using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
namespace TractorTool.Plugins.SystemHud;
public partial class HudCard : UserControl
{
    public event Action? ExpansionChanged;
    internal ToggleButton DragSurface => SummaryButton;
    public bool IsExpanded { get => SummaryButton.IsChecked == true; set => SummaryButton.IsChecked = value; }

    public HudCard()
    {
        InitializeComponent();
        SummaryButton.IsCheckedChanged += (_, _) =>
        {
            Chevron.RenderTransform = new RotateTransform(IsExpanded ? 180 : 0);
            ExpansionChanged?.Invoke();
        };
    }
}
