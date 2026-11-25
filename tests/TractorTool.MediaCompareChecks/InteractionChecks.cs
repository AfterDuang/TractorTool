using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using TractorTool.Plugins.MediaCompare.Views;

internal static class InteractionChecks
{
    // The real Avalonia application is initialized by the opt-in diagnostic. Keep this
    // view detached: no second GPU surface, host services, or user data is needed.
    internal static void CheckEmptyDragRouting()
    {
        var view = new CompareView();
        var preview = view.FindControl<ComparePreview>("Preview")
            ?? throw new InvalidOperationException("Comparison preview was not found.");
        foreach (var routedEvent in new[] { DragDrop.DragEnterEvent, DragDrop.DragOverEvent, DragDrop.DropEvent })
        {
            using var data = new DataTransfer();
            var args = new DragEventArgs(routedEvent, data, preview,
                new Point(20, 20), KeyModifiers.None) { DragEffects = DragDropEffects.Copy };
            preview.RaiseEvent(args);
            if (!args.Handled || args.DragEffects != DragDropEffects.None)
                throw new InvalidOperationException($"{routedEvent.Name} did not reach the canvas rejection handler.");
        }
    }
}
