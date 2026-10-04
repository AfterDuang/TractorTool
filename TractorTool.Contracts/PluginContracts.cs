namespace TractorTool.Contracts;

public static class PluginApi
{
    public const string Version = "1.4";
}

public interface IPlugin : IAsyncDisposable
{
    ValueTask InitializeAsync(PluginContext context, CancellationToken cancellationToken);
    ValueTask StopAsync(CancellationToken cancellationToken);
}

/// <summary>Optional application-lifetime activity; never owned or cancelled by a page.</summary>
public interface IPluginBackgroundService
{
    Task RunAsync(CancellationToken stoppingToken);
}

public sealed record PluginContext(string PluginId, IAppPaths Paths,
    IPluginSettingsStore Settings, IAppLog Log, CancellationToken ApplicationStopping)
{
    // Keep the original constructor/deconstruction ABI for independently built API 1.0 plugins.
    // Plugins using this service declare API 1.1; hand-built test contexts may leave it absent.
    public Processes.IProcessRunner? Processes { get; init; }
    // API 1.2; host-owned downloads, plugin-owned usage leases.
    public Packages.IExternalPackageService? Packages { get; init; }
}

public interface IAppPaths
{
    string AppRoot { get; }
    string PluginRoot { get; }
    string DataRoot { get; }
    string PackageRoot { get; }
    string GetPluginDataDirectory(string pluginId);
}

public interface IVersionedSettings
{
    int SchemaVersion { get; }
}

/// <summary>Bound to one stable plugin ID. Invalid files are preserved and reported.</summary>
public interface IPluginSettingsStore
{
    T Load<T>(string name) where T : IVersionedSettings, new();
    void Save<T>(string name, T value) where T : IVersionedSettings, new();
}

public interface IAppLog
{
    void Write(string source, string message, Exception? error = null);
}
