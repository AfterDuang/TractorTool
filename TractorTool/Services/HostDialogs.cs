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
    public Task<bool> ConfirmAsync(string title, string message, CancellationToken token = default) =>
        ShowAsync(title, message, "确认", "取消", token);
    public Task<bool> ConfirmExitAsync(int count, CancellationToken token) => ShowAsync("仍有任务未完成",
        $"还有 {count} 个任务等待或正在处理。退出会停止这些任务；关闭窗口到托盘可以继续处理。",
        "停止任务并退出", "继续处理", token);
    private async Task<bool> ShowAsync(string title, string message, string accept, string cancel, CancellationToken token)
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
                    PrimaryButtonText = accept, CloseButtonText = cancel, DefaultButton = FAContentDialogButton.Close
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
