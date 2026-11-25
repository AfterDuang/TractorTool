using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using TractorTool.Plugins.MediaCompare.Media;
using TractorTool.Plugins.MediaCompare.Rendering;

namespace TractorTool.Plugins.MediaCompare.Views;

/// <summary>Atomic A/B preview. Submit transfers ownership, and may be called from a decoder thread.</summary>
public sealed class ComparePreview : Control, IAsyncDisposable
{
    private readonly object _pendingGate = new();
    private FramePair? _pending;
    private bool _acceptFrames;
    private CompositionPreviewSession? _session;
    private CompositionSurfaceVisual? _visual;
    private TopLevel? _topLevel;
    private PreviewView _view = PreviewView.Fit;
    private Size _imageA, _imageB;
    private bool _attached, _pumping, _dirty, _failed;
    private bool _stopped;
    private Task _transition = Task.CompletedTask;
    private Task _presentation = Task.CompletedTask;
    private Task? _stopTask;
    private Exception? _cleanupError;
    private int _generation, _wakeQueued;
    private Point? _dragStart;
    private PreviewView _dragView;
    private Size _dragImage;
    private double _dragViewportWidth;
    private string? _rendererStatus;

    public event Action<string>? Error;
    public event Action<string>? RendererStatusChanged;
    public event Action<bool>? AvailabilityChanged;
    public bool IsAvailable => _attached && !_failed && _session is not null;
    internal PreviewLifetime? Lifetime { get; set; }

    public ComparePreview()
    {
        ClipToBounds = true;
        Focusable = true;
        SizeChanged += (_, _) => Redraw();
    }

    public void Submit(FramePair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        FramePair? previous;
        lock (_pendingGate)
        {
            if (!_acceptFrames) { previous = pair; }
            else { previous = _pending; _pending = pair; }
        }
        previous?.Dispose();
        Wake();
    }

