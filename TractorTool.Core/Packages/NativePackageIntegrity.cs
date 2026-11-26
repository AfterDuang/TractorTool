using System.Buffers;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using TractorTool.Contracts.Packages;
using TractorTool.Core.Storage;

namespace TractorTool.Core.Packages;

/// <summary>Records and verifies the complete portable payload without executing native code.</summary>
internal static class NativePackageIntegrity
{
    internal const string ManifestFileName = ".package-files.json";
    private const int MaxPaths = 10000;
    private const int MaxManifestBytes = 8 * 1024 * 1024;
    private const long MaxPayloadBytes = 1024L * 1024 * 1024;

    internal static async Task<string> WriteAsync(string directory, PackageDefinition definition, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RequireNative(definition);
        directory = AppPaths.Absolute(directory);
        PackageFiles.CheckPath(directory);
        if (File.Exists(Path.Combine(directory, "receipt.json")) || File.Exists(Path.Combine(directory, ManifestFileName)))
            throw new InvalidDataException("运行库载荷包含宿主保留的安装记录。");

        var files = EnumeratePayload(directory, token);
        ValidateLibraries(directory, definition, files, token);
        List<PayloadFile> manifestFiles = [];
        foreach (var (relative, length) in files.OrderBy(p => p.Key, StringComparer.Ordinal))
            manifestFiles.Add(new(relative, length, await HashFileAsync(PackageFiles.SafePath(directory, relative), length, token)));

        var bytes = JsonSerializer.SerializeToUtf8Bytes(new PayloadManifest(1, manifestFiles), JsonOptions.Value);
        if (bytes.Length > MaxManifestBytes) throw new InvalidDataException("运行库文件清单超过大小限制。");
        var manifestPath = PackageFiles.SafePath(directory, ManifestFileName);
        await using (var output = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            await output.WriteAsync(bytes, token);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    internal static async Task ValidateAsync(string directory, PackageDefinition definition, string manifestHash, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RequireNative(definition);
        directory = AppPaths.Absolute(directory);
        ValidateReceiptMetadata(directory, manifestHash);
        var manifestPath = PackageFiles.SafePath(directory, ManifestFileName);
        byte[] bytes;
        await using (var input = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
        {
            // Recheck after opening: the file may have changed since the receipt was inspected.
            if (input.Length is <= 0 or > MaxManifestBytes) throw new InvalidDataException("运行库文件清单大小无效。");
            bytes = new byte[(int)input.Length];
            await input.ReadExactlyAsync(bytes, token);
            if (input.Length != bytes.Length) throw new InvalidDataException("运行库文件清单在读取期间发生变化。");
        }
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(manifestHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("运行库文件清单校验失败，请修复依赖。");
        var manifest = JsonSerializer.Deserialize<PayloadManifest>(bytes, JsonOptions.Value);
        if (manifest is not { Schema: 1, Files.Count: > 0 and <= MaxPaths })
            throw new InvalidDataException("运行库文件清单格式无效。");

        var actual = EnumeratePayload(directory, token);
        if (actual.Count != manifest.Files.Count) throw new InvalidDataException("运行库文件集合已变化，请修复依赖。");
        HashSet<string> recorded = new(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            if (file is null || file.Path is null || file.Path.Contains('\\') ||
                IsMetadata(file.Path) || !recorded.Add(file.Path) || !IsHash(file.Sha256) ||
                file.Length is < 0 or > MaxPayloadBytes)
                throw new InvalidDataException("运行库文件清单条目无效。");
            var path = PackageFiles.SafePath(directory, file.Path);
            total += file.Length;
            if (total > MaxPayloadBytes || !actual.TryGetValue(file.Path, out var length) || length != file.Length)
                throw new InvalidDataException("运行库文件缺失或长度已变化，请修复依赖。");
            if (!file.Sha256.Equals(await HashFileAsync(path, length, token), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"运行库文件校验失败：{file.Path}。请修复依赖。");
        }
        ValidateLibraries(directory, definition, actual, token);
    }

    // Startup reads are intentionally cheap; every acquisition verifies the full payload below this record.
    internal static void ValidateReceiptMetadata(string directory, string manifestHash)
    {
        if (!IsHash(manifestHash)) throw new InvalidDataException("运行库文件清单摘要无效。");
        var path = PackageFiles.SafePath(directory, ManifestFileName);
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is <= 0 or > MaxManifestBytes)
            throw new InvalidDataException("运行库文件清单缺失或过大，请修复依赖。");
    }

    internal static bool IsMetadata(string relative) =>
        relative.Equals("receipt.json", StringComparison.OrdinalIgnoreCase) ||
        relative.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, long> EnumeratePayload(string directory, CancellationToken token)
    {
        PackageFiles.CheckPath(directory);
        Dictionary<string, long> files = new(StringComparer.Ordinal);
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        Stack<string> pending = new();
        pending.Push(directory);
        long total = 0;
        while (pending.TryPop(out var current))
        {
            PackageFiles.CheckPath(current);
            foreach (var path in Directory.EnumerateFileSystemEntries(current))
            {
                token.ThrowIfCancellationRequested();
                PackageFiles.CheckPath(path);
                var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
                _ = PackageFiles.RelativeParts(relative);
                var attributes = File.GetAttributes(path);
                if (IsMetadata(relative))
                {
                    if ((attributes & FileAttributes.Directory) != 0 || relative is not ("receipt.json" or ManifestFileName))
                        throw new InvalidDataException("运行库安装记录名称存在冲突。");
                    continue;
                }
                if (!paths.Add(relative) || paths.Count > MaxPaths)
                    throw new InvalidDataException("运行库路径重名或文件数超过限制。");
                if ((attributes & FileAttributes.Directory) != 0) { pending.Push(path); continue; }
                var length = new FileInfo(path).Length;
                if (length < 0 || length > MaxPayloadBytes || (total += length) > MaxPayloadBytes)
                    throw new InvalidDataException("运行库展开大小超过限制。");
                files.Add(relative, length);
            }
        }
        if (files.Count == 0) throw new InvalidDataException("运行库载荷为空。");
        return files;
    }

    private static async Task<string> HashFileAsync(string path, long length, CancellationToken token)
    {
        PackageFiles.CheckPath(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length != length) throw new InvalidDataException("运行库文件长度已变化。");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long read = 0;
            int count;
            while ((count = await input.ReadAsync(buffer.AsMemory(), token)) != 0)
            {
                read += count;
                if (read > length) throw new InvalidDataException("运行库文件在校验期间发生变化。");
                hash.AppendData(buffer, 0, count);
            }
            if (read != length) throw new InvalidDataException("运行库文件在校验期间发生变化。");
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static void ValidateLibraries(string directory, PackageDefinition definition, IReadOnlyDictionary<string, long> files,
        CancellationToken token)
    {
        var machine = definition.Rid switch
        {
            "win-x86" => Machine.I386,
            "win-x64" => Machine.Amd64,
            "win-arm64" => Machine.Arm64,
            _ => throw new InvalidDataException("运行库平台不受支持。")
        };
        foreach (var relative in definition.Libraries.Values.Distinct(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var canonical = string.Join('/', PackageFiles.RelativeParts(relative));
            if (!files.ContainsKey(canonical)) throw new InvalidDataException($"运行库入口不存在：{relative}。");
            var path = PackageFiles.SafePath(directory, canonical);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                using var reader = new PEReader(input);
                var headers = reader.PEHeaders;
                if (headers.PEHeader is null || headers.CoffHeader.Machine != machine ||
                    (headers.CoffHeader.Characteristics & Characteristics.Dll) == 0 || headers.CorHeader is not null ||
                    headers.PEHeader.Magic != (machine == Machine.I386 ? PEMagic.PE32 : PEMagic.PE32Plus))
                    throw new InvalidDataException($"运行库入口不是目标平台的原生 DLL：{relative}。");
            }
            catch (BadImageFormatException ex)
            {
                throw new InvalidDataException($"运行库入口不是有效的 PE 文件：{relative}。", ex);
            }
        }
    }

    private static void RequireNative(PackageDefinition definition)
    {
        if (definition.Kind != PackageKind.NativeLibrary) throw new InvalidOperationException("此校验仅适用于原生运行库。");
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private sealed record PayloadManifest(int Schema, IReadOnlyList<PayloadFile> Files);
    private sealed record PayloadFile(string Path, long Length, string Sha256);
}
