using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.Core;
using FluentAvalonia.UI.Controls;

namespace TractorTool.UI.Controls;

/// <summary>Synchronously presents registered pages; owns neither page cleanup nor background work.</summary>
public sealed class PageHost : Decorator, IFANavigationPageFactory
{
    /// <summary>UI API 1.4. Raised directly on the current page before replacing it.</summary>
    public static readonly RoutedEvent<CancelRoutedEventArgs> NavigatingFromEvent =
        RoutedEvent.Register<PageHost, CancelRoutedEventArgs>("NavigatingFrom", RoutingStrategies.Direct);

    private static readonly TransformOperations EntranceOffset = TransformOperations.Parse("translate(0px, 18px)");
    private Border _presenter = new();
    private RenderTargetBitmap? _previousFrame;

    public PageHost()
    {
        ClipToBounds = true;
        // FAFrame 3.1.0 retains detached frames through TopLevel.BackRequested.
        // This host needs only one page, not a window subscription or navigation history.
        Child = _presenter;
        _presenter.PropertyChanged += PresenterChanged;
    }

    /// <returns>False when the current page cancels navigation; it remains displayed.</returns>
    public bool Present(Control page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.Parent is not null || page.GetVisualParent() is not null)
            throw new InvalidOperationException("页面工厂必须返回尚未挂载的独立页面。");

        RenderTargetBitmap? frame = null;
        try
        {
            var navigating = new CancelRoutedEventArgs(NavigatingFromEvent);
            _presenter.Child?.RaiseEvent(navigating);
            if (navigating.Cancel) return false;

            var presenter = _presenter;
            var animate = FAUISettings.AreAnimationsEnabled();
            frame = animate ? CaptureFrame() : null;
            ReleaseFrame();
            presenter.Transitions = null;
            presenter.Child = page;
            presenter.Opacity = animate ? 0 : 1;
            presenter.RenderTransform = animate ? EntranceOffset : TransformOperations.Identity;
            _previousFrame = frame;
            frame = null;
            InvalidateVisual();
            if (animate)
            {
                void StartEntrance()
                {
                    if (!ReferenceEquals(_presenter, presenter) || !ReferenceEquals(presenter.Child, page)) return;
                    if (FAUISettings.AreAnimationsEnabled())
                        presenter.Transitions = new Transitions
                        {
                            new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(500), Easing = new SplineEasing(0.1, 0.9, 0.2, 1) },
                            new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(500), Easing = new SplineEasing(0.1, 0.9, 0.2, 1) }
                        };
                    presenter.Opacity = 1;
                    presenter.RenderTransform = TransformOperations.Identity;
                }
                // Start attached transitions before yielding to rendering; do not queue a hidden, idle frame.
                if (TopLevel.GetTopLevel(presenter) is not null) StartEntrance();
                else Dispatcher.UIThread.Post(StartEntrance, DispatcherPriority.Render);
            }
            return true;
        }
        catch (Exception error)
        {
            if (ReplacePresenter() is { } recoveryError)
                throw new AggregateException("页面呈现及旧页面脱离失败；导航容器已重建。", error, recoveryError);
            throw;
        }
        finally { frame?.Dispose(); }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_previousFrame is { } frame)
            context.DrawImage(frame, new Rect(Bounds.Size));
    }

    private RenderTargetBitmap? CaptureFrame()
    {
        if (_presenter.Child is null || Bounds.Width <= 0 || Bounds.Height <= 0 ||
            TopLevel.GetTopLevel(this) is not { } root) return null;
        var frame = new RenderTargetBitmap(PixelSize.FromSize(Bounds.Size, root.RenderScaling),
            new Vector(96, 96) * root.RenderScaling);
        // Capture pixels before leaving; plugin page resources may be disposed as soon as Present returns.
        try { frame.Render(this); return frame; }
        catch { frame.Dispose(); throw; }
    }

    private void PresenterChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.OpacityProperty && _presenter.Opacity >= 1) ReleaseFrame();
    }

    private void ReleaseFrame()
    {
        if (_previousFrame is not { } frame) return;
        _previousFrame = null;
        frame.Dispose();
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ReleaseFrame();
        base.OnDetachedFromVisualTree(e);
    }

    public void Clear()
    {
        if (ReplacePresenter() is { } error) throw error;
    }

    private Exception? ReplacePresenter()
    {
        ReleaseFrame();
        var retired = _presenter;
        retired.PropertyChanged -= PresenterChanged;
        retired.Transitions = null;
        var errors = new List<Exception>();
        try { retired.Child = null; }
        catch (Exception error) { errors.Add(error); }
        try { Child = null; }
        catch (Exception error) { errors.Add(error); }
        _presenter = new Border();
        _presenter.PropertyChanged += PresenterChanged;
        // A faulty plugin must not prevent the host from presenting its error page.
        try { Child = _presenter; }
        catch (Exception error) { errors.Add(error); }
        return errors.Count == 0 ? null : new AggregateException("旧页面脱离失败。", errors);
    }

    // Retained for binary compatibility with previously published UI SDKs.
    public Control GetPage(Type sourceType) => throw new NotSupportedException("使用注册的页面工厂。");
    public Control GetPageFromObject(object target) => (Control)target;
}
