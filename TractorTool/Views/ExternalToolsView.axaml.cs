using Avalonia.Controls;
namespace TractorTool.Views;
public partial class ExternalToolsView : UserControl
{
    public ExternalToolsView() => InitializeComponent();

    private void CategoryChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, Categories) && PackagesScroll is not null) PackagesScroll.Offset = default;
    }
}
