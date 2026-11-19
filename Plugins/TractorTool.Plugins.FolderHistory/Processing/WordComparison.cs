using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace TractorTool.Plugins.FolderHistory.Processing;

internal static class WordComparison
{
    private const int MaxCharacters = 4 * 1024 * 1024;
    private const int MaxParagraphs = 100_000;

    internal static TextComparisonResult Compare(byte[]? before, byte[]? after, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var original = ReadParagraphs(before, token);
        var revised = ReferenceEquals(before, after) ? original : ReadParagraphs(after, token);
        if (original.SequenceEqual(revised, StringComparer.Ordinal))
        {
            var summary = before is null ? "新增 Word 文档，没有可显示的正文或表格文字。" :
                after is null ? "删除 Word 文档，没有可显示的正文或表格文字。" :
                "当前可预览的正文和表格文字没有变化。排版、图片等可另存原文件查看。";
            return new(true, summary, []);
        }
        return TextComparison.CompareParagraphs(original, revised, token);
    }

    private static string[] ReadParagraphs(byte[]? bytes, CancellationToken token)
    {
        if (bytes is null) return [];
        using var stream = new MemoryStream(bytes, writable: false);
        using var document = WordprocessingDocument.Open(stream, false, OfficeComparison.CreateOpenSettings());
        var main = document.MainDocumentPart ?? throw new InvalidDataException("Word 文档缺少正文部件。");
        using var reader = OpenXmlReader.Create(main);
        var lines = new List<string>();
        var paragraph = new StringBuilder();
        var enclosingRows = new Stack<(int Column, bool Continued)>();
        var tableStarts = new Stack<int>();
        var cellColumn = 0;
        var continuedCell = false;
        var characters = 0;
        var excludedDepth = int.MaxValue;
        var inBody = false;
        var inParagraph = false;
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.Depth > 128) throw new InvalidDataException("Word 文档结构过深，可另存原文件查看。");
            var type = reader.ElementType;
            if (!inBody)
            {
                inBody = reader.IsStartElement && type == typeof(W.Body);
                continue;
            }
            if (reader.Depth > excludedDepth) continue;
            excludedDepth = int.MaxValue;
            if (reader.IsStartElement)
            {
                if (type == typeof(W.Drawing) || type == typeof(W.Picture) || type == typeof(W.EmbeddedObject) ||
                    type == typeof(W.DeletedRun) || type == typeof(W.MoveFromRun) || type == typeof(AlternateContent))
                    excludedDepth = reader.Depth;
                else if (type == typeof(W.Table))
                    tableStarts.Push(lines.Count);
                else if (type == typeof(W.TableRow))
                {
                    enclosingRows.Push((cellColumn, continuedCell));
                    cellColumn = 0;
                }
                else if (type == typeof(W.TableCell))
                {
                    cellColumn++;
                    continuedCell = false;
                }
                else if (type == typeof(W.Paragraph))
                {
                    inParagraph = true;
                    paragraph.Clear();
                    if (cellColumn > 0)
                    {
                        // Use the column, not the absolute row, so inserting a table row preserves later matches.
                        paragraph.Append($"表格第 {cellColumn} 列");
                        if (continuedCell) paragraph.Append(" · 续段");
                        continuedCell = true;
                        paragraph.Append("　");
                    }
                }
                else if (inParagraph)
                {
                    if (type == typeof(W.Text)) paragraph.Append(reader.GetText());
                    else if (type == typeof(W.TabChar)) paragraph.Append('\t');
                    else if (type == typeof(W.Break) || type == typeof(W.CarriageReturn)) paragraph.Append('\n');
                    else if (type == typeof(W.NoBreakHyphen)) paragraph.Append('\u2011');
                    else if (type == typeof(W.SoftHyphen)) paragraph.Append('\u00ad');
                }
            }
            else if (reader.IsEndElement)
            {
                if (type == typeof(W.Paragraph))
                {
                    AddLine(paragraph.ToString());
                    inParagraph = false;
                }
                else if (type == typeof(W.TableRow))
                    (cellColumn, continuedCell) = enclosingRows.Pop();
                else if (type == typeof(W.Table) && tableStarts.Pop() == lines.Count)
                    AddLine("（空表格）");
                else if (type == typeof(W.Body))
                    break;
            }
            if (characters + (inParagraph ? paragraph.Length : 0) > MaxCharacters)
                throw new InvalidDataException("Word 正文和表格文字超过 4,194,304 个字符。");
        }
        if (!inBody) throw new InvalidDataException("Word 文档缺少正文。");
        return lines.ToArray();

        void AddLine(string text)
        {
            if (lines.Count >= MaxParagraphs)
                throw new InvalidDataException("Word 正文和表格超过 100,000 段预览上限。");
            lines.Add(text);
            characters += text.Length;
        }
    }
}
