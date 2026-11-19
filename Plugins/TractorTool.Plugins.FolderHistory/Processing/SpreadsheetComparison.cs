namespace TractorTool.Plugins.FolderHistory.Processing;

internal static class SpreadsheetComparison
{
    internal static TextComparisonResult Compare(byte[]? before, byte[]? after, CancellationToken token)
    {
        var original = SpreadsheetWorkbook.Read(before, token);
        var revised = SpreadsheetWorkbook.Read(after, token);
        if (original.Count == 0 && revised.Count == 0)
            return new(false, "未找到可对比的单元格工作表。图表和对话框工作表可另存原件查看。", []);
        var changes = new List<TextDiffLine>();
        var approximate = false;
        foreach (var name in original.Keys.Union(revised.Keys, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            original.TryGetValue(name, out var oldRows);
            revised.TryGetValue(name, out var newRows);
            if (oldRows is null || newRows is null)
            {
                changes.Add(new(oldRows is null ? "新增" : "删除", oldRows is null ? null : 1, newRows is null ? null : 1,
                    oldRows is null ? null : $"工作表 · {name}", newRows is null ? null : $"工作表 · {name}"));
                foreach (var row in (oldRows ?? newRows!))
                    AddCells(name, oldRows is null ? null : row, newRows is null ? null : row, changes, token);
                continue;
            }

            // Row addresses shift on insertion. Match content first, then compare cells at their original addresses.
            var ids = new Dictionary<SheetCell, int>();
            var pairs = TextComparison.AlignLines(Keys(oldRows, ids, token), Keys(newRows, ids, token), token, out var dense);
            approximate |= dense;
            var priorShift = 0;
            var additions = 0;
            var deletions = 0;
            foreach (var pair in pairs)
            {
                token.ThrowIfCancellationRequested();
                var oldRow = pair.Before < 0 ? null : oldRows[pair.Before];
                var newRow = pair.After < 0 ? null : newRows[pair.After];
                var count = changes.Count;
                AddCells(name, oldRow, newRow, changes, token);
                if (oldRow is null) { additions++; continue; }
                if (newRow is null) { deletions++; continue; }

                // A shift across empty rows has no inserted/deleted populated row to explain it.
                var shift = newRow.Number - oldRow.Number;
                var delta = shift - priorShift;
                if (changes.Count == count && shift != 0 && (delta > additions || -delta > deletions))
                    changes.Add(new("修改", oldRow.Number, newRow.Number,
                        $"{name} · 第 {oldRow.Number} 行", $"{name} · 第 {newRow.Number} 行")
                    { BeforeNote = "行位置变化", AfterNote = "行位置变化" });
                priorShift = shift;
                additions = deletions = 0;
            }
        }
        var summary = changes.Count == 0 ? "所比较的工作表、单元格值和公式相同。" : $"工作表和单元格共 {changes.Count:N0} 处变化";
        if (approximate) summary += " · 部分密集差异按行配对";
        return TextComparisonResult.FromChanges(summary, changes, token);
    }

    private static string[] Keys(List<SheetRow> rows, Dictionary<SheetCell, int> ids, CancellationToken token)
    {
        var result = new string[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var cells = rows[i].Cells;
            // Excel can rewrite formula references on row insertion. Align on literal cells when available;
            // AddCells still compares every formula and its saved value.
            var anchors = cells.Any(cell => cell.Formula is null) ? cells.Where(cell => cell.Formula is null) : cells;
            result[i] = string.Join(',', anchors.Select(cell =>
            {
                if (!ids.TryGetValue(cell, out var id)) ids.Add(cell, id = ids.Count);
                return id;
            }));
        }
        return result;
    }

    private static void AddCells(string sheet, SheetRow? before, SheetRow? after, List<TextDiffLine> changes,
        CancellationToken token)
    {
        var oldCells = before?.Cells ?? [];
        var newCells = after?.Cells ?? [];
        var i = 0;
        var j = 0;
        while (i < oldCells.Length || j < newCells.Length)
        {
            token.ThrowIfCancellationRequested();
            var oldColumn = i < oldCells.Length ? oldCells[i].Column : int.MaxValue;
            var newColumn = j < newCells.Length ? newCells[j].Column : int.MaxValue;
            var oldCell = oldColumn <= newColumn ? oldCells[i++] : null;
            var newCell = newColumn <= oldColumn ? newCells[j++] : null;
            if (oldCell == newCell) continue;
            var column = ColumnName(Math.Min(oldColumn, newColumn));
            changes.Add(new(oldCell is null ? "新增" : newCell is null ? "删除" : "修改",
                oldCell is null ? null : before!.Number, newCell is null ? null : after!.Number,
                oldCell?.Display, newCell?.Display)
            {
                BeforeNote = oldCell is null ? "" : $"{sheet}!{column}{before!.Number} · {oldCell.Kind}",
                AfterNote = newCell is null ? "" : $"{sheet}!{column}{after!.Number} · {newCell.Kind}"
            });
        }
    }

    internal static string ColumnName(int column)
    {
        Span<char> name = stackalloc char[3]; // Excel's last column is XFD.
        var i = name.Length;
        while (column > 0)
        {
            column--;
            name[--i] = (char)('A' + column % 26);
            column /= 26;
        }
        return name[i..].ToString();
    }
}

internal sealed record SheetRow(int Number, SheetCell[] Cells);
internal sealed record SheetCell(int Column, string Kind, string? Value, string? Formula)
{
    internal string Display => Formula is null ? Value ?? "" :
        Value is null ? $"{Formula}　（无保存值）" : $"{Formula}　（保存值 {Value}）";
}
