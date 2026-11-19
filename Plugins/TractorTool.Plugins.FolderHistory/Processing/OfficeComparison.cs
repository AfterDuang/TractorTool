using System.IO.Compression;
using System.Xml;
using DocumentFormat.OpenXml.Packaging;

namespace TractorTool.Plugins.FolderHistory.Processing;

internal enum OfficeFormat { Word, Excel }

internal static class OfficeComparison
{
    private const int MiB = 1024 * 1024;
    private const int MaxFileBytes = 32 * MiB;
    private const int MaxParts = 4096;
    private const int MaxXmlBytes = 32 * MiB;

    internal static OfficeFormat? GetFormat(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".docx" => OfficeFormat.Word,
        ".xlsx" => OfficeFormat.Excel,
        _ => null
    };

    internal static int FileSizeLimit(OfficeFormat? format) => format.HasValue ? MaxFileBytes : TextComparison.MaxBytes;

    internal static OpenSettings CreateOpenSettings() => new() { MaxCharactersInPart = 16 * MiB };

    internal static TextComparisonResult Compare(OfficeFormat format, byte[]? before, byte[]? after, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            CheckPackage(before, token);
            if (!ReferenceEquals(before, after)) CheckPackage(after, token);
            var result = format switch
            {
                OfficeFormat.Word => WordComparison.Compare(before, after, token),
                OfficeFormat.Excel => SpreadsheetComparison.Compare(before, after, token),
                _ => throw new ArgumentOutOfRangeException(nameof(format))
            };
            if (result.IsText && result.Lines.Count == 0 && before is not null && after is not null &&
                !before.AsSpan().SequenceEqual(after))
                return result with { Summary = "文件有变化，" + result.Summary };
            return result;
        }
        catch (InvalidDataException error)
        {
            return new(false, $"无法预览此文档。{error.Message} 可另存原文件查看。", []);
        }
        catch (Exception error) when (error is OpenXmlPackageException or IOException or FormatException or XmlException)
        {
            return new(false, "无法读取文档结构，文件可能已加密、损坏或与扩展名不符。可另存原文件查看。", []);
        }
    }

    private static void CheckPackage(byte[]? bytes, CancellationToken token)
    {
        if (bytes is null) return;
        if (bytes.Length > MaxFileBytes) throw new InvalidDataException("文档超过 32 MiB 预览上限。");
        if (!bytes.AsSpan().StartsWith("PK\x03\x04"u8))
            throw new InvalidDataException("文件可能已加密、损坏或与扩展名不符。");

        using var stream = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        if (archive.Entries.Count > MaxParts) throw new InvalidDataException("文档内的部件过多。");
        long xmlBytes = 0, totalBytes = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Length > 128L * MiB - totalBytes) throw new InvalidDataException("文档解压后超过 128 MiB 预览上限。");
            totalBytes += entry.Length;
            if (!entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
                !entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.Length > MaxXmlBytes - xmlBytes) throw new InvalidDataException("文档 XML 内容超过预览上限。");
            xmlBytes += entry.Length;
        }
    }
}
