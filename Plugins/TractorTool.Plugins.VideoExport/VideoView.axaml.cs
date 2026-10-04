using Avalonia.Controls;
using Avalonia.Interactivity;
namespace TractorTool.Plugins.VideoExport;
public partial class VideoView : UserControl
{
    public VideoView()
    {
        InitializeComponent();
        Loaded += (_, _) => Present();
        SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < 700;
            Grid.SetColumn(Functions, narrow ? 0 : 1); Grid.SetRow(Functions, narrow ? 2 : 0);
            Grid.SetRowSpan(Functions, narrow ? 1 : 2);
            Functions.Margin = new(0, narrow ? 12 : 0, 0, 0);
            Header.RowDefinitions = new(narrow ? "Auto,Auto,Auto" : "Auto,Auto");
        };
    }
    private void ShowExport(object? sender, RoutedEventArgs e) => Switch(false);
    private void ShowInformation(object? sender, RoutedEventArgs e) => Switch(true);
    private void Switch(bool information)
    {
        if (DataContext is not VideoViewModel vm || vm.IsInformation == information) return;
        vm.IsInformation = information; Present();
    }
    private void Present()
    {
        if (DataContext is not VideoViewModel vm) return;
        Workspace.Present(vm.IsInformation ? new MediaInfoView { DataContext = vm } : new ExportWorkspaceView { DataContext = vm });
    }
}
