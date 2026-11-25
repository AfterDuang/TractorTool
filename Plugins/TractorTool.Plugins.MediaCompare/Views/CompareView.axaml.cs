using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TractorTool.Plugins.MediaCompare.Media;
using TractorTool.Plugins.MediaCompare.ViewModels;
using TractorTool.UI;

namespace TractorTool.Plugins.MediaCompare.Views;

public partial class CompareView : UserControl, IPluginPageLifetime
{
    private CompareViewModel? _attached;
    private bool _picking;
    private bool _playbackSpacePressed;
    private bool _playbackSpaceArmed;

    public CompareView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) => Detach();
        AddHandler(KeyDownEvent, OnPlaybackKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnPlaybackKeyUp, RoutingStrategies.Tunnel);
        LostFocus += (_, _) => ResetPlaybackKey();
        DropSurface.AddHandler(DragDrop.DragEnterEvent, OnDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        DropSurface.AddHandler(DragDrop.DragOverEvent, OnDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        DropSurface.AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearDropFeedback(), RoutingStrategies.Bubble, handledEventsToo: true);
        DropSurface.AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Bubble, handledEventsToo: true);
        Timeline.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(Timeline).Properties.IsLeftButtonPressed) _attached?.BeginScrub();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        Timeline.AddHandler(PointerReleasedEvent, async (_, _) =>
        {
            if (_attached is { } model) await model.EndScrubAsync(Timeline.Value);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        Timeline.PointerCaptureLost += async (_, _) =>
        {
            if (_attached is { IsScrubbing: true } model) await model.EndScrubAsync(Timeline.Value);
        };
        Timeline.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty && _attached is { IsScrubbing: true } model)
                model.RequestSeek(Timeline.Value);
        };
        Timeline.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.KeyModifiers != KeyModifiers.None || _attached is not { CanSeek: true } model) return;
            if (e.Key is Key.Home or Key.End)
            { e.Handled = true; model.RequestSeek(e.Key == Key.Home ? 0 : model.DurationSeconds); }
            else if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown)
            {
                e.Handled = true;
                var change = e.Key is Key.PageUp or Key.PageDown ? 1 : .1;
                model.RequestSeek(model.PositionSeconds + (e.Key is Key.Left or Key.Down or Key.PageDown ? -change : change));
            }
        }, RoutingStrategies.Tunnel);
        Preview.Error += error => Dispatcher.UIThread.Post(() => _attached?.ReportRendererError(error));
        Preview.RendererStatusChanged += status => Dispatcher.UIThread.Post(() => { if (_attached is { } model) model.RendererStatus = status; });
        Preview.AvailabilityChanged += available => { if (_attached is { } model) model.RendererAvailable = available; };
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CompareViewModel model || _attached is not null) return;
        _attached = model;
        model.RendererAvailable = Preview.IsAvailable;
        model.FrameReady += Preview.Submit;
        model.ViewResetRequested += Preview.ResetView;
        Preview.Focus();
        await model.EnterViewAsync();
    }

    public void OnNavigatedFrom() => Detach();
    private void Detach()
    {
        ResetPlaybackKey();
        ClearDropFeedback();
        if (_attached is not { } model) return;
        _attached = null;
        model.FrameReady -= Preview.Submit;
        model.ViewResetRequested -= Preview.ResetView;
        model.LeaveView();
    }

    private async void ChooseMedia(object? sender, RoutedEventArgs e) => await ChooseAsync(sender, false);
    private async void ChooseSequence(object? sender, RoutedEventArgs e) => await ChooseAsync(sender, true);
    private async Task ChooseAsync(object? sender, bool sequence)
    {
        if (_picking || _attached is not { CanOperate: true } model || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var side = sender is Button { Tag: "B" } ? MediaSide.B : MediaSide.A;
        SourceAOptions.Flyout?.Hide(); SourceBOptions.Flyout?.Hide();
        _picking = true;
        try
        {
            var files = await storage.OpenFilePickerAsync(new()
            {
                Title = sequence ? $"选择 {side} 序列中的第一张图片（连续编号）" : $"选择 {side} 素材",
                AllowMultiple = false,
                FileTypeFilter = sequence
                    ? [new("序列帧") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.tif", "*.tiff", "*.bmp", "*.exr"] }]
                    : [new("视频与图片") { Patterns = ["*.mp4", "*.mov", "*.mkv", "*.avi", "*.webm", "*.mxf", "*.m4v", "*.png", "*.jpg", "*.jpeg", "*.webp", "*.tif", "*.tiff", "*.bmp", "*.exr"] }, FilePickerFileTypes.All]
            });
            try
            {
                if (!IsLoaded || !ReferenceEquals(_attached, model) || files.Count == 0) return;
                var path = files[0].TryGetLocalPath() ?? throw new NotSupportedException("请选择本地素材。");
                Preview.Focus();
                await model.OpenAsync(side, path, sequence);
            }
            finally { foreach (var file in files) file.Dispose(); }
        }
        catch (Exception ex) { model.ReportError("无法选择素材：" + ex.Message, ex); }
        finally { _picking = false; }
    }

    private MediaSide DropSide(DragEventArgs e) => e.GetPosition(DropSurface).X < DropSurface.Bounds.Width / 2 ? MediaSide.A : MediaSide.B;

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var accepted = !_picking && _attached is { CanOperate: true } && e.DragEffects.HasFlag(DragDropEffects.Copy)
            && GetDroppedFile(e.DataTransfer) is not null;
        e.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        var side = DropSide(e);
        DropIndicatorA.IsVisible = accepted && side == MediaSide.A;
        DropIndicatorB.IsVisible = accepted && side == MediaSide.B;
        DropLabelA.Text = _attached?.HasA == true ? "释放以替换 A" : "释放以打开 A";
        DropLabelB.Text = _attached?.HasB == true ? "释放以替换 B" : "释放以打开 B";
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        ClearDropFeedback();
        e.Handled = true;
        var canCopy = e.DragEffects.HasFlag(DragDropEffects.Copy);
        e.DragEffects = DragDropEffects.None;
        if (!canCopy || _picking || _attached is not { CanOperate: true } model) return;
        if (GetDroppedFile(e.DataTransfer) is not { } path) return;
        var side = DropSide(e);
        e.DragEffects = DragDropEffects.Copy;
        Preview.Focus();
        await model.OpenAsync(side, path, false);
    }

    private void ClearDropFeedback() => DropIndicatorA.IsVisible = DropIndicatorB.IsVisible = false;

    private static string? GetDroppedFile(IDataTransfer data)
    {
        IStorageItem[]? files = null;
        try
        {
            files = data.TryGetFiles()?.ToArray();
            if (files is not { Length: 1 } || files[0] is not IStorageFile file) return null;
            var path = file.TryGetLocalPath();
            return path is not null && Path.IsPathFullyQualified(path) ? path : null;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException) { return null; }
        finally
        {
            // Virtual shell files can own an OLE transfer even when rejected for
            // lacking a local path. Release every returned item, including extras.
            if (files is not null) foreach (var file in files) file.Dispose();
        }
    }

    private void FitView(object? sender, RoutedEventArgs e) => Preview.ResetView();
    private async void ClearSource(object? sender, RoutedEventArgs e)
    {
        if (_attached is not { } model) return;
        var side = sender is Button { Tag: "B" } ? MediaSide.B : MediaSide.A;
        SourceAOptions.Flyout?.Hide(); SourceBOptions.Flyout?.Hide();
        Preview.Focus();
        await model.ClearAsync(side);
    }
    private void FocusPreview(object? sender, RoutedEventArgs e) => Preview.Focus();
    private async void AudioChanged(object? sender, SelectionChangedEventArgs e)
    { if (IsLoaded && _attached is { } model) await model.SetAudioAsync(AudioChoice.SelectedIndex); }

    private bool IsPlaybackKey(KeyEventArgs e) => e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None && !_picking && !AudioChoice.IsDropDownOpen &&
        SourceAOptions.Flyout?.IsOpen != true && SourceBOptions.Flyout?.IsOpen != true && HelpButton.Flyout?.IsOpen != true &&
        !(e.Source is Visual source && source.GetSelfAndVisualAncestors().Any(x => x is TextBox or NumericUpDown or ComboBox or ComboBoxItem or MenuItem));

    private void OnPlaybackKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsPlaybackKey(e) || _attached is not { HasTimeline: true } model) return;
        // Reserve Space before a focused button handles it; Enter still activates buttons.
        e.Handled = true;
        if (_playbackSpacePressed) return;
        _playbackSpacePressed = true;
        _playbackSpaceArmed = model.TogglePlayCommand.CanExecute(null);
    }

    private async void OnPlaybackKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;
        var armed = _playbackSpaceArmed;
        var pressed = _playbackSpacePressed;
        ResetPlaybackKey();
        if (!pressed || !IsPlaybackKey(e)) return;
        e.Handled = true;
        // One activation on release avoids repeated toggles while Space is held down.
        if (armed && _attached is { } model && model.TogglePlayCommand.CanExecute(null))
        {
            // The async command temporarily disables its button; keep keyboard focus in this page.
            if (PlayButton.IsKeyboardFocusWithin) Preview.Focus();
            await model.TogglePlayCommand.ExecuteAsync(null);
        }
    }

    private void ResetPlaybackKey() => _playbackSpacePressed = _playbackSpaceArmed = false;
}
