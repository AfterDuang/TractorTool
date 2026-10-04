using Avalonia.Controls;
namespace TractorTool.Plugins.VideoExport;
public partial class VideoView : UserControl
{
    public VideoView()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (DataContext is VideoViewModel vm) Functions.SelectedIndex = vm.IsInformation ? 1 : 0; Present(); };
        SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < 700;
            Grid.SetColumn(Functions, narrow ? 0 : 1); Grid.SetRow(Functions, narrow ? 2 : 0);
            Grid.SetRowSpan(Functions, narrow ? 1 : 2);
            Functions.Margin = new(0, narrow ? 12 : 0, 0, 0);
            Header.RowDefinitions = new(narrow ? "Auto,Auto,Auto" : "Auto,Auto");
        };
    }
    private void FunctionChanged(object? sender, SelectionChangedEventArgs e)
    { if (IsLoaded && Functions.SelectedIndex >= 0) Switch(Functions.SelectedIndex == 1); }
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
