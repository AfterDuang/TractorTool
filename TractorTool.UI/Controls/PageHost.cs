using Avalonia.Controls;
using Avalonia.VisualTree;
using FluentAvalonia.Core;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Media.Animation;
using FluentAvalonia.UI.Navigation;

namespace TractorTool.UI.Controls;

/// <summary>Synchronously presents registered pages; owns neither page cleanup nor background work.</summary>
public sealed class PageHost : Decorator, IFANavigationPageFactory
{
    private FAFrame _frame;
    private Exception? _navigationError;

    public PageHost()
    {
        ClipToBounds = true;
        Child = _frame = CreateFrame();
    }

    /// <returns>False when the current page cancels navigation; it remains displayed.</returns>
    public bool Present(Control page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.Parent is not null || page.GetVisualParent() is not null)
            throw new InvalidOperationException("页面工厂必须返回尚未挂载的独立页面。");

        _navigationError = null;
        try
        {
            var succeeded = _frame.NavigateFromObject(page, new FAFrameNavigationOptions
            {
                IsNavigationStackEnabled = false,
                TransitionInfoOverride = FAUISettings.AreAnimationsEnabled()
                    ? new FAEntranceNavigationTransitionInfo { FromVerticalOffset = 18 }
                    : new FASuppressNavigationTransitionInfo()
            });
            if (_navigationError is { } error)
                throw new InvalidOperationException("页面呈现失败。", error);
            return succeeded;
        }
        catch (Exception error)
        {
            // FAFrame can change its private CurrentEntry before a page throws. Do not
            // reuse that navigation state: a fresh frame lets the host show its error page.
            if (ReplaceFrame() is { } recoveryError)
                throw new AggregateException("页面呈现及旧页面脱离失败；导航容器已重建。", error, recoveryError);
            throw;
        }
    }

    public void Clear()
    {
        if (ReplaceFrame() is { } error) throw error;
    }

    private FAFrame CreateFrame()
    {
        var frame = new FAFrame { CacheSize = 0, IsNavigationStackEnabled = false, NavigationPageFactory = this };
        frame.NavigationFailed += (_, args) => _navigationError = args.Exception;
        return frame;
    }

    private Exception? ReplaceFrame()
    {
        var retired = _frame;
        var errors = new List<Exception>();
        try { retired.Content = null; }
        catch (Exception error) { errors.Add(error); }
        try { Child = null; }
        catch (Exception error) { errors.Add(error); }
        _frame = CreateFrame();
        // Install the fresh frame even if a faulty plugin throws while being detached.
        try { Child = _frame; }
        catch (Exception error) { errors.Add(error); }
        return errors.Count == 0 ? null : new AggregateException("旧页面脱离失败。", errors);
    }

    public Control GetPage(Type sourceType) => throw new NotSupportedException("使用注册的页面工厂。");
    public Control GetPageFromObject(object target) => (Control)target;
}
