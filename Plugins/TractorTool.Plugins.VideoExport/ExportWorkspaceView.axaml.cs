using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
namespace TractorTool.Plugins.VideoExport;
public partial class ExportWorkspaceView : UserControl
{
    private bool _picking;
    public ExportWorkspaceView()
    {
        InitializeComponent();
        SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < 880;
            Columns.ColumnDefinitions = new(narrow ? "*" : "380,18,*");
            Columns.RowDefinitions = new(narrow ? "*,16,*" : "*");
            Grid.SetColumn(PresetPanel, narrow ? 0 : 2); Grid.SetRow(PresetPanel, narrow ? 2 : 0);
        };
    }
    private void CardsResized(object? sender, SizeChangedEventArgs e)
    {
        if (PresetItems.ItemsPanelRoot is not WrapPanel panel) return;
        var width = Math.Max(1, e.NewSize.Width);
        var columns = Math.Max(1, (int)((width + 12) / 332));
        panel.ItemWidth = Math.Max(1, (width - (columns - 1) * 12) / columns);
    }
    private async void ChooseOutput(object? sender, RoutedEventArgs e)
    {
        if (_picking || DataContext is not VideoViewModel { IsBusy: false } model || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        _picking = true;
        try
        {
            var folders = await storage.OpenFolderPickerAsync(new() { Title = "选择输出文件夹", AllowMultiple = false });
            try
            {
                if (!IsLoaded || !ReferenceEquals(DataContext, model) || model.IsBusy || folders.Count == 0) return;
                model.OutputDirectory = folders[0].TryGetLocalPath() ?? throw new NotSupportedException("请选择本地文件夹。");
            }
            finally { foreach (var folder in folders) folder.Dispose(); }
        }
        catch (Exception ex) { model.Error = "无法选择文件夹：" + ex.Message; }
        finally { _picking = false; }
    }
}
