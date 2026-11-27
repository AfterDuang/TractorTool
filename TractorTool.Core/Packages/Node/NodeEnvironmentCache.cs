using System.Security.Cryptography;
using TractorTool.Core.Packages.Environments;
using TractorTool.Core.Storage;

namespace TractorTool.Core.Packages.Node;

internal sealed class NodeCacheStorage : EnvironmentCacheStorage<NodeEnvironmentDefinition, NodeEnvironmentPolicy>
{
    internal static bool IsProjectRegistration(string cache, DirectoryInfo entry)
    {
        cache = AppPaths.Absolute(cache);
        var registry = Path.Combine(cache, "store", "v10", "projects");
        if (!string.Equals(entry.Parent?.FullName, registry, StringComparison.OrdinalIgnoreCase)) return false;
        const FileAttributes linkDirectory = FileAttributes.Directory | FileAttributes.ReparsePoint;
        if (entry.Attributes == (FileAttributes)(-1) || (entry.Attributes & linkDirectory) != linkDirectory) return false;
        // pnpm 10.34.9 registers projects even with hoisted+copy and no global virtual store.
        // Read only the first hop: retired or failed generations need not exist anymore.
        var target = entry.ResolveLinkTarget(returnFinalTarget: false)?.FullName;
        if (target is null) return false;
        target = Path.TrimEndingDirectorySeparator(target);
        var dataRoot = Path.GetDirectoryName(Path.GetDirectoryName(cache)!)!;
        var environments = Path.Combine(dataRoot, "Packages", "Node", "Environments");
        if (!AppPaths.Contains(environments, target)) return false;
        var parts = Path.GetRelativePath(environments, target).Split(Path.DirectorySeparatorChar);
        if (parts.Length != 4 || !EnvironmentPolicy.IsHash(parts[1]) ||
            !Guid.TryParseExact(parts[2], "N", out _) || parts[3] != "environment") return false;
        AppPaths.ValidateId(parts[0]);
        var name = Convert.ToHexStringLower(SHA256.HashData(EnvironmentPolicy.Utf8.GetBytes(target)))[..32];
        return entry.Name == name;
    }

    internal static void PrepareForDataRemoval(string dataRoot, CancellationToken token)
    {
        var cache = Path.Combine(dataRoot, "Cache", "Node");
        if (IsOwned(cache)) PrepareForRemoval(cache, token);
    }
}
