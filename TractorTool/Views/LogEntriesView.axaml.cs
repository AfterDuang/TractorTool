using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TractorTool.ViewModels;

namespace TractorTool.Views;

public partial class LogEntriesView : UserControl
{
    private LogsViewModel? _model;
    private ScrollViewer? _scroll;
    private bool _following = true, _scrollQueued;

    public LogEntriesView()
    {
        InitializeComponent();
        Loaded += (_, _) => Connect();
        Unloaded += (_, _) => Disconnect();
        DataContextChanged += (_, _) => { if (IsLoaded) Connect(); };
        LogList.AddHandler(PointerWheelChangedEvent, (_, e) => { if (e.Delta.Y > 0) _following = false; }, RoutingStrategies.Tunnel);
        LogList.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is Key.Up or Key.PageUp or Key.Home) _following = false;
        }, RoutingStrategies.Tunnel);
        LogList.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.Source is Avalonia.Visual source && (source is ScrollBar || source.GetVisualAncestors().OfType<ScrollBar>().Any()))
                _following = false;
        }, RoutingStrategies.Tunnel);
        LogList.AddHandler(PointerReleasedEvent, (_, _) =>
        {
            if (_scroll is not null) _following = AtEnd();
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void Connect()
    {
        Disconnect();
        _model = DataContext as LogsViewModel;
        _scroll = LogList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (_model is not null) _model.EntriesUpdated += FollowTail;
        if (_scroll is not null) _scroll.ScrollChanged += OnScrollChanged;
        _following = true;
        FollowTail();
    }

    private void Disconnect()
    {
        if (_model is not null) _model.EntriesUpdated -= FollowTail;
        if (_scroll is not null) _scroll.ScrollChanged -= OnScrollChanged;
        _model = null; _scroll = null;
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scroll is null) return;
        // Layout growth must not be mistaken for the user scrolling away from the end.
        if (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0)
        {
            if (AtEnd()) _following = true;
            FollowTail();
            return;
        }
        if (!_scrollQueued)
            _following = AtEnd();
    }

    private bool AtEnd() => _scroll is not null && _scroll.Offset.Y >= _scroll.Extent.Height - _scroll.Viewport.Height - 2;

    private void FollowTail()
    {
        if (!_following || _scroll is null || _scrollQueued) return;
        _scrollQueued = true;
        // One dispatch after item layout; no timer, polling or selection changes.
        Dispatcher.UIThread.Post(() =>
        {
            _scrollQueued = false;
            if (_following && _scroll is not null) _scroll.ScrollToEnd();
        }, DispatcherPriority.Loaded);
    }
    private async void CopyLogs(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LogsViewModel vm) return;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("剪贴板暂不可用。");
            await clipboard.SetTextAsync(vm.Text);
            vm.Status = "已复制当前筛选的记录";
        }
        catch (Exception ex) { vm.Status = "复制失败：" + ex.Message; }
    }
}
