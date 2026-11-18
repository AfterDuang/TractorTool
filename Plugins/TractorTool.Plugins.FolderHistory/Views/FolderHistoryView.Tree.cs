using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TractorTool.Plugins.FolderHistory.ViewModels;

namespace TractorTool.Plugins.FolderHistory.Views;

public partial class FolderHistoryView
{
    private FolderHistoryViewModel? _treeModel;
    private bool _syncingTree;
    private TreePress? _press;

    private sealed record TreePress(IPointer Pointer, Point Origin, TreeItem[] Initial, bool Additive)
    {
        public bool Dragging { get; set; }
    }

    private void InitializeTreeInput()
    {
        FileTree.SelectionChanged += (_, _) => { if (!_syncingTree && _press is null) CommitTreeSelection(); };
        TreeArea.AddHandler(PointerPressedEvent, TreePressed, RoutingStrategies.Tunnel);
        TreeArea.AddHandler(PointerMovedEvent, TreeMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        TreeArea.AddHandler(PointerReleasedEvent, TreeReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        TreeArea.AddHandler(KeyDownEvent, TreeKeyDown, RoutingStrategies.Tunnel);
        TreeArea.PointerCaptureLost += (_, e) =>
        {
            if (ReferenceEquals(e.Source, TreeArea) && _press?.Dragging == true) EndTreePress(cancel: true);
        };
        TreeArea.PointerExited += (_, _) =>
        {
            if (_press is { Dragging: false }) EndTreePress(cancel: false);
        };
        TreeArea.AddHandler(PointerWheelChangedEvent, (_, _) => EndTreePress(cancel: true), RoutingStrategies.Tunnel);
    }

    private void BindTree(FolderHistoryViewModel model)
    {
        UnbindTree();
        _treeModel = model;
        model.PropertyChanged += TreePropertyChanged;
        SynchronizeTree();
    }

    private void UnbindTree()
    {
        EndTreePress(cancel: true);
        if (_treeModel is { } model) model.PropertyChanged -= TreePropertyChanged;
        _treeModel = null;
    }

    private void TreePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FolderHistoryViewModel.VisibleTreeItems) or nameof(FolderHistoryViewModel.SelectedTreeItems))
            SynchronizeTree();
    }

    private void SynchronizeTree()
    {
        if (_treeModel is not { } model) return;
        _syncingTree = true;
        try
        {
            if (!ReferenceEquals(FileTree.ItemsSource, model.VisibleTreeItems))
            {
                EndTreePress(cancel: true);
                FileTree.ItemsSource = model.VisibleTreeItems;
            }
            UpdateTreeSelection(model.SelectedTreeItems);
        }
        finally { _syncingTree = false; }
    }

    private void ToggleNode(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TreeItem item }) _treeModel?.ToggleExpanded(item);
    }

    private void TreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _press?.Dragging == true)
        {
            EndTreePress(cancel: true);
            e.Handled = true;
        }
        else if (e.KeyModifiers == KeyModifiers.None && FileTree.SelectedItem is TreeItem { HasChildren: true } node &&
                 (e.Key == Key.Right && !node.IsExpanded || e.Key == Key.Left && node.IsExpanded))
        {
            _treeModel?.ToggleExpanded(node);
            e.Handled = true;
        }
    }

    private void TreePressed(object? sender, PointerPressedEventArgs e)
    {
        if (_treeModel is null || e.Pointer.Type != PointerType.Mouse || _press is not null ||
            e.GetCurrentPoint(TreeArea).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed ||
            e.Source is not Visual source || source.FindAncestorOfType<Button>(includeSelf: true) is not null ||
            source.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null) return;
        _press = new(e.Pointer, e.GetPosition(TreeArea), _treeModel.SelectedTreeItems.ToArray(),
            e.KeyModifiers.HasFlag(KeyModifiers.Control));
    }

    private void TreeMoved(object? sender, PointerEventArgs e)
    {
        if (_press is not { } press || e.Pointer != press.Pointer) return;
        if (!e.GetCurrentPoint(TreeArea).Properties.IsLeftButtonPressed) { EndTreePress(cancel: true); return; }
        var position = e.GetPosition(TreeArea);
        if (!press.Dragging)
        {
            if (Math.Abs(position.X - press.Origin.X) < 5 && Math.Abs(position.Y - press.Origin.Y) < 5) return;
            press.Dragging = true;
            FileTree.Focus();
            e.Pointer.Capture(TreeArea);
        }
        DrawTreeSelection(press, position);
        e.Handled = true;
    }

    private void DrawTreeSelection(TreePress press, Point position)
    {
        var bounds = new Rect(press.Origin, new Point(Math.Clamp(position.X, 0, TreeArea.Bounds.Width),
            Math.Clamp(position.Y, 0, TreeArea.Bounds.Height))).Normalize();
        Canvas.SetLeft(SelectionBox, bounds.X);
        Canvas.SetTop(SelectionBox, bounds.Y);
        SelectionBox.Width = bounds.Width;
        SelectionBox.Height = bounds.Height;
        SelectionBox.IsVisible = true;

        var selected = press.Additive ? press.Initial.ToHashSet() : new HashSet<TreeItem>();
        var hitArea = new Rect(bounds.X, bounds.Y, Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
        foreach (var row in FileTree.GetRealizedContainers().OfType<ListBoxItem>())
        {
            if (row.DataContext is TreeItem item && row.TranslatePoint(default, TreeArea) is { } origin &&
                new Rect(origin, row.Bounds.Size).Intersects(hitArea)) selected.Add(item);
        }
        UpdateTreeSelection(selected);
    }

    private void TreeReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_press is not { } press || e.Pointer != press.Pointer ||
            e.InitialPressMouseButton != MouseButton.Left) return;
        if (press.Dragging) { DrawTreeSelection(press, e.GetPosition(TreeArea)); e.Handled = true; }
        EndTreePress(cancel: false);
    }

    private void EndTreePress(bool cancel)
    {
        if (_press is not { } press) return;
        _press = null;
        SelectionBox.IsVisible = false;
        if (ReferenceEquals(press.Pointer.Captured, TreeArea)) press.Pointer.Capture(null);
        if (cancel)
        {
            var syncing = _syncingTree;
            _syncingTree = true;
            try { UpdateTreeSelection(press.Initial); }
            finally { _syncingTree = syncing; }
        }
        else if (!_syncingTree) CommitTreeSelection();
    }

    private void UpdateTreeSelection(IReadOnlyCollection<TreeItem> items)
    {
        if (FileTree.SelectedItems is { } selected && items.Count == selected.Count &&
            items.ToHashSet().SetEquals(selected.Cast<TreeItem>())) return;
        FileTree.SelectedItems = items.ToList();
    }

    private void CommitTreeSelection() =>
        _treeModel?.SetSelection(FileTree.SelectedItems?.Cast<TreeItem>().ToArray() ?? []);
}
