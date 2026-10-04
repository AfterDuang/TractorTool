using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
namespace TractorTool.Plugins.VideoExport;
public partial class VideoView : UserControl
{
    private bool _picking;
    public VideoView() => InitializeComponent();
    private async void ChooseVideo(object? sender, RoutedEventArgs e)
    {
        if (_picking || DataContext is not VideoViewModel model || model.IsBusy) return;
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;
        _picking = true; PickerError.IsVisible = false;
        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择源视频", AllowMultiple = false,
                FileTypeFilter = [new("视频") { Patterns = ["*.mp4", "*.mkv", "*.mov", "*.avi", "*.webm", "*.m4v", "*.ts"] }, FilePickerFileTypes.All]
            });
            try
            {
                if (!IsLoaded || !ReferenceEquals(DataContext, model) || files.Count == 0) return;
                var path = files[0].TryGetLocalPath() ?? throw new NotSupportedException("请选择本地视频文件。");
                await model.SelectSourceAsync(path);
            }
            finally { foreach (var file in files) file.Dispose(); }
        }
        catch (Exception ex) { PickerError.Text = "无法选择文件：" + ex.Message; PickerError.IsVisible = true; }
        finally { _picking = false; }
    }
}
