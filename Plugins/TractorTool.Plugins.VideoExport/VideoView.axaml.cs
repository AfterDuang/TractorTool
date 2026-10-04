using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
namespace TractorTool.Plugins.VideoExport;
public partial class VideoView : UserControl
{
    private bool _picking;
    public VideoView() => InitializeComponent();
    private async void ChooseVideo(object? sender, RoutedEventArgs e)
        => await ChooseFileAsync(false);
    private async void ChooseCover(object? sender, RoutedEventArgs e)
        => await ChooseFileAsync(true);
    private async Task ChooseFileAsync(bool cover)
    {
        if (_picking || DataContext is not VideoViewModel model || model.IsBusy) return;
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;
        _picking = true; PickerError.IsVisible = false;
        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = cover ? "选择封面图片" : "选择源视频", AllowMultiple = false,
                FileTypeFilter = cover ? [new("图片") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"] }]
                    : [new("视频") { Patterns = ["*.mp4", "*.mkv", "*.mov", "*.avi", "*.webm", "*.m4v", "*.ts"] }, FilePickerFileTypes.All]
            });
            try
            {
                if (!IsLoaded || !ReferenceEquals(DataContext, model) || model.IsBusy || files.Count == 0) return;
                var path = files[0].TryGetLocalPath() ?? throw new NotSupportedException("请选择本地文件。");
                if (cover) model.CoverPath = path;
                else await model.SelectSourceAsync(path);
            }
            finally { foreach (var file in files) file.Dispose(); }
        }
        catch (Exception ex) { PickerError.Text = "无法选择文件：" + ex.Message; PickerError.IsVisible = true; }
        finally { _picking = false; }
    }

    private async void ChooseOutput(object? sender, RoutedEventArgs e)
    {
        if (_picking || DataContext is not VideoViewModel model || model.IsBusy) return;
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;
        _picking = true; PickerError.IsVisible = false;
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
        catch (Exception ex) { PickerError.Text = "无法选择文件夹：" + ex.Message; PickerError.IsVisible = true; }
        finally { _picking = false; }
    }
}
