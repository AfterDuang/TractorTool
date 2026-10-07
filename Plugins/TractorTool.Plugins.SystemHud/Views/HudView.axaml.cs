using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace TractorTool.Plugins.SystemHud;
public partial class HudView : UserControl
{
    private bool? _compact;

    public HudView()
    {
        InitializeComponent();
        SizeChanged += (_, e) =>
        {
            var compact = e.NewSize.Width < 900;
            if (_compact == compact) return;
            _compact = compact;
            Workspace.ColumnDefinitions = new(compact ? "*" : "400,*");
            Workspace.RowDefinitions = new(compact ? "Auto,Auto" : "*");
            Grid.SetColumn(EditorTabs, compact ? 0 : 1);
            Grid.SetRow(EditorTabs, compact ? 1 : 0);
            // Only one vertical scroll owner per pane; compact layout scrolls as a whole.
            WorkspaceScroll.VerticalScrollBarVisibility = compact ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            var innerScrolling = compact ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            PreviewScroll.VerticalScrollBarVisibility = innerScrolling;
            ProfileScroll.VerticalScrollBarVisibility = innerScrolling;
            PositionScroll.VerticalScrollBarVisibility = innerScrolling;
        };
    }
}
