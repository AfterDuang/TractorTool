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
    internal static void PrepareForRemoval(string directory, CancellationToken token)
    {
        lock (FileLocks.For(directory))
        {
            if (!IsOwned(directory)) throw new InvalidDataException("依赖缓存归属不明，已保留文件。");
            // Validate the whole tree before removing any registration. Never recurse through a link.
            foreach (var entry in CheckTree(directory, token))
            {
                token.ThrowIfCancellationRequested();
                PackageFiles.CheckPath(entry.Parent!.FullName);
                entry.Refresh();
                if (entry.Attributes == (FileAttributes)(-1)) continue;
                if (!Policy.IsCacheRegistration(directory, entry))
                    throw new InvalidDataException("依赖缓存登记已变化，已保留文件。");
                entry.Delete(recursive: false);
            }
        }
    }

    private static List<DirectoryInfo> CheckTree(string directory, CancellationToken token = default)
    {
        List<DirectoryInfo> registrations = [];
        Visit(directory);
        return registrations;

        void Visit(string current)
        {
            token.ThrowIfCancellationRequested();
            PackageFiles.CheckPath(current);
            if (!Directory.Exists(current)) return;
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(current); }
            // Concurrent installs can retire their own cache scratch directories.
            catch (DirectoryNotFoundException) { return; }
            foreach (var path in entries)
            {
                token.ThrowIfCancellationRequested();
                var entry = new DirectoryInfo(path);
                if (Policy.IsCacheRegistration(directory, entry)) { registrations.Add(entry); continue; }
                PackageFiles.CheckPath(path);
                if (Directory.Exists(path)) Visit(path);
            }
        }
    }
}
