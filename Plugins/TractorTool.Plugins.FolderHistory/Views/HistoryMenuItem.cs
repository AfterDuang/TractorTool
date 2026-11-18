using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using FluentAvalonia.UI.Controls;

namespace TractorTool.Plugins.FolderHistory.Views;

internal sealed class HistoryMenuItem(FAMenuFlyout menu) : FAMenuFlyoutItem
{
    protected override Type StyleKeyOverride => typeof(FAMenuFlyoutItem);
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this, menu);

    private sealed class Peer(HistoryMenuItem owner, FAMenuFlyout menu) : ControlAutomationPeer(owner), IInvokeProvider
    {
        private HistoryMenuItem Item => (HistoryMenuItem)Owner;

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.MenuItem;
        protected override string GetNameCore() => Item.Text;

        public void Invoke()
        {
            EnsureEnabled();
            if (!Item.IsLoaded || !menu.IsOpen) throw new ElementNotEnabledException();
            Item.OnClick();
            menu.Hide();
        }
    }
}
