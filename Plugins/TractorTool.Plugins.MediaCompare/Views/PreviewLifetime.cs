using Avalonia.Threading;

namespace TractorTool.Plugins.MediaCompare.Views;

/// <summary>Tracks attached and retiring previews until their GPU work has finished.</summary>
internal sealed class PreviewLifetime : IAsyncDisposable
{
    private readonly HashSet<ComparePreview> _previews = [];
    private Task? _stopTask;
    private bool _stopping;
    private Exception? _cleanupError;

    internal bool Register(ComparePreview preview)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_stopping) return false;
        _previews.Add(preview);
        return true;
    }

    internal void Retired(ComparePreview preview)
    {
        Dispatcher.UIThread.VerifyAccess();
        _previews.Remove(preview);
    }

    internal void RecordCleanupFailure(Exception error)
    {
        Dispatcher.UIThread.VerifyAccess();
        _cleanupError ??= error;
    }

    public ValueTask DisposeAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        _stopping = true;
        return new(_stopTask ??= StopAsync());
    }

    private async Task StopAsync()
    {
        var previews = _previews.ToArray();
        try { await Task.WhenAll(previews.Select(preview => preview.DisposeAsync().AsTask())); }
        catch (Exception error) { RecordCleanupFailure(error); }
        finally { _previews.Clear(); }
        if (_cleanupError is not null) throw new InvalidOperationException("释放媒体预览资源失败。", _cleanupError);
    }
}
