namespace TractorTool.Core.Plugins;

internal static class PluginAssemblyPolicy
{
    private static readonly Lazy<HashSet<string>> ReservedNames = new(ReadReservedNames);

    internal static bool IsHostInternal(string? name) =>
        string.Equals(name, "TractorTool", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "TractorTool.Core", StringComparison.OrdinalIgnoreCase);

    internal static bool IsReserved(string name) => ReservedNames.Value.Contains(name);

    internal static void ValidatePluginFile(string path, PluginManifest manifest)
    {
        if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return;
        var name = Path.GetFileNameWithoutExtension(path);
        if (IsReserved(name))
            throw new InvalidDataException($"插件包含宿主共享程序集：{name}。");
        if (name.StartsWith("Avalonia.", StringComparison.OrdinalIgnoreCase) &&
            (!Version.TryParse(manifest.UiApiVersion, out var api) || api < new Version(1, 6)))
            throw new InvalidDataException($"私有 Avalonia 扩展 {name} 需要在清单声明 UI API 1.6 或更高。");
    }

    private static HashSet<string> ReadReservedNames()
    {
        var inventory = AppContext.GetData("TractorTool.PluginSdk.ReservedAssemblies") as string;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (inventory is null || inventory.Split('|').Any(name => string.IsNullOrWhiteSpace(name) || !names.Add(name)) ||
            !names.IsSupersetOf(["TractorTool", "TractorTool.Core", "TractorTool.Contracts", "TractorTool.UI"]))
            throw new InvalidDataException("宿主 runtimeconfig.json 的共享程序集清单缺失或无效，请修复宿主安装。");
        // Core also runs without a UI in management checks. A UI host must never classify its
        // fundamental Avalonia types as private just because its runtimeconfig is incomplete.
        var trustedPaths = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trustedPaths?.Split(Path.PathSeparator).Any(path =>
                Path.GetFileName(path).Equals("Avalonia.Base.dll", StringComparison.OrdinalIgnoreCase)) == true &&
            !names.IsSupersetOf(["Avalonia", "Avalonia.Base", "Avalonia.Controls"]))
            throw new InvalidDataException("宿主共享程序集清单缺少 Avalonia 基础程序集，请修复宿主安装。");
        return names;
    }
}
