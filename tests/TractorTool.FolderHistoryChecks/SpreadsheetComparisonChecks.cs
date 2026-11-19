using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using TractorTool.Plugins.FolderHistory.Processing;

internal static class SpreadsheetComparisonChecks
{
    internal static void Run()
    {
        var original = WorkbookBytes(["名称", "苹果", "香蕉"], ("报价", [
            RowAt(1, Shared("A1", 0)), RowAt(2, Shared("A2", 1), Number("B2", "7")),
            RowAt(3, Shared("A3", 2), Number("B3", "9"))]));
        var revised = WorkbookBytes([], ("报价", [
            RowAt(1, Inline("A1", "名称")), RowAt(2, Inline("A2", "新增项目")),
            RowAt(3, Inline("A3", "苹果"), Number("B3", "7.00")), RowAt(4, Inline("A4", "香蕉"), Number("B4", "9E0"))]));
        var added = Compare(original, revised).Lines.Single();
        Require(added is { Kind: "新增", AfterLine: 2, AfterText: "新增项目" } && added.AfterNote.Contains("报价!A2"),
            "插入一行或切换共享字符串存储方式不应把后续各行标为修改");

        var priced = WorkbookBytes([], ("报价", [
            RowAt(1, Inline("A1", "苹果"), Number("B1", "1", "LEN(A1)")),
            RowAt(2, Inline("A2", "香蕉"), Number("B2", "1", "LEN(A2)"))]));
        var inserted = WorkbookBytes([], ("报价", [
            RowAt(1, Inline("A1", "新项目")), RowAt(2, Inline("A2", "苹果"), Number("B2", "1", "LEN(A2)")),
            RowAt(3, Inline("A3", "香蕉"), Number("B3", "1", "LEN(A3)"))]));
        var shifted = Compare(priced, inserted).Lines;
        Require(shifted.Count == 3 && shifted[0] is { Kind: "新增", AfterText: "新项目" } &&
            shifted.Skip(1).All(line => line.Kind == "修改" && line.AfterNote.Contains("!B")),
            "插入带相对引用的行应保持其余项目行对齐，同时显示公式引用的实际变化");

        var oldFormula = WorkbookBytes([], ("报价", [RowAt(8, Number("C8", "150", "SUM(A8:B8)"))]));
        var newFormula = WorkbookBytes([], ("报价", [RowAt(8, Number("C8", "150", "A8+B8"))]));
        var formula = Compare(oldFormula, newFormula).Lines.Single();
        Require(formula.Kind == "修改" && formula.BeforeNote.Contains("报价!C8") &&
            formula.BeforeText!.Contains("=SUM(A8:B8)") && formula.AfterText!.Contains("=A8+B8"),
            "公式不同但缓存值相同时仍需显示公式变化");
        var recalculated = WorkbookBytes([], ("报价", [RowAt(8, Number("C8", "160", "A8+B8"))]));
        var cache = Compare(newFormula, recalculated).Lines.Single();
        Require(cache.BeforeText!.Contains("保存值 150") && cache.AfterText!.Contains("保存值 160"), "公式保存值的变化丢失");
        var shared = SharedFormulaBook("A1", "B2");
        Require(Compare(shared, SharedFormulaBook("A1", "9/0")).Lines.Count == 0,
            "共享公式成员单元格自带的公式文本必须由基准公式覆盖");
        var updatedGroup = Compare(shared, SharedFormulaBook("A1+1", "B2")).Lines;
        Require(updatedGroup.Count == 2 && updatedGroup[1].AfterText!.Contains("基准 B1") &&
            updatedGroup[1].AfterText!.Contains("=A1+1"), "共享公式的基准变化必须反映到成员单元格上");
        var runs = new Cell(new InlineString(new Run(new Text("_x00")), new Run(new Text("0D_"))))
            { CellReference = "C1", DataType = CellValues.InlineString };
        var cachedText = Number("D1", "换_x000D_行", "A1");
        cachedText.DataType = CellValues.String;
        var escaped = WorkbookBytes(["换_x000D_行"], ("转义", [
            RowAt(1, Shared("A1", 0), Inline("B1", "_x005F_x000D_"), runs, cachedText)]));
        var decoded = Compare(null, escaped).Lines;
        Require(decoded[1].AfterText == "换\r行" && decoded[2].AfterText == "_x000D_" &&
            decoded[3].AfterText == "_x000D_" && decoded[4].AfterText!.Contains("换\r行"),
            "共享字符串、内联富文本和保存值应逐文本片段解码一次");

        var sparse = WorkbookBytes([], ("报价", [RowAt(10, Number("C10", "150", "A8+B8"))]));
        var moved = Compare(newFormula, sparse).Lines.Single();
        Require(moved.BeforeLine == 8 && moved.AfterLine == 10, "仅跨空行移动内容不应该显示无变化");
        var emptySheet = WorkbookBytes([], ("空表", []));
        Require(Compare(null, emptySheet).Lines.Single() is { Kind: "新增", AfterText: "工作表 · 空表" }, "新增空表不可见");
        Require(Compare(emptySheet, null).Lines.Single() is { Kind: "删除", BeforeText: "工作表 · 空表" }, "删除空表不可见");
        var longValue = new string('文', 32_767);
        var longCells = WorkbookBytes([], ("空表",
            Enumerable.Range(1, 4).Select(i => RowAt((uint)i, Inline($"A{i}", longValue))).ToArray()));
        var pages = OfficeComparison.Compare(OfficeFormat.Excel, emptySheet, longCells, CancellationToken.None);
        Require(pages.PageCount > 1 && pages.GetPage(1).Lines.All(line => line.AfterNote.Contains("空表!A")),
            "跨页的长单元格片段丢失了工作表地址");
        var segments = Enumerable.Range(0, pages.PageCount).SelectMany(i => pages.GetPage(i).Lines).ToArray();
        Require(segments.All(line => line.AfterText is { Length: <= 4096 }) &&
            segments.Sum(line => line.AfterText?.Length ?? 0) == 4 * longValue.Length,
            "单元格分页丢失原文或渲染了过长的片段");

        var invalid = WorkbookBytes([], ("报价", [RowAt(1, Shared("A1", 5))]));
        RequireInvalid(invalid, "缺失共享字符串时不应输出成功的比较结果");
        RequireInvalid(Edit(emptySheet, part => part.WorksheetParts.Single().Worksheet!.RemoveAllChildren<SheetData>()),
            "缺少 sheetData 与合法的空工作表不能混淆");
        RequireInvalid(Edit(emptySheet, part => part.Workbook!.Sheets!.Elements<Sheet>().Single().Id = "missing"),
            "丢失工作表关联不能当作空表");
        var charts = Edit(emptySheet, part =>
        {
            var sheet = part.Workbook!.Sheets!.Elements<Sheet>().Single();
            part.DeletePart(part.WorksheetParts.Single());
            var chart = part.AddNewPart<ChartsheetPart>();
            chart.Chartsheet = new Chartsheet();
            sheet.Id = part.GetIdOfPart(chart);
        });
        Require(!Compare(null, charts).IsText, "只有图表的新增工作簿不能被报告为单元格相同");
    }

