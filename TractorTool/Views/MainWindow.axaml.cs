using Avalonia.Media;
using Avalonia.Controls;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Windowing;
using TractorTool.ViewModels;

namespace TractorTool.Views;
public partial class MainWindow : FAAppWindow
{
    private MainViewModel? _presentedViewModel;
    public MainWindow()
    {
        InitializeComponent();
        TitleBar.Height = 48;
        TitleBar.ExtendsContentIntoTitleBar = true;
        NavigationView.ItemInvoked += (_, e) =>
        {
            var item = e.InvokedItem as NavigationItem ?? e.InvokedItemContainer?.DataContext as NavigationItem;
            item?.OpenCommand.Execute(null);
            // NavigationView selects on invocation; restore the committed item after cancellation.
            if (DataContext is MainViewModel vm)
                NavigationView.SetCurrentValue(FANavigationView.SelectedItemProperty, vm.SelectedNavigation);
        };
        DataContextChanged += (_, _) => ConnectPagePresenter();
        Closed += (_, _) => _presentedViewModel?.Dispose();
        ActualThemeVariantChanged += (_, _) => ApplyCaptionColors();
        Opened += (_, _) => ApplyCaptionColors();
    }
    private void ConnectPagePresenter()
    {
        var next = DataContext as MainViewModel;
        if (ReferenceEquals(next, _presentedViewModel)) return;
        _presentedViewModel?.DetachPagePresenter();
        _presentedViewModel = next;
        next?.AttachPagePresenter(PagePresenter.Present, PagePresenter.Clear);
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
