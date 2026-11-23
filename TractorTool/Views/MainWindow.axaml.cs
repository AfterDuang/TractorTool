using Avalonia.Media;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Windowing;
using TractorTool.ViewModels;
using TractorTool.Services;

namespace TractorTool.Views;
public partial class MainWindow : FAAppWindow
{
    private MainViewModel? _presentedViewModel;
    private readonly HostDialogs _dialogs;
    public Task<bool> ConfirmExitAsync(int count, CancellationToken token) => _dialogs.ConfirmExitAsync(count, token);
    internal void BeginShutdown()
    {
        _dialogs.Dispose();
        IsEnabled = false;
    }
    public MainWindow()
    {
        InitializeComponent();
        _dialogs = new(this);
        TitleBar.Height = TitleBarDragRegion.Height;
        TitleBar.ExtendsContentIntoTitleBar = true;
        StartupExit.Click += (_, _) => Close();
        NavigationView.ItemInvoked += (_, e) =>
        {
            var item = e.InvokedItem as NavigationItem ?? e.InvokedItemContainer?.DataContext as NavigationItem;
            item?.OpenCommand.Execute(null);
            // NavigationView selects on invocation; restore the committed item after cancellation.
            if (DataContext is MainViewModel vm)
                NavigationView.SetCurrentValue(FANavigationView.SelectedItemProperty, vm.SelectedNavigation);
        };
        DataContextChanged += (_, _) => ConnectPagePresenter();
        Closed += (_, _) => { _dialogs.Dispose(); Disconnect(); _presentedViewModel?.Dispose(); };
        ActualThemeVariantChanged += (_, _) => ApplyCaptionColors();
        Opened += (_, _) => ApplyCaptionColors();
    }

    // Presentation only: App still controls startup ownership, cancellation and service creation.
    public void ShowStartup(string message)
    {
        NavigationView.IsVisible = false;
        StartupPanel.IsVisible = true;
        StartupStatus.Text = message;
        StartupProgress.IsVisible = true;
        StartupProgress.IsIndeterminate = true;
        StartupError.IsVisible = false;
    }

    public void ShowStartupError(string message)
    {
        ShowStartup("启动未完成");
        StartupProgress.IsIndeterminate = false;
        StartupProgress.IsVisible = false;
        StartupError.Text = message;
        StartupError.IsVisible = true;
    }

    public void ShowWorkspace()
    {
        StartupProgress.IsIndeterminate = false;
        StartupPanel.IsVisible = false;
        NavigationView.IsVisible = true;
    }
    private void ConnectPagePresenter()
    {
        var next = DataContext as MainViewModel;
        if (ReferenceEquals(next, _presentedViewModel)) return;
        Disconnect();
        _presentedViewModel = next;
        if (next is not null)
        {
            next.Plugins.PickArchiveAsync = async () =>
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "安装或更新插件", AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("插件 ZIP") { Patterns = ["*.zip"] }]
                });
                return files.Count == 0 ? null : files[0].TryGetLocalPath()
                    ?? throw new InvalidOperationException("请选择本地插件 ZIP 文件。");
            };
            next.Plugins.ConfirmAsync = message => _dialogs.ConfirmAsync("插件变更", message);
            next.Settings.ExternalTools.ConnectConfirmation(_dialogs.ConfirmAsync);
            next.Settings.Migration.PickDirectoryAsync = async () =>
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                    { Title = "选择空的应用数据目录", AllowMultiple = false });
                return folders.Count == 0 ? null : folders[0].TryGetLocalPath()
                    ?? throw new InvalidOperationException("请选择本机磁盘上的空目录。");
            };
            next.Settings.Migration.ConfirmAsync = message => _dialogs.ConfirmAsync("迁移应用数据", message);
        }
        next?.AttachPagePresenter(PagePresenter.Present, PagePresenter.Clear);
    }
    private void Disconnect()
    {
        if (_presentedViewModel is not { } previous) return;
        previous.Plugins.PickArchiveAsync = null;
        previous.Plugins.ConfirmAsync = null;
        previous.Settings.ExternalTools.ConnectConfirmation(null);
        previous.Settings.Migration.PickDirectoryAsync = null;
        previous.Settings.Migration.ConfirmAsync = null;
        previous.DetachPagePresenter();
    }
    private void ApplyCaptionColors()
    {
        if (this.TryFindResource("AppTextBrush", out var value) && value is ISolidColorBrush text)
        {
            TitleBar.ButtonForegroundColor = text.Color;
            TitleBar.ForegroundColor = text.Color;
        }
        TitleBar.ButtonBackgroundColor = Colors.Transparent;
        TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
    }
}