    private static TextComparisonResult Compare(byte[]? before, byte[]? after) =>
        SpreadsheetComparison.Compare(before, after, CancellationToken.None);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void RequireInvalid(byte[] workbook, string message)
    {
        try { Compare(null, workbook); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }

    private static byte[] Edit(byte[] file, Action<WorkbookPart> edit)
    {
        using var stream = new MemoryStream();
        stream.Write(file);
        stream.Position = 0;
        using (var document = SpreadsheetDocument.Open(stream, true))
            edit(document.WorkbookPart!);
        return stream.ToArray();
    }

    private static Row RowAt(uint index, params Cell[] cells) => new(cells) { RowIndex = index };
    private static Cell Shared(string address, int index) => new(new CellValue(index)) { CellReference = address, DataType = CellValues.SharedString };
    private static Cell Inline(string address, string text) => new(new InlineString(new Text(text))) { CellReference = address, DataType = CellValues.InlineString };
    private static Cell Number(string address, string value, string? formula = null)
    {
        var cell = new Cell { CellReference = address };
        if (formula is not null) cell.CellFormula = new CellFormula(formula);
        cell.CellValue = new CellValue(value);
        return cell;
    }

    private static byte[] SharedFormulaBook(string master, string follower)
    {
        var first = Number("B1", "1");
        first.CellFormula = new CellFormula(master) { FormulaType = CellFormulaValues.Shared, Reference = "B1:B2", SharedIndex = 0U };
        var second = Number("B2", "2");
        second.CellFormula = new CellFormula(follower) { FormulaType = CellFormulaValues.Shared, SharedIndex = 0U };
        return WorkbookBytes([], ("公式", [RowAt(1, first), RowAt(2, second)]));
    }

    private static byte[] WorkbookBytes(string[] strings, params (string Name, Row[] Rows)[] source)
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var part = document.AddWorkbookPart();
            var sheets = new Sheets();
            part.Workbook = new Workbook(sheets);
            if (strings.Length != 0)
            {
                var shared = part.AddNewPart<SharedStringTablePart>();
                shared.SharedStringTable = new SharedStringTable(strings.Select(text => new SharedStringItem(new Text(text))));
            }
            uint sheetId = 0;
            foreach (var (name, rows) in source)
            {
                var worksheet = part.AddNewPart<WorksheetPart>();
                worksheet.Worksheet = new Worksheet(new SheetData(rows));
                sheets.Append(new Sheet { Name = name, Id = part.GetIdOfPart(worksheet), SheetId = ++sheetId });
            }
        }
        return stream.ToArray();
    }
}
