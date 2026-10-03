using Avalonia.Controls;

namespace TractorTool.Plugins.ClipboardHistory;

public partial class ClipboardHistoryView : UserControl
{
    public ClipboardHistoryView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is ClipboardHistoryViewModel vm) await vm.InitializeAsync();
        };
    }
}
