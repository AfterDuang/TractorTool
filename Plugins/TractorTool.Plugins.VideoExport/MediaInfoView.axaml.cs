using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
namespace TractorTool.Plugins.VideoExport;
public partial class MediaInfoView : UserControl
{
    public MediaInfoView()
    {
        InitializeComponent();
        SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < 850;
            Columns.ColumnDefinitions = new(narrow ? "*" : "320,20,*");
            Columns.RowDefinitions = new(narrow ? "Auto,16,*" : "*");
            Grid.SetColumn(ReportPanel, narrow ? 0 : 2); Grid.SetRow(ReportPanel, narrow ? 2 : 0);
        };
    }
    private async void CopyReport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not VideoViewModel model || !model.HasMediaReport) return;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("剪贴板暂不可用。");
            await clipboard.SetTextAsync(model.MediaReport);
        }
        catch (Exception ex) { model.Error = "复制失败：" + ex.Message; }
    }
}
