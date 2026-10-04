using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TractorTool.ViewModels;

namespace TractorTool.Views;

public partial class PluginsView : UserControl
{
    public PluginsView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropOverlay.IsVisible = false);
        AddHandler(DragDrop.DropEvent, OnDrop);
        Unloaded += (_, _) => DropOverlay.IsVisible = false;
    }

    private static string? GetArchive(IDataTransfer data)
    {
        // Do not read archive contents while hovering, or assume every storage item is a local file.
        try
        {
            var files = data.TryGetFiles()?.Take(2).ToArray();
            if (files is not { Length: 1 } || files[0] is not IStorageFile file) return null;
            var path = file.TryGetLocalPath();
            return path is not null && Path.IsPathFullyQualified(path) &&
                string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase) ? path : null;
        }
        catch { return null; }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var accepted = DataContext is PluginsViewModel { CanInstall: true } && GetArchive(e.DataTransfer) is not null;
        e.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.IsVisible = accepted;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;
        var file = GetArchive(e.DataTransfer);
        e.DragEffects = DragDropEffects.None;
        e.Handled = true;
        if (file is null || DataContext is not PluginsViewModel { CanInstall: true } vm) return;
        e.DragEffects = DragDropEffects.Copy;
        await vm.InstallArchiveAsync(file);
    }

    private async void OnPluginEnabledChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { DataContext: PluginCard card } toggle || toggle.IsChecked == card.RequestedEnabled) return;
        // IsCheckedChanged covers both clicking and dragging the thumb. Initial bindings are equal
        // to the requested state and must never enqueue a management operation.
        if (DataContext is PluginsViewModel { CanInstall: true } vm && card.ToggleCommand is { } command && command.CanExecute(null))
        {
            await command.ExecuteAsync(null);
            // A failed write leaves the model value unchanged. Explicitly restore the local thumb
            // even if the binding suppresses an equal-value notification; keep its native transition.
            if (ReferenceEquals(DataContext, vm) && vm.CanInstall && ReferenceEquals(toggle.DataContext, card))
                toggle.SetCurrentValue(ToggleSwitch.IsCheckedProperty, (bool?)card.RequestedEnabled);
        }
        else toggle.SetCurrentValue(ToggleSwitch.IsCheckedProperty, (bool?)card.RequestedEnabled);
    }
}
