using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TractorTool.Plugins.FolderHistory.ViewModels;
using TractorTool.UI;

namespace TractorTool.Plugins.FolderHistory.Views;

public partial class FolderHistoryView : UserControl, IPluginPageLifetime
{
    private bool _picking;
    private bool _wide;

    public FolderHistoryView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is FolderHistoryViewModel model) await model.InitializeAsync();
        };
        Workspace.SizeChanged += (_, e) =>
        {
            var wide = e.NewSize.Width >= 740;
            if (wide == _wide) return;
            _wide = wide;
            Workspace.ColumnDefinitions = new(wide ? "232,*" : "*");
            Workspace.RowDefinitions = new(wide ? "Auto" : "Auto,Auto");
            Grid.SetColumn(FolderDetails, wide ? 1 : 0);
            Grid.SetRow(FolderDetails, wide ? 0 : 1);
        };
    }

    private async void AddFolder(object? sender, RoutedEventArgs e) =>
        await PickFolderAsync("选择要保存版本的文件夹", (model, root) => model.AddFolderAsync(root));

    private async void ExportAll(object? sender, RoutedEventArgs e) => await ExportAsync(singleEntry: false);

    private async void ExportEntry(object? sender, RoutedEventArgs e) => await ExportAsync(singleEntry: true);

    private Task ExportAsync(bool singleEntry)
    {
        if (DataContext is not FolderHistoryViewModel { SelectedFolder: { } folder, SelectedVersion: { } version } model)
            return Task.CompletedTask;
        var path = singleEntry ? model.SelectedEntry?.Path : null;
        if (singleEntry && path is null) return Task.CompletedTask;
        return PickFolderAsync("选择另存位置，将在其中创建新文件夹", (vm, parent) => vm.ExportAsync(folder.Root, version.Id, parent, path));
    }

    public void OnNavigatedFrom() => DataContext = null;

    private async Task PickFolderAsync(string title, Func<FolderHistoryViewModel, string, Task> apply)
    {
        if (_picking || DataContext is not FolderHistoryViewModel { IsBusy: false } model) return;
        _picking = true;
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null || !storage.CanPickFolder) throw new NotSupportedException("当前窗口无法打开文件夹选择器。");
            var folders = await storage.OpenFolderPickerAsync(new() { Title = title, AllowMultiple = false });
            string? path = null;
            try
            {
                if (folders.Count == 0 || !IsLoaded || !ReferenceEquals(DataContext, model) || model.IsBusy) return;
                path = folders[0].TryGetLocalPath() ?? throw new NotSupportedException("请选择本地文件夹。");
            }
            finally { foreach (var folder in folders) folder.Dispose(); }
            await apply(model, path);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (IsLoaded && ReferenceEquals(DataContext, model)) model.ReportPickerError(error); }
        finally { _picking = false; }
    }
}