    public void ResetView()
    {
        Dispatcher.UIThread.VerifyAccess();
        _view = PreviewView.Fit; Redraw();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_stopped || Lifetime?.Register(this) == false) return;
        _attached = true; _failed = false; _rendererStatus = null;
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null) _topLevel.ScalingChanged += OnScalingChanged;
        lock (_pendingGate) _acceptFrames = true;
        _transition = InitializeAsync(_transition, ++_generation);
    }

    private async Task InitializeAsync(Task previous, int generation)
    {
        CompositionPreviewSession? created = null;
        try
        {
            // A replacement surface starts only after the preceding generation has retired.
            await previous;
            if (!_attached || generation != _generation) return;
            var compositor = ElementComposition.GetElementVisual(this)?.Compositor
                ?? throw new InvalidOperationException("媒体预览尚未连接到窗口。");
            created = await CompositionPreviewSession.CreateAsync(compositor);
            if (!_attached || generation != _generation) { await ReleaseSessionAsync(created); return; }
            _session = created;
            _visual = compositor.CreateSurfaceVisual();
            _visual.Surface = created.Surface;
            _visual.Size = new Vector(Bounds.Width, Bounds.Height);
            ElementComposition.SetElementChildVisual(this, _visual);
            SetStatus("GPU 预览 · A/B 同帧合成");
            Redraw();
            if (_attached && generation == _generation) AvailabilityChanged?.Invoke(true);
        }
        catch (Exception ex)
        {
            if (created is not null)
            {
                if (ReferenceEquals(_session, created)) DetachSurface();
                await ReleaseSessionAsync(created);
            }
            if (_attached && generation == _generation) ReportFailure(ex);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Deactivate();
        base.OnDetachedFromVisualTree(e);
    }

    private void Deactivate()
    {
        _attached = false;
        var generation = ++_generation;
        if (_topLevel is not null) _topLevel.ScalingChanged -= OnScalingChanged;
        _topLevel = null;
        DropPending();
        _dragStart = null;
        _imageA = _imageB = default;
        var session = DetachSurface();
        _transition = RetireAsync(_transition, _presentation, session, generation);
        AvailabilityChanged?.Invoke(false);
    }

    private CompositionPreviewSession? DetachSurface()
    {
        ElementComposition.SetElementChildVisual(this, null);
        _visual = null;
        var session = _session; _session = null;
        return session;
    }

    private async Task RetireAsync(Task previous, Task presentation, CompositionPreviewSession? session, int generation)
    {
        try { await Task.WhenAll(previous, presentation); }
        catch (Exception ex) { RecordCleanupFailure(ex); }
        if (session is not null) await ReleaseSessionAsync(session);
        if (!_attached && generation == _generation) Lifetime?.Retired(this);
    }

    private async Task ReleaseSessionAsync(CompositionPreviewSession session)
    {
        try { await session.DisposeAsync(); }
        catch (Exception ex) { RecordCleanupFailure(ex); }
    }

    private void RecordCleanupFailure(Exception error)
    {
        _cleanupError ??= error;
        Lifetime?.RecordCleanupFailure(error);
        try { Error?.Invoke($"释放媒体预览资源失败：{error.Message}"); }
        catch { /* Reporting cannot interrupt the remaining GPU cleanup; StopAsync rethrows the failure. */ }
    }

    private void DropPending()
    {
        FramePair? pair;
        lock (_pendingGate) { _acceptFrames = false; pair = _pending; _pending = null; }
        pair?.Dispose();
    }

    private void Fail(Exception error)
    {
        var session = DetachSurface();
        _transition = RetireAsync(_transition, _presentation, session, _generation);
        ReportFailure(error);
    }

    private void ReportFailure(Exception error)
    {
        _failed = true; DropPending();
        AvailabilityChanged?.Invoke(false);
        SetStatus("GPU 预览不可用");
        Error?.Invoke(error.Message);
    }

    private void OnScalingChanged(object? sender, EventArgs e) => Redraw();

    private void SetStatus(string value)
    {
        if (_rendererStatus == value) return;
        _rendererStatus = value; RendererStatusChanged?.Invoke(value);
    }

    private void Redraw()
    {
        _dirty = true;
        if (_visual is not null) _visual.Size = new Vector(Bounds.Width, Bounds.Height);
        Wake();
    }

    private void Wake()
    {
        if (Interlocked.Exchange(ref _wakeQueued, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _wakeQueued, 0);
            if (!_pumping) _presentation = PumpAsync();
        }, DispatcherPriority.Render);
    }

    private async Task PumpAsync()
    {
        if (_pumping || !_attached || _failed || _session is not { } session || !HasDrawableBounds()) return;
        _pumping = true;
        try
        {
            while (_attached && !_failed && ReferenceEquals(_session, session))
            {
                var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
                var pixels = PixelSize.FromSize(Bounds.Size, scale);
                if (pixels.Width < 2 || pixels.Height < 1) break;
                FramePair? pair;
                lock (_pendingGate) { pair = _pending; _pending = null; }
                if (pair is null && !_dirty) break;
                _dirty = false;
                try
                {
                    await session.PresentAsync(pair, pixels.Width, pixels.Height, _view);
                    if (pair is not null && ReferenceEquals(_session, session))
                    { _imageA = ImageSize(pair.A); _imageB = ImageSize(pair.B); }
                }
                finally { pair?.Dispose(); }
            }
        }
        catch (Exception ex)
        {
            if (_attached && ReferenceEquals(_session, session)) Fail(ex);
        }
        finally
        {
            _pumping = false;
            if (_attached && !_failed && HasDrawableBounds())
            {
                bool pending;
                lock (_pendingGate) pending = _pending is not null;
                if (pending || _dirty) Wake();
            }
        }
    }

    private static Size ImageSize(MediaFrame? image) => image is null ? default : new(image.DisplayWidth, image.DisplayHeight);

    public ValueTask DisposeAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        _stopped = true;
        return new(_stopTask ??= StopAsync());
    }

    private async Task StopAsync()
    {
        Deactivate();
        await _transition;
        if (_cleanupError is not null) throw new InvalidOperationException("释放媒体预览资源失败。", _cleanupError);
    }

    private bool HasDrawableBounds()
    {
        var pixels = PixelSize.FromSize(Bounds.Size, _topLevel?.RenderScaling ?? 1);
        return pixels.Width >= 2 && pixels.Height >= 1;
    }

    // Drawing a background also makes the otherwise composition-only control hit-testable.
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var point = e.GetPosition(this);
        var (image, viewportWidth, localX) = GetActiveImage(point);
        if (image.Width == 0 || image.Height == 0 || viewportWidth <= 0) return;
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        _view = _view.ZoomAt(image.Width, image.Height, viewportWidth, Bounds.Height * scale,
            localX, point.Y * scale, Math.Pow(1.15, e.Delta.Y));
        Redraw(); e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        if (e.ClickCount == 2) { ResetView(); e.Handled = true; return; }
        var point = e.GetPosition(this);
        var (image, width, _) = GetActiveImage(point);
        if (image.Width == 0 || image.Height == 0) return;
        _dragStart = point; _dragView = _view; _dragImage = image; _dragViewportWidth = width;
        e.Pointer.Capture(this); e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragStart is not { } start) return;
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var delta = e.GetPosition(this) - start;
        _view = _dragView.Pan(_dragImage.Width, _dragImage.Height, _dragViewportWidth,
            Bounds.Height * scale, delta.X * scale, delta.Y * scale);
        Redraw(); e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragStart is null) return;
        _dragStart = null; e.Pointer.Capture(null); e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    { _dragStart = null; base.OnPointerCaptureLost(e); }

    private (Size Image, double Width, double X) GetActiveImage(Point point)
    {
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var pixels = PixelSize.FromSize(Bounds.Size, scale);
        var half = pixels.Width / 2;
        var x = point.X * scale;
        return x < half ? (_imageA, half, x) : (_imageB, pixels.Width - half, x - half);
    }
}
