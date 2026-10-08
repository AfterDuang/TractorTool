using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace TractorTool.Plugins.SystemHud;
public partial class HudView : UserControl
{
    private bool? _compact;
    private bool? _stackAppearance;

    public HudView()
    {
        InitializeComponent();
        AppearanceGrid.SizeChanged += (_, e) =>
        {
            var stack = e.NewSize.Width < 640;
            if (_stackAppearance == stack) return;
            _stackAppearance = stack;
            AppearanceGrid.ColumnDefinitions = new(stack ? "*" : "*,*");
            AppearanceGrid.RowDefinitions = new(stack ? "Auto,Auto" : "Auto");
            Grid.SetColumn(ColorSettings, stack ? 0 : 1);
            Grid.SetRow(ColorSettings, stack ? 1 : 0);
        };
        SizeChanged += (_, e) =>
        {
            var compact = e.NewSize.Width - 56 < 900;
            if (_compact == compact) return;
            _compact = compact;
            Workspace.ColumnDefinitions = new(compact ? "*" : "400,*");
            Workspace.RowDefinitions = new(compact ? "Auto,Auto" : "*");
            Grid.SetColumn(EditorPanel, compact ? 0 : 1);
            Grid.SetRow(EditorPanel, compact ? 1 : 0);
            // Only one vertical scroll owner per pane; compact layout scrolls as a whole.
            WorkspaceScroll.VerticalScrollBarVisibility = compact ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            var innerScrolling = compact ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            PreviewScroll.VerticalScrollBarVisibility = innerScrolling;
            ProfileScroll.VerticalScrollBarVisibility = innerScrolling;
        };
    }
}
