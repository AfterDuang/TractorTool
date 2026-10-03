using System.Text.RegularExpressions;
using TractorTool.Contracts;

namespace TractorTool.Core.Storage;

public sealed record BootstrapSettings : IVersionedSettings
{
    public int SchemaVersion { get; init; } = 1;
    public string? DataRoot { get; init; }
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

    public static AppPaths Open(string? testProfileRoot = null)
    {
        var fixedRoot = testProfileRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TractorTool");
        fixedRoot = Absolute(fixedRoot);
        Directory.CreateDirectory(fixedRoot);
        var store = new JsonFileStore<BootstrapSettings>(Path.Combine(fixedRoot, "bootstrap.json"));
        var settings = store.Load();
        var result = new AppPaths(AppContext.BaseDirectory, fixedRoot, settings.DataRoot);
        if (settings.DataRoot is not null && !Directory.Exists(result.DataRoot))
            throw new DirectoryNotFoundException($"数据目录不可用：{result.DataRoot}。请恢复该目录或修正 {result.BootstrapFile}，未创建空数据替代。");
        result.EnsureDataDirectories();
        if (!File.Exists(result.BootstrapFile)) store.Save(settings);
        return result;
    }

    public void EnsureDataDirectories()
    {
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
