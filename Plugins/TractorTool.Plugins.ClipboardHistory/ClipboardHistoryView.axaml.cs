using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace TractorTool.Plugins.ClipboardHistory;

public partial class ClipboardHistoryView : UserControl
{
    private Window? _window;
    public ClipboardHistoryView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            _window = TopLevel.GetTopLevel(this) as Window;
            if (_window is not null) _window.Activated += OnWindowActivated;
            if (DataContext is ClipboardHistoryViewModel vm) await vm.InitializeAsync();
        };
        Unloaded += (_, _) =>
        {
            if (_window is not null) _window.Activated -= OnWindowActivated;
            _window = null;
        };
        HistoryList.DoubleTapped += (_, e) =>
        {
            if (e.Source is not Control source) return;
            var row = source as ListBoxItem ?? source.FindAncestorOfType<ListBoxItem>();
            if (row?.DataContext is HistoryEntry entry && DataContext is ClipboardHistoryViewModel vm && vm.SelectedEntry?.Id == entry.Id)
                CopySelection();
        };
        HistoryList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter || (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control))
            {
                CopySelection();
                e.Handled = true;
            }
        };
    }

    private async void OnWindowActivated(object? sender, EventArgs e)
    {
        if (DataContext is ClipboardHistoryViewModel vm) await vm.RequestRefreshAsync();
    }

    private void CopySelection()
    {
        if (DataContext is ClipboardHistoryViewModel vm && vm.CopyCommand.CanExecute(null)) vm.CopyCommand.Execute(null);
    }
}
