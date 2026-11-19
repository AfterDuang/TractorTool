using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using TractorTool.Plugins.FolderHistory.Processing;
using W = DocumentFormat.OpenXml.Wordprocessing;

internal static class WordComparisonChecks
{
    internal static void Run()
    {
        var before = Document(new W.Paragraph(new W.Run(new W.Text("日期为 15 日"))), Table("甲", "乙"));
        var after = Document(new W.Paragraph(new W.Run(new W.Text("日期为 ")),
            new W.Run(new W.RunProperties(new W.Bold()), new W.Text("20 日"))), Table("甲", "丙"));
        var changes = WordComparison.Compare(before, after, default);
        Assert(changes.Lines.Where(row => row.Kind == "修改").Select(row => row.After.Changed)
            .SequenceEqual(["20", "丙"]), "Word paragraph and table edits were not aligned or highlighted");
        Assert(changes.Lines.Single(row => row.After.Changed == "丙").AfterText!.Contains("第 2 列"),
            "Word table change lost its column");

        var formatted = Document(new W.Paragraph(new W.Run(new W.Text("日期为 ")),
            new W.Run(new W.RunProperties(new W.Italic()), new W.Text("15 日"))), Table("甲", "乙"));
        var same = WordComparison.Compare(before, formatted, default);
        Assert(same.IsText && same.Lines.Count == 0 && same.Summary.Contains("正文和表格文字没有变化"),
            "Word run splits or formatting were reported as text edits");

        var cells = Document(Table("甲", "乙"));
        var twoParagraphs = Document(new W.Table(new W.TableRow(new W.TableCell(Paragraph("甲"), Paragraph("乙")))));
        Assert(WordComparison.Compare(cells, twoParagraphs, default).Lines.Any(row => row.Kind == "修改"),
            "Word table cell boundaries were lost");
        var withEmptyParagraph = Document(Paragraph("前"), new W.Paragraph(), Paragraph("后"));
        Assert(WordComparison.Compare(null, withEmptyParagraph, default).Lines
            .Select(row => row.AfterText).SequenceEqual(["前", "", "后"]), "Word addition lost an empty paragraph");
        Assert(WordComparison.Compare(Document(), null, default).Summary.Contains("删除"),
            "deleting an empty Word document was described as unchanged");
        var revised = Document(new W.Paragraph(
            new W.DeletedRun(new W.Run(new W.DeletedText("旧称"))),
            new W.InsertedRun(new W.Run(new W.Text("现名"))),
            new W.Run(new W.TabChar(), new W.Text("第一行"), new W.Break(), new W.Text("第二行"))));
        Assert(WordComparison.Compare(null, revised, default).Lines.Single().AfterText == "现名\t第一行\n第二行",
            "Word revision text, tab or manual break changed in preview");
    }

    private static W.Paragraph Paragraph(string text) => new(new W.Run(new W.Text(text)));

    private static W.Table Table(string first, string second) =>
        new(new W.TableRow(new W.TableCell(Paragraph(first)), new W.TableCell(Paragraph(second))));

    private static byte[] Document(params OpenXmlElement[] blocks)
    {
        using var stream = new MemoryStream();
        using (var package = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
            package.AddMainDocumentPart().Document = new W.Document(new W.Body(blocks));
        return stream.ToArray();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
