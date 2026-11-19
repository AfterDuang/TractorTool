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
        Require(Compare(original, original).Lines.Count == 0 && Compare(original, original.ToArray()).Lines.Count == 0,
            "相同字节的工作簿应该保留正常的相同比较结果");

        var labels = WorkbookBytes([], ("重复标签", [
            RowAt(1, Inline("A1", "项目"), Number("B1", "1", "1")),
            RowAt(2, Inline("A2", "项目"), Number("B2", "2", "2"))]));
        var extraLabel = WorkbookBytes([], ("重复标签", [
            RowAt(1, Inline("A1", "项目"), Number("B1", "0", "0")),
            RowAt(2, Inline("A2", "项目"), Number("B2", "1", "1")),
            RowAt(3, Inline("A3", "项目"), Number("B3", "2", "2"))]));
        var duplicateInsertion = Compare(labels, extraLabel).Lines;
        Require(duplicateInsertion.Count == 2 && duplicateInsertion.All(line => line.Kind == "新增" && line.AfterLine == 1),
            "重复标签行必须利用公式区分，不能把开头插行错报为后续公式批量修改");
        var duplicateDeletion = Compare(extraLabel, labels).Lines;
        Require(duplicateDeletion.Count == 2 && duplicateDeletion.All(line => line.Kind == "删除" && line.BeforeLine == 1),
            "重复标签的删除应该与新增对称，保留其余行的身份");
        CheckRepeatedFormulaRows();

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
        Require(updatedGroup.Count == 2 && updatedGroup[1].BeforeText!.StartsWith("=A2") &&
            updatedGroup[1].AfterText!.StartsWith("=A2+1"), "共享公式必须按成员地址显示有效表达式");
        var expanded = WorkbookBytes([], ("公式", [RowAt(1, Number("B1", "1", "A1")), RowAt(2, Number("B2", "2", "A2"))]));
        Require(Compare(shared, expanded).Lines.Count == 0 && Compare(expanded, shared).Lines.Count == 0,
            "共享与普通公式只是存储方式改变时不能报内容修改");
        var sharedRows = WorkbookBytes([], ("公式", [
            RowAt(1, Inline("A1", "甲"), FormulaMember("B1", "LEN(A1)", "B1:B2")),
            RowAt(2, Inline("A2", "乙"), FormulaMember("B2"))]));
        var insertedSharedRow = WorkbookBytes([], ("公式", [
            RowAt(1, Inline("A1", "甲"), FormulaMember("B1", "LEN(A1)", "B1:B3")),
            RowAt(2, Inline("A2", "新"), FormulaMember("B2")),
            RowAt(3, Inline("A3", "乙"), FormulaMember("B3"))]));
        var sharedMove = Compare(sharedRows, insertedSharedRow).Lines;
        Require(sharedMove.Count == 3 && sharedMove.Count(line => line.Kind == "新增") == 2 &&
            sharedMove.Single(line => line.Kind == "修改") is { BeforeLine: 2, AfterLine: 3 } movedFormula &&
            movedFormula.BeforeText!.StartsWith("=LEN(A2)") && movedFormula.AfterText!.StartsWith("=LEN(A3)"),
            "共享公式插行后，即使基准和保存值不变，也必须显示成员引用的变化");
        CheckSharedReferences();
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
        try { Compare(invalid, invalid); throw new InvalidOperationException("相同字节不能跳过首次结构检查"); }
        catch (InvalidDataException) { }
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
        Require(!Compare(charts, charts).IsText, "相同字节的纯图表工作簿不能被报告为单元格相同");
    }

    private static void CheckSharedReferences()
    {
        const string original = "=SUM(A1,$A1,A$1,$A$1,A1:B2,A:A,$B:$C,1:2,$3:$4,'Q1''s'!A1,LOG10(100),1E+10,\"A1 \"\"B2\"\"\")";
        const string expanded = "=SUM(C3,$A3,C$1,$A$1,C3:D4,C:C,$B:$C,3:4,$3:$4,'Q1''s'!C3,LOG10(100),1E+10,\"A1 \"\"B2\"\"\")";
        var formula = new SharedSpreadsheetFormula(original, "B2:D4", 2, 2);
        Require(formula.At(4, 4, 4096, default) == expanded,
            "共享公式应平移相对引用，并保留绝对引用、范围、工作表名称、函数和字符串");
        Require(new SharedSpreadsheetFormula("=LOG10!a1+_A1+1E10", "B2:D4", 2, 2).At(4, 4, 4096, default) == "=LOG10!c3+_A1+1E10",
            "工作表名前缀、名称、科学记数和小写引用不能被错误重写");
        foreach (var expression in new[] { "SUM(Table1[Price])", "[1]Sheet1!A1", "'[book.xlsx]Sheet 1'!A1", "SUM(Sheet1:Sheet3!A1)", "A1#", "A1 (B1)", "A1.Price" })
        {
            var result = OfficeComparison.Compare(OfficeFormat.Excel, null, SharedFormulaBook(expression, ""), default);
            Require(!result.IsText && result.Summary.Contains("共享公式") && result.Summary.Contains("另存"),
                "不支持的共享引用必须明确降级，不能猜测公式或报告内容相同");
        }
        RequireRejected(() => new SharedSpreadsheetFormula("=A9", "A1:A2", 1, 1).At(2, 1, 3, default),
            "展开后的共享公式没有遵循剩余字符预算");
        RequireRejected(() => formula.At(5, 4, 4096, default), "共享公式成员不能越出声明范围");
        RequireRejected(() => new SharedSpreadsheetFormula("=A1", "B2:C3", 2, 3).At(3, 2, 4096, default),
            "越出工作表边界的相对引用不能被猜测为合法公式");

        static void RequireRejected(Action action, string message)
        {
            try { action(); }
            catch (InvalidDataException) { return; }
            throw new InvalidOperationException(message);
        }
    }

    private static void CheckRepeatedFormulaRows()
    {
        var original = RepeatedRows(("100", "LEN(A1)+98"), ("200", "LEN(A2)+198"));
        var inserted = RepeatedRows(("300", "LEN(A1)+298"), ("100", "LEN(A2)+98"), ("200", "LEN(A3)+198"));
        var changes = Compare(original, inserted).Lines;
        Require(changes.Count == 4 && changes.Take(2).All(line => line.Kind == "新增" && line.AfterLine == 1) &&
            changes[2] is { Kind: "修改", BeforeLine: 1, AfterLine: 2 } &&
            changes[3] is { Kind: "修改", BeforeLine: 2, AfterLine: 3 } &&
            changes[2].BeforeText!.Contains("保存值 100") && changes[2].AfterText!.Contains("保存值 100") &&
            changes[3].BeforeText!.Contains("保存值 200") && changes[3].AfterText!.Contains("保存值 200"),
            "重复标签的相对公式被插行改写后，应按唯一保存值对齐并显示真实引用变化");
        var deleted = Compare(inserted, original).Lines;
        Require(deleted.Count == 4 && deleted.Take(2).All(line => line.Kind == "删除" && line.BeforeLine == 1) &&
            deleted[2] is { BeforeLine: 2, AfterLine: 1 } && deleted[3] is { BeforeLine: 3, AfterLine: 2 },
            "重复标签相对公式的删行也应保留原行对应关系");

        var sameValues = RepeatedRows(("1", "1"), ("1", "2-1"));
        var anotherFormula = RepeatedRows(("1", "3-2"), ("1", "1"), ("1", "2-1"));
        var exact = Compare(sameValues, anotherFormula).Lines;
        Require(exact.Count == 2 && exact.All(line => line.Kind == "新增" && line.AfterLine == 1),
            "保存值相同但公式不同的行应优先保留完整公式匹配，不能被缓存值重新配对");
        var ambiguous = Compare(RepeatedRows(("1", "1"), ("1", "1")), RepeatedRows(("1", "1"), ("1", "1"), ("1", "1")));
        Require(ambiguous.Summary.Contains("无法唯一对应"), "内容完全重复时不能暗示已确定插行位置");

        static byte[] RepeatedRows(params (string Value, string Formula)[] cells) => WorkbookBytes([], ("重复标签",
            cells.Select((cell, i) => RowAt((uint)(i + 1), Inline($"A{i + 1}", "项目"), Number($"B{i + 1}", cell.Value, cell.Formula))).ToArray()));
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

    private static Cell FormulaMember(string address, string formula = "", string? range = null)
    {
        var cell = Number(address, "1");
        cell.CellFormula = new CellFormula(formula) { FormulaType = CellFormulaValues.Shared, Reference = range, SharedIndex = 0U };
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
