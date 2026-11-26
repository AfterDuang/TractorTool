using TractorTool.Contracts.Packages;
using TractorTool.Core.Packages.Python;
using TractorTool.Core.Packages.Node;
using TractorTool.Core.Storage;

namespace TractorTool.Core.Packages;

public sealed partial class ExternalPackageService
{
    private const string NativeRootMarker = ".native-packages";
    private const string NativeRootIdentity = "TractorTool.NativePackages.v1";
    private readonly HashSet<PackageReference> _ambiguousInstallations = [];
    private readonly Dictionary<PackageReference, string> _installedLocations = [];

    private string PackageDirectory(PackageDefinition definition)
    {
        lock (_gate)
        {
            var parent = _root;
            if (definition.Kind == PackageKind.NativeLibrary)
            {
                parent = PackageFiles.SafePath(_root, "DLL");
                if (HasDirectory(parent) && !IsNativeRoot(parent) && Directory.EnumerateFileSystemEntries(parent).Any())
                    throw new InvalidDataException("Packages/DLL 已被旧依赖或未知文件占用，已保留文件，不能作为运行库目录。");
            }
            else if (string.Equals(definition.Package.Id, "DLL", StringComparison.OrdinalIgnoreCase) &&
                IsNativeRoot(PackageFiles.SafePath(_root, "DLL")))
                throw new InvalidDataException("依赖标识 DLL 与运行库分类目录冲突，请使用独立标识。");
            else if (definition.Kind == PackageKind.Tool && string.Equals(definition.Package.Id, "uv", StringComparison.OrdinalIgnoreCase) &&
                PythonEnvironmentService.IsOwnedRoot(PackageFiles.SafePath(_root, "uv")))
                throw new InvalidDataException("依赖标识 uv 与 Python 环境分类目录冲突，请使用独立标识。");
            else if (definition.Kind == PackageKind.Tool && string.Equals(definition.Package.Id, "Node", StringComparison.OrdinalIgnoreCase) &&
                NodeEnvironmentService.IsOwnedRoot(PackageFiles.SafePath(_root, "Node")))
                throw new InvalidDataException("依赖标识 Node 与 Node 环境分类目录冲突，请使用独立标识。");
            var directory = PackageFiles.SafePath(parent, $"{definition.Package.Id}/{definition.Package.Version}/{definition.Rid}");
            if (_installedLocations.TryGetValue(definition.Package, out var existing) && !string.Equals(existing, directory,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException("该依赖版本已存在于另一安装目录，已保留文件，不能以不同类型重新安装。");
            return directory;
        }
    }

    // An explicit marker distinguishes the category from an existing package literally named DLL.
    // Only an absent/empty directory may be claimed; unknown contents are never moved or deleted.
    private static bool IsNativeRoot(string directory)
    {
        var marker = PackageFiles.SafePath(directory, NativeRootMarker);
        var info = new FileInfo(marker);
        if (!info.Exists) return false;
        if (info.Length != NativeRootIdentity.Length || File.ReadAllText(marker) != NativeRootIdentity)
            throw new InvalidDataException("DLL 运行库目录标记无效，已保留文件。");
        return true;
    }

    private void PreparePackageParent(PackageDefinition definition, string work)
    {
        // Different packages can finish downloading concurrently. Claim the shared category once.
        var categoryGate = definition.Kind == PackageKind.Tool && definition.Package.Id.Equals("uv", StringComparison.OrdinalIgnoreCase)
            ? FileLocks.For(Path.Combine(_root, "uv")) :
            definition.Kind == PackageKind.Tool && definition.Package.Id.Equals("Node", StringComparison.OrdinalIgnoreCase)
            ? FileLocks.For(Path.Combine(_root, "Node")) : _gate;
        lock (categoryGate)
        lock (_gate)
        {
            var directory = PackageDirectory(definition);
            if (definition.Kind == PackageKind.NativeLibrary)
            {
                var parent = PackageFiles.SafePath(_root, "DLL");
                if (!IsNativeRoot(parent))
                {
                    Directory.CreateDirectory(parent);
                    var marker = PackageFiles.SafePath(work, NativeRootMarker);
                    File.WriteAllText(marker, NativeRootIdentity);
                    File.Move(marker, PackageFiles.SafePath(parent, NativeRootMarker));
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
        }
    }
}
