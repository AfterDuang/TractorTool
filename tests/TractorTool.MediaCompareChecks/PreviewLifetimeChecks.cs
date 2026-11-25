using System.Runtime.CompilerServices;
using Avalonia.Threading;
using TractorTool.Plugins.MediaCompare.Views;

internal static class PreviewLifetimeChecks
{
    // Runs on the existing diagnostic UI thread without creating a renderer or injecting a driver fault.
    internal static async Task RunAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        var lifetime = new PreviewLifetime();
        var expected = new IOException("Synthetic cleanup failure.");
        var retired = RetireFailedPreview(lifetime, expected);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        if (retired.TryGetTarget(out _))
            throw new InvalidOperationException("A failed but retired preview is still retained by the plugin lifetime.");
        GC.KeepAlive(lifetime);

        lifetime.RecordCleanupFailure(new IOException("Later cleanup failure."));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try { await lifetime.DisposeAsync(); }
            catch (InvalidOperationException error) when (ReferenceEquals(error.InnerException, expected)) { continue; }
            throw new InvalidOperationException("Plugin stop lost the first retired-preview cleanup failure.");
        }
        if (lifetime.Register(new ComparePreview()))
            throw new InvalidOperationException("A failed stop must still close preview registration.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<ComparePreview> RetireFailedPreview(PreviewLifetime lifetime, Exception error)
    {
        var preview = new ComparePreview { Lifetime = lifetime };
        if (!lifetime.Register(preview)) throw new InvalidOperationException("Preview registration failed before stop.");
        lifetime.RecordCleanupFailure(error);
        lifetime.Retired(preview);
        return new(preview);
    }
}
