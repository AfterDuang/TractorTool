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
        WireDrop(SourceA, MediaSide.A); WireDrop(SourceB, MediaSide.B);
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
        Timeline.KeyDown += (_, e) =>
        {
            if (_attached is not { } model) return;
            if (e.Key is Key.Home or Key.End)
            { e.Handled = true; model.RequestSeek(e.Key == Key.Home ? 0 : model.DurationSeconds); }
            else if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown)
            {
                e.Handled = true;
                var change = e.Key is Key.PageUp or Key.PageDown ? 1 : .1;
                model.RequestSeek(model.PositionSeconds + (e.Key is Key.Left or Key.Down or Key.PageDown ? -change : change));
            }
        };
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
        Preview.Focus();
        await model.EnterViewAsync();
    }

    public void OnNavigatedFrom() => Detach();
    private void Detach()
    {
        ResetPlaybackKey();
        if (_attached is not { } model) return;
        _attached = null;
        model.FrameReady -= Preview.Submit;
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

    private void WireDrop(Control zone, MediaSide side)
    {
        zone.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = _attached is { CanOperate: true } && GetDroppedFile(e.DataTransfer) is not null
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        });
        zone.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            e.Handled = true; e.DragEffects = DragDropEffects.None;
            if (_attached is not { CanOperate: true } model || GetDroppedFile(e.DataTransfer) is not { } path) return;
            e.DragEffects = DragDropEffects.Copy;
            Preview.Focus();
            await model.OpenAsync(side, path, false);
        });
    }

    private static string? GetDroppedFile(IDataTransfer data)
    {
        try
        {
            var files = data.TryGetFiles()?.Take(2).ToArray();
            if (files is not { Length: 1 } || files[0] is not IStorageFile file) return null;
            var path = file.TryGetLocalPath();
            return path is not null && Path.IsPathFullyQualified(path) ? path : null;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException) { return null; }
    }

    private void FitView(object? sender, RoutedEventArgs e) => Preview.ResetView();
    private void FillView(object? sender, RoutedEventArgs e) => Preview.FillView();
    private void ActualSize(object? sender, RoutedEventArgs e) => Preview.ActualSize();
    private void FocusPreview(object? sender, RoutedEventArgs e) => Preview.Focus();
    private async void AudioChanged(object? sender, SelectionChangedEventArgs e)
    { if (IsLoaded && _attached is { } model) await model.SetAudioAsync(AudioChoice.SelectedIndex); }

    private bool IsPlaybackKey(KeyEventArgs e) => e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None && !_picking && !AudioChoice.IsDropDownOpen &&
        SourceAOptions.Flyout?.IsOpen != true && SourceBOptions.Flyout?.IsOpen != true && HelpButton.Flyout?.IsOpen != true &&
        !(e.Source is Visual source && source.GetSelfAndVisualAncestors().Any(x => x is TextBox or NumericUpDown or ComboBox or ComboBoxItem or MenuItem));

    private void OnPlaybackKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsPlaybackKey(e) || _attached is not { } model) return;
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
