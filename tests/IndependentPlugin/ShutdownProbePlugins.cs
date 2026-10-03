using Avalonia.Controls;
using TractorTool.Contracts;
using TractorTool.UI;

namespace IndependentPlugin;

/// <summary>Real DLL lifecycle fixtures controlled only through their isolated plugin data directories.</summary>
public class ShutdownProbePlugin : IPlugin, IPluginBackgroundService, IPluginUiModule
{
    private PluginContext _context = null!;
    private string _data = "";
    private CancellationTokenRegistration _registration;
    protected virtual bool ThrowOnCancellation => false;
    protected virtual string? DelayStage => null;

    public async ValueTask InitializeAsync(PluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _data = context.Paths.GetPluginDataDirectory(context.PluginId);
        Directory.CreateDirectory(_data);
        Mark("initialize-started");
        if (ThrowOnCancellation)
            _registration = context.ApplicationStopping.Register(() =>
            {
                Mark("cancellation-callback");
                throw new InvalidOperationException("fixture cancellation callback failed");
            });
        if (DelayStage == "initialize") await WaitForReleaseAsync("initialize");
        Mark("initialize-completed");
    }

    public void RegisterPages(IPluginPageRegistry registry)
    {
        Mark("registered");
        registry.AddNavigationPage("lifecycle", "生命周期检查", () => new TextBlock { Text = "生命周期检查夹具" });
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        Mark("background-started");
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            Mark("background-stopping");
            if (DelayStage == "background") await WaitForReleaseAsync("background");
            Mark("background-completed");
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        if (!Has("initialize-completed")) Mark("stop-before-initialize-completed");
        Mark("stop-started");
        // Deliberately ignore the deadline token so the host must not dispose a still-running Stop.
        if (DelayStage == "stop") await WaitForReleaseAsync("stop");
        Mark("stop-completed");
    }

    public ValueTask DisposeAsync()
    {
        if (!Has("initialize-completed") || !Has("stop-completed") ||
            (Has("background-started") && !Has("background-completed"))) Mark("disposed-too-early");
        _registration.Dispose();
        Mark("disposed");
        return ValueTask.CompletedTask;
    }

    private bool Has(string name) => File.Exists(Path.Combine(_data, name + ".marker"));
    private void Mark(string name)
    {
        File.WriteAllText(Path.Combine(_data, name + ".marker"), "");
        _context.Log.Write(_context.PluginId, "shutdown-fixture:" + name);
    }
    private async Task WaitForReleaseAsync(string stage)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (!Has("release-" + stage))
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Fixture gate was not released: " + stage);
            await Task.Delay(10);
        }
    }
}

public sealed class ThrowingCancellationPlugin : ShutdownProbePlugin
{
    protected override bool ThrowOnCancellation => true;
}
public sealed class DelayedStopPlugin : ShutdownProbePlugin
{
    protected override string DelayStage => "stop";
}
public sealed class DelayedBackgroundPlugin : ShutdownProbePlugin
{
    protected override string DelayStage => "background";
}
public sealed class DelayedInitializationPlugin : ShutdownProbePlugin
{
    protected override string DelayStage => "initialize";
}
