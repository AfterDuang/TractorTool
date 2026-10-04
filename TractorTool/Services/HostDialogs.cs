using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;

namespace TractorTool.Services;

/// <summary>One dialog queue for plugin changes, dependency consent and cleanup.</summary>
public sealed class HostDialogs(Window owner) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _closed = new();
    public async Task<bool> ConfirmAsync(string title, string message, CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _closed.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                linked.Token.ThrowIfCancellationRequested();
                if (!owner.IsEnabled) return false;
                owner.Show();
                if (owner.WindowState == WindowState.Minimized) owner.WindowState = WindowState.Normal;
                owner.Activate();
                var dialog = new FAContentDialog
                {
                    Title = title, Content = new ScrollViewer { MaxHeight = 380,
                        Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap } },
                    PrimaryButtonText = "确认", CloseButtonText = "取消", DefaultButton = FAContentDialogButton.Close
                };
                // Register after ShowAsync has mounted the dialog, so cancellation cannot hide it too early.
                var result = dialog.ShowAsync(owner);
                using var registration = linked.Token.Register(() => Dispatcher.UIThread.Post(() => dialog.Hide()));
                var accepted = await result == FAContentDialogResult.Primary;
                linked.Token.ThrowIfCancellationRequested();
                return accepted;
            });
        }
        finally { _gate.Release(); }
    }
    public void Dispose() => _closed.Cancel();
}
