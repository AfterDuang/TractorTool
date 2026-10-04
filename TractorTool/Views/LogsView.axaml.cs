using Avalonia.Controls;
using TractorTool.ViewModels;

namespace TractorTool.Views;

public partial class LogsView : UserControl
{
    public LogsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => PresentSection();
    }
    private void SectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, Sections)) PresentSection();
    }
    private void PresentSection()
    {
        if (ContentHost is null || DataContext is not LogsViewModel vm) return;
        ContentHost.Present(Sections.SelectedIndex == 1
            ? new TasksView { DataContext = vm.Tasks }
            : new LogEntriesView { DataContext = vm });
    }
}
