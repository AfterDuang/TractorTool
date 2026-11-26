using TractorTool.Core.Storage;

namespace TractorTool.Core.Packages.Environments;

internal class EnvironmentCacheStorage<TDefinition, TPolicy>
    where TDefinition : class, IEnvironmentDefinition
    where TPolicy : EnvironmentPolicy<TDefinition>, new()
{
    private static readonly TPolicy Policy = new();
    internal static string Marker => Policy.CacheMarker;
    private static string Owner => Policy.CacheOwner;
    internal static bool IsOwned(string directory)
    {
        PackageFiles.CheckPath(directory);
        var file = Path.Combine(directory, Marker); PackageFiles.CheckPath(file);
        return File.Exists(file) && new FileInfo(file).Length == Owner.Length && File.ReadAllText(file) == Owner;
    }
    internal static void Ensure(string directory)
    {
        lock (FileLocks.For(directory))
        {
            if (IsOwned(directory)) { CheckTree(directory); return; }
            if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
                throw new InvalidDataException("依赖缓存含未登记内容，未覆盖缓存目录。");
            Directory.CreateDirectory(directory);
            var pending = Path.Combine(Path.GetDirectoryName(directory)!, "." + Policy.Folder + "-cache-owner.tmp");
            PackageFiles.CheckPath(pending);
            using (var stream = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None))
            { stream.Write(EnvironmentPolicy.Utf8.GetBytes(Owner)); stream.Flush(true); }
            File.Move(pending, Path.Combine(directory, Marker));
        }
    }
    internal static void CheckTree(string directory)
    {
        PackageFiles.CheckPath(directory);
        if (!Directory.Exists(directory)) return;
        string[] entries;
        try { entries = Directory.GetFileSystemEntries(directory); }
        // Concurrent installs can retire their own cache scratch directories.
        // Link and access errors are still rejected; only an ordinary disappearance is harmless.
        catch (DirectoryNotFoundException) { return; }
        foreach (var path in entries)
        {
            PackageFiles.CheckPath(path);
            if (Directory.Exists(path)) CheckTree(path);
        }
    }
}
