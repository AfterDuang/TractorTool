using System.Reflection;
using System.Runtime.Loader;

namespace TractorTool.Core.Plugins;

internal sealed class PluginLoadContext(string entryPath, string packageRoot) : AssemblyLoadContext(isCollectible: false)
{
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
        // Framework assemblies are provided by .NET; private dependencies must ship with the plugin.
        if (name.Name is "netstandard" or "mscorlib" or "System" or "Microsoft.CSharp" || name.Name!.StartsWith("System.")) return null;
        throw new FileNotFoundException($"插件缺少私有依赖：{name}");
    }

    protected override nint LoadUnmanagedDll(string name)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(name);
        if (path is null) return 0;
        return LoadUnmanagedDllFromPath(ManifestValidator.PackagePath(packageRoot, path));
    }
}
