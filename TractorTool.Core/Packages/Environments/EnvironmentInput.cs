using System.Security.Cryptography;

namespace TractorTool.Core.Packages.Environments;

internal static class EnvironmentInput
{
    internal const int MaxBytes = 2 * 1024 * 1024;

    internal static string Read(string path, string expectedHash)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > MaxBytes) throw new InvalidDataException("依赖项目或锁文件大小无效。");
        var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("依赖项目或锁文件与声明的 SHA256 不符。");
        return EnvironmentPolicy.Utf8.GetString(bytes);
    }
}
