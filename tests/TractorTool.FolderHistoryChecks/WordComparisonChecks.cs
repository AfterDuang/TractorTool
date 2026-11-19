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
        CheckRevisionBoundaries();
        CheckSymbols();
    }

    private static void CheckRevisionBoundaries()
    {
        var separate = Document(Paragraph("甲"), Paragraph("乙"), Paragraph("丙"));
        var joined = Document(DeletedParagraphMark("甲"), DeletedParagraphMark("乙"), Paragraph("丙"));
        Assert(WordComparison.Compare(null, joined, default).Lines.Single().AfterText == "甲乙丙" &&
            WordComparison.Compare(separate, joined, default).Lines.Any(row => row.Kind != "相同") &&
            WordComparison.Compare(Document(Paragraph("甲乙丙")), joined, default).Lines.Count == 0,
            "deleted paragraph marks did not merge consecutive paragraphs into the final revision");
        Assert(WordComparison.Compare(null, Document(DeletedParagraphMark(""), Paragraph("后")), default)
            .Lines.Single().AfterText == "后", "an empty deleted paragraph mark left an extra paragraph");

        var previousParagraph = new W.Paragraph(new W.ParagraphProperties(new W.ParagraphMarkRunProperties(
            new W.ParagraphMarkRunPropertiesChange(new W.PreviousParagraphMarkRunProperties(new W.Deleted { Id = "1" }))
                { Id = "2" })), new W.Run(new W.Text("甲")));
        var previousRow = new W.TableRow(new W.TableRowProperties(new W.TableRowPropertiesChange(
            new W.PreviousTableRowProperties(new W.Deleted { Id = "3" })) { Id = "4" }), new W.TableCell(Paragraph("保留")));
        Assert(WordComparison.Compare(Document(Paragraph("甲"), Paragraph("乙"), new W.Table(Row("保留"))),
            Document(previousParagraph, Paragraph("乙"), new W.Table(previousRow)), default).Lines.Count == 0,
            "obsolete paragraph or row property revisions affected the current text");

        var withDeletedRow = Document(new W.Table(DeletedRow("删除"), Row("保留")));
        Assert(WordComparison.Compare(Document(new W.Table(Row("保留"))), withDeletedRow, default).Lines.Count == 0,
            "a deleted table row was included or shifted the next row's column");
        Assert(WordComparison.Compare(Document(), Document(new W.Table(DeletedRow("删除"))), default).Lines.Count == 0,
            "deleting every table row left a synthetic empty table");
        var withDeletedCell = Document(new W.Table(new W.TableRow(
            new W.TableCell(new W.TableCellProperties(new W.CellDeletion { Id = "1" }), Paragraph("删除")),
            new W.TableCell(Paragraph("保留")))));
        Assert(WordComparison.Compare(Document(new W.Table(Row("保留"))), withDeletedCell, default).Lines.Count == 0,
            "a deleted table cell was included or shifted the following cell");

        var nested = Document(new W.Table(new W.TableRow(new W.TableCell(
            DeletedParagraphMark("外"), Paragraph("层"),
            new W.Table(DeletedRow("内删"), Row("内留")), Paragraph("外续")))));
        Assert(WordComparison.Compare(null, nested, default).Lines.Select(row => row.AfterText).SequenceEqual(
            ["表格第 1 列　外层", "表格第 1 列　内留", "表格第 1 列 · 续段　外续"]),
            "nested rows lost the outer cell or joined paragraphs gained duplicate labels");

        var crossingCell = Document(new W.Table(new W.TableRow(
            new W.TableCell(DeletedParagraphMark("甲")), new W.TableCell(Paragraph("乙")))));
        Assert(!OfficeComparison.Compare(OfficeFormat.Word, null, crossingCell, default).IsText &&
            !OfficeComparison.Compare(OfficeFormat.Word, null, Document(DeletedParagraphMark("文末")), default).IsText,
            "a paragraph deletion across a cell or document boundary was silently accepted");
        var oversizedJoin = Document(DeletedParagraphMark(new string('a', 2 * 1024 * 1024)),
            Paragraph(new string('b', 2 * 1024 * 1024 + 1)));
        Assert(!OfficeComparison.Compare(OfficeFormat.Word, null, oversizedJoin, default).IsText,
            "joining deleted paragraph marks bypassed the character limit");
    }

    private static void CheckSymbols()
    {
        var original = Symbol("Wingdings", "F0FC");
        var changed = WordComparison.Compare(original, Symbol("Wingdings", "F0FB"), default);
        Assert(changed.Lines.Single().BeforeText == "审批结果：〔符号 Wingdings:F0FC〕" &&
            changed.Lines.Single().AfterText == "审批结果：〔符号 Wingdings:F0FB〕" &&
            changed.Lines.Single().Kind == "修改", "a changed body symbol was omitted from the comparison");
        Assert(WordComparison.Compare(original, Symbol("Symbol", "F0FC"), default).Lines.Single().Kind == "修改",
            "a symbol font change was treated as formatting-only");
        Assert(WordComparison.Compare(original, Symbol("Wingdings", "f0fc"), default).Lines.Count == 0,
            "hexadecimal spelling alone changed a symbol's identity");
        Assert(!OfficeComparison.Compare(OfficeFormat.Word, null, Symbol("Wingdings", "ZZZZ"), default).IsText &&
            !OfficeComparison.Compare(OfficeFormat.Word, null, Symbol("", "F0FC"), default).IsText,
            "an invalid symbol was silently omitted");
        var deleted = Document(new W.Paragraph(new W.Run(new W.Text("审批结果：")),
            new W.DeletedRun(new W.Run(new W.SymbolChar { Font = "Wingdings", Char = "F0FB" })),
            new W.Run(new W.SymbolChar { Font = "Wingdings", Char = "F0FC" })));
        Assert(WordComparison.Compare(original, deleted, default).Lines.Count == 0,
            "a deleted symbol leaked into the final revision");
    }

    private static byte[] Symbol(string font, string code) => Document(new W.Paragraph(new W.Run(
        new W.Text("审批结果："), new W.SymbolChar { Font = font, Char = code })));

    private static W.Paragraph DeletedParagraphMark(string text) => new(
        new W.ParagraphProperties(new W.ParagraphMarkRunProperties(new W.Deleted { Id = "1" })),
        new W.Run(new W.Text(text)));

    private static W.TableRow Row(string text) => new(new W.TableCell(Paragraph(text)));

    private static W.TableRow DeletedRow(string text) => new(new W.TableRowProperties(new W.Deleted { Id = "1" }),
        new W.TableCell(Paragraph(text)));

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
