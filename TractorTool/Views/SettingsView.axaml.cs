using Avalonia.Controls;
using Avalonia.VisualTree;
using System.ComponentModel;
using TractorTool.UI;
using TractorTool.ViewModels;
using FluentAvalonia.UI.Controls;
using Avalonia.Controls.Primitives;
namespace TractorTool.Views;
public partial class SettingsView : UserControl, IPluginPageLifetime
{
    private SettingsViewModel? _vm;
    private Control? _page;
    private SettingsPage? _shown;
    private bool _navigating;
    public SettingsView()
    {
        InitializeComponent();
        // Frame uses a direct event. Preserve the nested plugin page's right to cancel
        // when the user leaves the entire settings area, as well as when changing category.
        AddHandler(FAFrame.NavigatingFromEvent, (_, args) =>
        {
            if (_page is null || args.Cancel) return;
            var source = args.Source; var handled = args.Handled;
            try { args.Handled = false; _page.RaiseEvent(args); }
            finally { args.Source = source; args.Handled = handled; }
        });
        Loaded += (_, _) => Connect();
        Unloaded += (_, _) => OnNavigatedFrom();
        DataContextChanged += (_, _) => { if (IsLoaded) Connect(); };
        SizeChanged += (_, _) =>
        {
            var compact = Bounds.Width < 780;
            Sidebar.IsVisible = !compact; CompactNavigation.IsVisible = compact;
            Layout.ColumnDefinitions[0].Width = new GridLength(compact ? 0 : 180);
        };
    }
    private void Connect()
    {
        if (ReferenceEquals(_vm, DataContext)) return;
        OnNavigatedFrom();
        _vm = DataContext as SettingsViewModel;
        if (_vm is null) return;
        _vm.PropertyChanged += Changed;
        Present();
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(SettingsViewModel.SelectedPage)) Present(); }
    private void CategoryChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_navigating && sender is SelectingItemsControl { SelectedItem: SettingsPage selected } &&
            DataContext is SettingsViewModel vm && selected != vm.SelectedCategory)
            vm.NavigateCommand.Execute(selected);
    }
    private void Present()
    {
        if (_navigating || _vm?.SelectedPage is not { } selected || selected == _shown) return;
        _navigating = true;
        var vm = _vm;
        Control? candidate = null;
        try
        {
            var created = selected.Factory() ?? throw new InvalidDataException("设置页面工厂返回空值。");
            if (created.Parent is not null || created.GetVisualParent() is not null)
                throw new InvalidDataException("页面工厂必须返回尚未挂载的独立页面。");
            candidate = created;
            if (!ReferenceEquals(_vm, vm)) { Release(candidate); return; }
            if (!SectionPresenter.Present(candidate))
            { Release(candidate); _vm.SelectedPage = _shown; return; }
            var previous = _page; _page = candidate; _shown = selected;
            Release(previous);
        }
        catch (Exception ex)
        {
            Release(candidate);
            vm.LogPageError(ex);
            if (!ReferenceEquals(_vm, vm)) return;
            var previous = _page;
            var retry = new Button { Content = "重试" };
            retry.Click += (_, _) => Present();
            try
            {
                var errorPage = new StackPanel { Spacing = 12, Children = {
                    new TextBlock { Text = "设置页面无法打开", Classes = { "sectionTitle" } },
                    new TextBlock { Text = ex.GetBaseException().Message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, retry } };
                if (!SectionPresenter.Present(errorPage)) { vm.SelectedPage = _shown; return; }
                _page = errorPage; _shown = null; Release(previous);
            }
            catch (Exception error) { _page = null; _shown = null; Release(previous); vm.LogPageError(error); }
        }
        finally { _navigating = false; }
    }
    private void Release(Control? page)
    {
        try
        {
            if (page is IPluginPageLifetime lifetime) lifetime.OnNavigatedFrom();
            else if (page?.DataContext is IPluginPageLifetime vm) vm.OnNavigatedFrom();
        }
        catch (Exception ex) { _vm?.LogPageError(ex); }
    }
    public void OnNavigatedFrom()
    {
        if (_vm is null) return;
        _vm.PropertyChanged -= Changed;
        try { SectionPresenter.Clear(); } catch (Exception ex) { _vm.LogPageError(ex); }
        Release(_page); _page = null; _shown = null; _vm = null;
    }
}
