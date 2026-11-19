using System.Globalization;
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
        var enclosingRows = new Stack<(int Column, bool Continued, int RowDepth, int CellDepth)>();
        var tableStarts = new Stack<(int Lines, int DeletedRows)>();
        var cellColumn = 0;
        var continuedCell = false;
        var characters = 0;
        var paragraphCount = 0;
        var deletedRows = 0;
        var rowDepth = -1;
        var cellDepth = -1;
        var paragraphDepth = -1;
        var excludedDepth = int.MaxValue;
        var inBody = false;
        var inParagraph = false;
        var deletedParagraphMark = false;
        var joinNextParagraph = false;
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
                {
                    RequireParagraphBoundary();
                    tableStarts.Push((lines.Count, deletedRows));
                }
                else if (type == typeof(W.TableRow))
                {
                    enclosingRows.Push((cellColumn, continuedCell, rowDepth, cellDepth));
                    cellColumn = 0;
                    rowDepth = reader.Depth;
                    cellDepth = -1;
                }
                else if (type == typeof(W.TableCell))
                {
                    RequireParagraphBoundary();
                    cellColumn++;
                    continuedCell = false;
                    cellDepth = reader.Depth;
                }
                else if (type == typeof(W.Deleted) && rowDepth >= 0 && reader.Depth == rowDepth + 2)
                {
                    // Current properties sit directly below the row; trPrChange contains obsolete markers.
                    deletedRows++;
                    excludedDepth = rowDepth;
                }
                else if (type == typeof(W.CellDeletion) && cellDepth >= 0 && reader.Depth == cellDepth + 2)
                {
                    cellColumn--;
                    excludedDepth = cellDepth;
                }
                else if (type == typeof(W.Paragraph))
                {
                    if (++paragraphCount > MaxParagraphs)
                        throw new InvalidDataException("Word 正文和表格超过 100,000 段预览上限。");
                    if (joinNextParagraph && reader.Depth != paragraphDepth)
                        throw new InvalidDataException("Word 含有跨容器的段落修订，无法完整对比。");
                    inParagraph = true;
                    paragraphDepth = reader.Depth;
                    deletedParagraphMark = false;
                    if (!joinNextParagraph)
                    {
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
                    joinNextParagraph = false;
                }
                else if (inParagraph)
                {
                    if (type == typeof(W.Text)) paragraph.Append(reader.GetText());
                    else if (type == typeof(W.Deleted) && reader.Depth == paragraphDepth + 3)
                        deletedParagraphMark = true;
                    else if (type == typeof(W.SymbolChar))
                    {
                        var symbol = (W.SymbolChar)reader.LoadCurrentElement()!;
                        var font = symbol.Font?.Value;
                        var code = symbol.Char?.Value;
                        if (string.IsNullOrWhiteSpace(font) || code?.Length != 4 ||
                            !ushort.TryParse(code, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
                            throw new InvalidDataException("Word 正文含有无效的符号字符。");
                        paragraph.Append($"〔符号 {font}:{value:X4}〕");
                    }
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
                    joinNextParagraph = deletedParagraphMark;
                    if (!joinNextParagraph)
                    {
                        AddLine(paragraph.ToString());
                        paragraph.Clear();
                    }
                    inParagraph = false;
                }
                else if (type == typeof(W.TableCell))
                {
                    RequireParagraphBoundary();
                    cellDepth = -1;
                }
                else if (type == typeof(W.TableRow))
                    (cellColumn, continuedCell, rowDepth, cellDepth) = enclosingRows.Pop();
                else if (type == typeof(W.Table))
                {
                    var start = tableStarts.Pop();
                    if (start == (lines.Count, deletedRows)) AddLine("（空表格）");
                }
                else if (type == typeof(W.Body))
                {
                    RequireParagraphBoundary();
                    break;
                }
            }
            if (characters + paragraph.Length > MaxCharacters)
                throw new InvalidDataException("Word 正文和表格文字超过 4,194,304 个字符。");
        }
        if (!inBody) throw new InvalidDataException("Word 文档缺少正文。");
        return lines.ToArray();

        void RequireParagraphBoundary()
        {
            if (joinNextParagraph)
                throw new InvalidDataException("Word 含有跨表格边界或末尾的段落修订，无法完整对比。");
        }

        void AddLine(string text)
        {
            if (lines.Count >= MaxParagraphs)
                throw new InvalidDataException("Word 正文和表格超过 100,000 段预览上限。");
            lines.Add(text);
            characters += text.Length;
        }
    }
}
