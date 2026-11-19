namespace TractorTool.Plugins.FolderHistory.Processing;

internal static class SpreadsheetComparison
{
    internal static TextComparisonResult Compare(byte[]? before, byte[]? after, CancellationToken token)
    {
        var original = SpreadsheetWorkbook.Read(before, token);
        var identical = ReferenceEquals(before, after) || before is not null && after is not null && before.AsSpan().SequenceEqual(after);
        var revised = identical ? original : SpreadsheetWorkbook.Read(after, token);
        if (original.Count == 0 && revised.Count == 0)
            return new(false, "未找到可对比的单元格工作表。图表和对话框工作表可另存原件查看。", []);
        const string unchanged = "所比较的工作表、单元格值和公式相同。";
        if (identical) return TextComparisonResult.FromChanges(unchanged, [], token);
        var changes = new List<TextDiffLine>();
        var approximate = false;
        var ambiguousRows = false;
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
            var (oldKeys, newKeys, repeatedMatches) = PrepareRowKeys(oldRows, newRows, token);
            var pairs = TextComparison.AlignLines(oldKeys, newKeys, token, out var dense);
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
                var oldKey = oldKeys[pair.Before];
                var newKey = newKeys[pair.After];
                ambiguousRows |= oldKey == newKey ? repeatedMatches.Contains(oldKey) :
                    oldKey.StartsWith("full:", StringComparison.Ordinal) || newKey.StartsWith("full:", StringComparison.Ordinal) ||
                    oldKey.StartsWith("value:", StringComparison.Ordinal) || newKey.StartsWith("value:", StringComparison.Ordinal);

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
        var summary = changes.Count == 0 ? unchanged : $"工作表和单元格共 {changes.Count:N0} 处变化";
        if (approximate) summary += " · 部分密集差异按行配对";
        if (ambiguousRows) summary += " · 部分重复行无法唯一对应，按顺序配对";
        return TextComparisonResult.FromChanges(summary, changes, token);
    }

    private static (string[] Before, string[] After, HashSet<string> Repeated) PrepareRowKeys(
        List<SheetRow> before, List<SheetRow> after, CancellationToken token)
    {
        var ids = new Dictionary<SheetCell, int>();
        var oldKeys = Keys(before, ids, token);
        var newKeys = Keys(after, ids, token);
        var repeated = RepeatedKeys(oldKeys, newKeys, token);
        if (repeated.Count == 0) return (oldKeys, newKeys, repeated);

        UseFullRowKeys(before, oldKeys, repeated, ids, token);
        UseFullRowKeys(after, newKeys, repeated, ids, token);
        var oldExact = new HashSet<string>(oldKeys, StringComparer.Ordinal);
        var newExact = new HashSet<string>(newKeys, StringComparer.Ordinal);
        var oldValues = UniqueSavedValues(before, oldKeys, newExact, ids, token);
        var newValues = UniqueSavedValues(after, newKeys, oldExact, ids, token);
        foreach (var (key, oldIndex) in oldValues)
        {
            token.ThrowIfCancellationRequested();
            if (oldIndex >= 0 && newValues.TryGetValue(key, out var newIndex) && newIndex >= 0)
                oldKeys[oldIndex] = newKeys[newIndex] = "value:" + key;
        }
        return (oldKeys, newKeys, RepeatedKeys(oldKeys, newKeys, token));
    }

    private static void UseFullRowKeys(List<SheetRow> rows, string[] keys, HashSet<string> repeated,
        Dictionary<SheetCell, int> ids, CancellationToken token)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            // Repeated labels alone cannot identify a row; retain its formulas and saved values too.
            if (repeated.Contains(keys[i])) keys[i] = "full:" + CellKey(rows[i].Cells, ids);
        }
    }

    private static Dictionary<string, int> UniqueSavedValues(List<SheetRow> rows, string[] keys,
        HashSet<string> otherExact, Dictionary<SheetCell, int> ids, CancellationToken token)
    {
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < rows.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            // Preserve exact formula matches before using saved values to follow rewritten relative references.
            if (!keys[i].StartsWith("full:", StringComparison.Ordinal) || otherExact.Contains(keys[i]) ||
                !rows[i].Cells.Any(cell => cell.Formula is not null && cell.Value is not null)) continue;
            var key = CellKey(rows[i].Cells.Select(cell => cell.Formula is null ? cell : cell with { Formula = null }), ids);
            if (!positions.TryAdd(key, i)) positions[key] = -1;
        }
        return positions;
    }

    private static HashSet<string> RepeatedKeys(string[] before, string[] after, CancellationToken token)
    {
        var repeated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var keys in new[] { before, after })
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                token.ThrowIfCancellationRequested();
                if (!seen.Add(key)) repeated.Add(key);
            }
        }
        return repeated;
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
            result[i] = CellKey(anchors, ids);
        }
        return result;
    }

    private static string CellKey(IEnumerable<SheetCell> cells, Dictionary<SheetCell, int> ids) =>
        string.Join(',', cells.Select(cell =>
        {
            if (!ids.TryGetValue(cell, out var id)) ids.Add(cell, id = ids.Count);
            return id;
        }));

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
