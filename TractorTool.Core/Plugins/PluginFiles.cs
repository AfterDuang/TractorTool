using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TractorTool.Core.Storage;

namespace TractorTool.Core.Plugins;

// Files are always staged on the installation volume. Never load code to inspect a package.
internal static class PluginFiles
{
    internal const long MaxPackageBytes = 256L * 1024 * 1024;

    internal static string Under(string root, params string[] parts)
    {
        var path = Path.GetFullPath(Path.Combine([root, .. parts]));
        if (!AppPaths.Contains(root, path)) throw new InvalidDataException("插件文件路径越界。");
        for (string? p = path; p is not null; p = Path.GetDirectoryName(p)) ManifestValidator.CheckNoLinks(p);
        return path;
    }

    internal static PluginManifest Extract(string archivePath, string stage, HostCompatibility compatibility)
    {
        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxPackageBytes) throw new InvalidDataException("插件 ZIP 不能超过 256 MiB。");
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        if (archive.Entries.Count > 5000) throw new InvalidDataException("插件 ZIP 文件数量超过限制。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/').TrimEnd('/');
            if (string.IsNullOrEmpty(name) || name.Length > 240 || name.Split('/').Length > 32 || name.Split('/').Any(p => !SafeSegment(p)) || !names.Add(name))
                throw new InvalidDataException("插件 ZIP 含非法路径或重复条目。");
            var kind = (entry.ExternalAttributes >> 16) & 0xF000;
            if (kind is not (0 or 0x8000 or 0x4000) || (entry.ExternalAttributes & 0x400) != 0)
                throw new InvalidDataException("插件 ZIP 不接受链接或特殊文件。");
            if (entry.Length > MaxPackageBytes - total) throw new InvalidDataException("插件解压大小超过 256 MiB。");
            total += entry.Length;
        }
        var json = archive.Entries.SingleOrDefault(e => e.FullName == "plugin.json")
            ?? throw new InvalidDataException("ZIP 根目录必须包含 plugin.json。");
        if (json.Length > 128 * 1024) throw new InvalidDataException("插件清单超过 128 KiB。");
        PluginManifest manifest;
        using (var input = json.Open()) manifest = JsonSerializer.Deserialize<PluginManifest>(input, JsonOptions.Value)
            ?? throw new InvalidDataException("插件清单为空。");
        AppPaths.ValidateId(manifest.Id);
        ManifestValidator.ValidateVersion(manifest.Version);
        var package = Under(stage, manifest.Id, manifest.Version);
        ManifestValidator.Validate(manifest, package);
        if (compatibility.Check(manifest) is { } error) throw new InvalidDataException(error);
        Directory.CreateDirectory(package);
        foreach (var entry in archive.Entries)
        {
            var path = Under(package, entry.FullName.Replace('\\', '/').TrimEnd('/'));
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var input = entry.Open();
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[64 * 1024];
            long length = 0;
            int read;
            while ((read = input.Read(buffer)) != 0)
            {
                length += read;
                if (length > entry.Length) throw new InvalidDataException("ZIP 内容大小与声明不一致。");
                output.Write(buffer, 0, read);
            }
            if (length != entry.Length) throw new InvalidDataException("ZIP 内容不完整。");
            output.Flush(true);
        }
        ValidatePackage(package, compatibility);
        return manifest;
    }

    private static bool SafeSegment(string value)
    {
        if (value is "" or "." or ".." || value.EndsWith('.') || value.EndsWith(' ') ||
            value.Any(c => c < 32 || "<>:\"|?*".Contains(c))) return false;
        var stem = value.Split('.')[0];
        return !new[] { "CON", "PRN", "AUX", "NUL", "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }

    internal static PluginManifest ValidatePackage(string path, HostCompatibility compatibility)
    {
        var manifest = ManifestValidator.Read(path);
        if (compatibility.Check(manifest) is { } error) throw new InvalidDataException(error);
        foreach (var file in Files(path))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                (PluginLoadContext.Shared(name) || name is "TractorTool" or "TractorTool.Core"))
                throw new InvalidDataException($"插件包含宿主共享程序集：{name}。");
        }
        using var stream = File.OpenRead(ManifestValidator.EntryPath(manifest, path));
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) throw new InvalidDataException("入口不是托管 DLL。");
        var metadata = pe.GetMetadataReader();
        if (!metadata.IsAssembly) throw new InvalidDataException("入口不是托管程序集。");
        foreach (var handle in metadata.AssemblyReferences)
            if (metadata.GetString(metadata.GetAssemblyReference(handle).Name) is "TractorTool" or "TractorTool.Core")
                throw new InvalidDataException("插件不能依赖宿主或 Core。");
        return manifest;
    }

    internal static IEnumerable<string> Files(string root, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Under(root);
        foreach (var path in Directory.EnumerateFileSystemEntries(root).Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            Under(root, Path.GetFileName(path));
            if (Directory.Exists(path)) { foreach (var file in Files(path, token)) yield return file; }
            else yield return path;
        }
    }

    internal static string Digest(string root, CancellationToken token = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        foreach (var path in Files(root, token))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, path).Replace('\\', '/') + "\0"));
            using var file = File.OpenRead(path);
            using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int count;
            do
            {
                token.ThrowIfCancellationRequested();
                count = file.Read(buffer);
                fileHash.AppendData(buffer, 0, count);
            } while (count != 0);
            hash.AppendData(fileHash.GetHashAndReset());
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static void Copy(string source, string destination)
    {
        Under(source); Under(destination);
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source))
            Copy(directory, Under(destination, Path.GetFileName(directory)));
        foreach (var path in Directory.EnumerateFiles(source))
        {
            Under(source, Path.GetFileName(path));
            var target = Under(destination, Path.GetRelativePath(source, path));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target, false);
        }
        if (Digest(source) != Digest(destination)) throw new IOException("插件数据备份校验失败。");
    }

    internal static void Delete(string boundary, string path)
    {
        path = Under(boundary, Path.GetRelativePath(boundary, path));
        if (path == AppPaths.Absolute(boundary)) throw new InvalidOperationException("不能删除管理根目录。");
        if (!Directory.Exists(path)) return;
        _ = Files(path).ToArray(); // Reject all links before recursive deletion.
        Directory.Delete(path, true);
    }
}
