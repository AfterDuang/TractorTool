using System.Text;

namespace TractorTool.Plugins.FolderHistory.Processing;

public sealed record TextDiffLine(string Kind, int? BeforeLine, int? AfterLine, string Text, string Ending);
public sealed record TextComparisonResult(bool IsText, string Summary, IReadOnlyList<TextDiffLine> Lines);

public static class TextComparison
{
    public const int MaxBytes = 256 * 1024;
    private const int MaxLines = 1200;
    private const int MaxLineLength = 4096;
    private const int MaxCells = 1_000_000;

    public static TextComparisonResult Compare(byte[] before, byte[] after)
    {
        if (before.Length > MaxBytes || after.Length > MaxBytes)
            return Unavailable("文件超过 256 KiB，未加载文本。可另存原始文件查看。");
        if (!TryDecode(before, out var oldText, out var oldEncoding) ||
            !TryDecode(after, out var newText, out var newEncoding))
            return Unavailable("无法可靠识别为文本，或含有二进制控制字符。可另存原始文件查看。");
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);
        if (oldLines.Length > MaxLines || newLines.Length > MaxLines ||
            (long)(oldLines.Length + 1) * (newLines.Length + 1) > MaxCells ||
            oldLines.Concat(newLines).Any(line => line.Text.Length > MaxLineLength))
            return Unavailable("文本行数或单行长度超过预览上限。可另存原始文件查看。");

        var lengths = new int[oldLines.Length + 1, newLines.Length + 1];
        for (var i = oldLines.Length - 1; i >= 0; i--)
            for (var j = newLines.Length - 1; j >= 0; j--)
                lengths[i, j] = oldLines[i] == newLines[j] ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);

        var result = new List<TextDiffLine>();
        var previous = 0;
        var current = 0;
        while (previous < oldLines.Length || current < newLines.Length)
        {
            if (previous < oldLines.Length && current < newLines.Length && oldLines[previous] == newLines[current])
            {
                result.Add(new("相同", previous + 1, current + 1, oldLines[previous].Text, oldLines[previous].Ending));
                previous++;
                current++;
            }
            else if (previous < oldLines.Length &&
                (current == newLines.Length || lengths[previous + 1, current] >= lengths[previous, current + 1]))
            {
                result.Add(new("删除", previous + 1, null, oldLines[previous].Text, oldLines[previous].Ending));
                previous++;
            }
            else
            {
                result.Add(new("新增", null, current + 1, newLines[current].Text, newLines[current].Ending));
                current++;
            }
        }

        var added = result.Count(line => line.Kind == "新增");
        var removed = result.Count(line => line.Kind == "删除");
        var summary = $"新增 {added} 行 · 删除 {removed} 行 · {oldEncoding} → {newEncoding}";
        if (added == 0 && removed == 0 && !before.AsSpan().SequenceEqual(after))
            summary += " · 文本相同，原始编码或 BOM 不同";
        return new(true, summary + "。仅供只读预览，恢复使用原始字节。", result);
    }

    private static bool TryDecode(byte[] bytes, out string text, out string name)
    {
        text = "";
        var offset = 0;
        Encoding encoding = new UTF8Encoding(false, true);
        name = "UTF-8";
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 }))
        { encoding = new UTF32Encoding(false, false, true); offset = 4; name = "UTF-32 LE"; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff }))
        { encoding = new UTF32Encoding(true, false, true); offset = 4; name = "UTF-32 BE"; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }))
        { encoding = new UnicodeEncoding(false, false, true); offset = 2; name = "UTF-16 LE"; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
        { encoding = new UnicodeEncoding(true, false, true); offset = 2; name = "UTF-16 BE"; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
        { offset = 3; name = "UTF-8 BOM"; }
        try { text = encoding.GetString(bytes, offset, bytes.Length - offset); }
        catch (DecoderFallbackException) { return false; }
        return !text.Any(character => char.IsControl(character) && character is not '\t' and not '\r' and not '\n');
    }

    private static (string Text, string Ending)[] SplitLines(string text)
    {
        var lines = new List<(string, string)>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not '\r' and not '\n') continue;
            var content = text[start..index];
            var ending = text[index] == '\r' ? "CR" : "LF";
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n') { index++; ending = "CRLF"; }
            lines.Add((content, ending));
            start = index + 1;
            if (lines.Count > MaxLines) return [.. lines];
        }
        if (start < text.Length) lines.Add((text[start..], "无换行"));
        return [.. lines];
    }

    private static TextComparisonResult Unavailable(string message) => new(false, message, []);
}
