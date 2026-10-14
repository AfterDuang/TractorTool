using System.Text.RegularExpressions;
using TractorTool.Contracts;

namespace TractorTool.Core.Storage;

public sealed record BootstrapSettings : IVersionedSettings
{
    public int SchemaVersion { get; init; } = 1;
    public string? DataRoot { get; init; }
    public DataMigrationRequest? Migration { get; init; }
    public string? PreviousDataRoot { get; init; }
    public static void Validate(BootstrapSettings value)
    {
        if (value.DataRoot is { } root) _ = AppPaths.Absolute(root);
        if (value.PreviousDataRoot is { } previous) _ = AppPaths.Absolute(previous);
        if (value.Migration is { } migration) DataMigrationRequest.Validate(migration);
    }
}

public sealed class AppPaths : IAppPaths
{
    public string AppRoot { get; }
    public string PluginRoot => Path.Combine(AppRoot, "Plugins");
    public string BootstrapRoot { get; }
    public string BootstrapFile => Path.Combine(BootstrapRoot, "bootstrap.json");
    public string DataRoot { get; }
    public string ConfigRoot => Path.Combine(DataRoot, "Config");
    public string LogRoot => Path.Combine(DataRoot, "Logs");
    public string PackageRoot => Path.Combine(DataRoot, "Packages");
    public string? MissingDataRoot { get; private init; }
    private bool _dataResetCompleted;

    // Production calls use AppContext.BaseDirectory. Explicit roots also make isolation testable.
    public AppPaths(string appRoot, string bootstrapRoot, string? dataRoot = null)
    {
        AppRoot = Absolute(appRoot);
        BootstrapRoot = Absolute(bootstrapRoot);
        DataRoot = Absolute(dataRoot ?? bootstrapRoot);
        if (Path.GetDirectoryName(DataRoot) is null)
            throw new InvalidDataException("数据目录不能是卷根目录，请选择专用子目录。");
        if (Contains(PluginRoot, DataRoot) || Contains(DataRoot, PluginRoot))
            throw new InvalidDataException("数据目录不能与软件 Plugins 目录互相包含。");
    }

    public static AppPaths Open(string? testProfileRoot = null, bool ensureDirectories = true)
    {
        var fixedRoot = testProfileRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TractorTool");
        fixedRoot = Absolute(fixedRoot);
        DataDirectories.CheckPath(fixedRoot);
        Directory.CreateDirectory(fixedRoot);
        DataDirectories.CheckPath(Path.Combine(fixedRoot, "bootstrap.json"));
        var store = new JsonFileStore<BootstrapSettings>(Path.Combine(fixedRoot, "bootstrap.json"), BootstrapSettings.Validate);
        var settings = store.Load();
        var result = new AppPaths(AppContext.BaseDirectory, fixedRoot, settings.DataRoot);
        if (settings.DataRoot is not null && !DataDirectories.Exists(result.DataRoot))
        {
            if (settings.Migration is not null)
                throw new IOException($"迁移尚未完成，数据目录缺失：{result.DataRoot}。请恢复目录后重试，迁移记录已保留。");
            // The startup owner must settle plugin records before committing this fallback.
            return new AppPaths(AppContext.BaseDirectory, fixedRoot) { MissingDataRoot = result.DataRoot };
        }
        if (ensureDirectories) result.EnsureDataDirectories();
        if (!File.Exists(result.BootstrapFile)) store.Save(settings);
        return result;
    }

    // Called under the bootstrap and plugin installation leases, before any data writer starts.
    public void CompleteDataReset(Action<string> discardPluginDataState, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(discardPluginDataState);
        if (MissingDataRoot is null || _dataResetCompleted) return;
        DataDirectories.CheckPath(BootstrapFile); DataDirectories.CheckPath(BootstrapFile + ".bak");
        var store = new JsonFileStore<BootstrapSettings>(BootstrapFile, BootstrapSettings.Validate);
        var settings = store.Load();
        if (settings.Migration is not null || settings.DataRoot is null ||
            !string.Equals(Absolute(settings.DataRoot), MissingDataRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("启动配置已变化，未重置数据。请重新启动。");
        if (DataDirectories.Exists(MissingDataRoot)) throw new IOException("原数据目录已恢复，未重置数据。请重新启动。");
        token.ThrowIfCancellationRequested();
        discardPluginDataState(MissingDataRoot);
        discardPluginDataState(BootstrapRoot); // Its stale business data is also being discarded.
        DataDirectories.Clear(BootstrapRoot, token);
        token.ThrowIfCancellationRequested();
        store.Save(new()); // Keep the old pointer until cleanup succeeds, so a failed reset can retry.
        _dataResetCompleted = true;
    }

    public void EnsureDataDirectories()
    {
        if (MissingDataRoot is not null && !_dataResetCompleted)
            throw new InvalidOperationException("数据重置尚未完成，不能启动数据写入。");
        foreach (var path in new[] { ConfigRoot, LogRoot, PackageRoot, Path.Combine(DataRoot, "Cache"), Path.Combine(DataRoot, "Data", "Plugins") })
            Directory.CreateDirectory(path);
    }

    public string GetPluginDataDirectory(string pluginId)
    {
        ValidateId(pluginId);
        return Path.Combine(DataRoot, "Data", "Plugins", pluginId);
    }

    public static void ValidateId(string value)
    {
        if (!Regex.IsMatch(value ?? "", @"^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$") ||
            value!.EndsWith('.') || Regex.IsMatch(value, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)", RegexOptions.IgnoreCase))
            throw new InvalidDataException($"无效的稳定标识：{value}");
    }

    public static string Absolute(string path) => Path.IsPathFullyQualified(path)
        ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))
        : throw new InvalidDataException($"必须使用绝对路径：{path}");

    public static bool Contains(string parent, string child)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        parent = Absolute(parent);
        child = Absolute(child);
        var prefix = Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar;
        return child.Equals(parent, comparison) || child.StartsWith(prefix, comparison);
    }
}
