using System.Reflection;
using System.Runtime.Loader;

namespace TractorTool.Core.Plugins;

internal sealed class PluginLoadContext(string entryPath, string packageRoot) : AssemblyLoadContext(isCollectible: false)
{
    private static readonly HashSet<string> FrameworkAssemblies = ReadFrameworkAssemblies();
    private readonly AssemblyDependencyResolver _resolver = new(entryPath);
    private readonly string _directory = Path.GetDirectoryName(entryPath)!;

    internal static bool Shared(string name) => name is "TractorTool.Contracts" or "TractorTool.UI" or "MicroCom.Runtime" or "FluentAvalonia" ||
        name == "Avalonia" || name.StartsWith("Avalonia.", StringComparison.Ordinal);

    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name is "TractorTool" or "TractorTool.Core")
            throw new FileLoadException("插件不能依赖宿主或 Core 内部实现。", name.Name);
        if (Shared(name.Name!))
        {
            var shared = Default.LoadFromAssemblyName(name);
            if (shared.GetName().Version != name.Version)
                throw new FileLoadException($"共享程序集版本不匹配：{name}，宿主 {shared.GetName()}");
            return shared;
        }
        var path = _resolver.ResolveAssemblyToPath(name);
        if (path is null)
        {
            var adjacent = Path.Combine(_directory, name.Name + ".dll");
            if (File.Exists(adjacent)) path = adjacent;
        }
        if (path is not null)
        {
            return LoadFromAssemblyPath(ManifestValidator.PackagePath(packageRoot, path));
        }
        // Only SDK platform assemblies may fall back to the runtime. TPA also contains host-private libraries.
        if (FrameworkAssemblies.Contains(name.Name!)) return null;
        throw new FileNotFoundException($"插件缺少私有依赖：{name}");
    }

    private static HashSet<string> ReadFrameworkAssemblies()
    {
        using var stream = typeof(PluginLoadContext).Assembly.GetManifestResourceStream("TractorTool.Core.PluginFrameworkManifest.txt")
            ?? throw new InvalidOperationException("宿主缺少 .NET 框架程序集清单，请重新构建宿主。");
        using var reader = new StreamReader(stream);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line)
        {
            // SDK format: file | package | assembly version | file version. Skip entries without an assembly version.
            var fields = line.Split('|');
            if (fields.Length != 4) throw new InvalidDataException(".NET 平台清单格式无效，请重新构建宿主。");
            if (fields[2].Length > 0 && fields[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                names.Add(Path.GetFileNameWithoutExtension(fields[0]));
        }
        if (names.Count == 0) throw new InvalidDataException(".NET 平台程序集清单为空，请重新构建宿主。");
        return names;
    }

    protected override nint LoadUnmanagedDll(string name)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(name);
        if (path is null) return 0;
        return LoadUnmanagedDllFromPath(ManifestValidator.PackagePath(packageRoot, path));
    }
}
