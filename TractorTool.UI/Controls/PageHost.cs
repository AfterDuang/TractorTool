using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Interactivity;
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

    public PageHost()
    {
        ClipToBounds = true;
        // FAFrame 3.1.0 retains detached frames through TopLevel.BackRequested.
        // This host needs only one page, not a window subscription or navigation history.
        Child = _presenter;
    }

    /// <returns>False when the current page cancels navigation; it remains displayed.</returns>
    public bool Present(Control page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.Parent is not null || page.GetVisualParent() is not null)
            throw new InvalidOperationException("页面工厂必须返回尚未挂载的独立页面。");

        try
        {
            var navigating = new CancelRoutedEventArgs(NavigatingFromEvent);
            _presenter.Child?.RaiseEvent(navigating);
            if (navigating.Cancel) return false;

            var presenter = _presenter;
            var animate = FAUISettings.AreAnimationsEnabled();
            presenter.Transitions = null;
            presenter.Opacity = animate ? 0 : 1;
            presenter.RenderTransform = animate ? EntranceOffset : TransformOperations.Identity;
            presenter.Child = page;
            if (animate)
            {
                // Start after layout. Stale callbacks must not animate a replacement page.
                Dispatcher.UIThread.Post(() =>
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
                }, DispatcherPriority.Render);
            }
            return true;
        }
        catch (Exception error)
        {
            if (ReplacePresenter() is { } recoveryError)
                throw new AggregateException("页面呈现及旧页面脱离失败；导航容器已重建。", error, recoveryError);
            throw;
        }
    }

    public void Clear()
    {
        if (ReplacePresenter() is { } error) throw error;
    }

    private Exception? ReplacePresenter()
    {
        var retired = _presenter;
        retired.Transitions = null;
        var errors = new List<Exception>();
        try { retired.Child = null; }
        catch (Exception error) { errors.Add(error); }
        try { Child = null; }
        catch (Exception error) { errors.Add(error); }
        _presenter = new Border();
        // A faulty plugin must not prevent the host from presenting its error page.
        try { Child = _presenter; }
        catch (Exception error) { errors.Add(error); }
        return errors.Count == 0 ? null : new AggregateException("旧页面脱离失败。", errors);
    }

    // Retained for binary compatibility with previously published UI SDKs.
    public Control GetPage(Type sourceType) => throw new NotSupportedException("使用注册的页面工厂。");
    public Control GetPageFromObject(object target) => (Control)target;
}
