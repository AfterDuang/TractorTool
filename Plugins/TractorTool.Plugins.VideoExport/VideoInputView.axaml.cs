using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
namespace TractorTool.Plugins.VideoExport;
public partial class VideoInputView : UserControl
{
    private bool _picking;
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];
    public VideoInputView()
    {
        InitializeComponent();
        WireDrop(VideoDrop, false); WireDrop(CoverDrop, true);
    }
    private void WireDrop(Border zone, bool cover)
    {
        zone.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            var accepted = DataContext is VideoViewModel { IsBusy: false } && GetFile(e.DataTransfer, cover) is not null;
            e.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
            zone.Opacity = accepted ? 0.75 : 1; e.Handled = true;
        });
        zone.AddHandler(DragDrop.DragLeaveEvent, (_, _) => zone.Opacity = 1);
        zone.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            zone.Opacity = 1; e.Handled = true; e.DragEffects = DragDropEffects.None;
            if (DataContext is not VideoViewModel { IsBusy: false } model || GetFile(e.DataTransfer, cover) is not { } path) return;
            e.DragEffects = DragDropEffects.Copy; PickerError.IsVisible = false;
            try { if (cover) model.CoverPath = path; else await model.SelectSourceAsync(path); }
            catch (Exception ex) { ShowError(ex); }
        });
        Unloaded += (_, _) => zone.Opacity = 1;
    }
    private static string? GetFile(IDataTransfer data, bool cover)
    {
        try
        {
            var files = data.TryGetFiles()?.Take(2).ToArray();
            if (files is not { Length: 1 } || files[0] is not IStorageFile file) return null;
            var path = file.TryGetLocalPath();
            return path is not null && Path.IsPathFullyQualified(path) && (!cover || ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) ? path : null;
        }
        catch { return null; }
    }
    private async void ChooseVideo(object? sender, RoutedEventArgs e) => await ChooseFileAsync(false);
    private async void ChooseCover(object? sender, RoutedEventArgs e) => await ChooseFileAsync(true);
    private void ClearCover(object? sender, RoutedEventArgs e) { if (DataContext is VideoViewModel { IsBusy: false } model) model.CoverPath = ""; }
    private async Task ChooseFileAsync(bool cover)
    {
        if (_picking || DataContext is not VideoViewModel { IsBusy: false } model || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        _picking = true; PickerError.IsVisible = false;
        try
        {
            var files = await storage.OpenFilePickerAsync(new()
            {
                Title = cover ? "选择封面图片" : "选择源视频", AllowMultiple = false,
                FileTypeFilter = cover ? [new("图片") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"] }]
                    : [new("视频") { Patterns = ["*.mp4", "*.mkv", "*.mov", "*.avi", "*.webm", "*.m4v", "*.ts"] }, FilePickerFileTypes.All]
            });
            try
            {
                if (!IsLoaded || !ReferenceEquals(DataContext, model) || model.IsBusy || files.Count == 0) return;
                var path = files[0].TryGetLocalPath() ?? throw new NotSupportedException("请选择本地文件。");
                if (cover) model.CoverPath = path; else await model.SelectSourceAsync(path);
            }
            finally { foreach (var file in files) file.Dispose(); }
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _picking = false; }
    }
    private void ShowError(Exception ex) { PickerError.Text = "无法读取文件：" + ex.Message; PickerError.IsVisible = true; }
}
