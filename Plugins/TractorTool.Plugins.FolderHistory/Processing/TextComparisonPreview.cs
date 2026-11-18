using System.Globalization;

namespace TractorTool.Plugins.FolderHistory.Processing;

internal sealed class TextComparisonPreview
{
    private const int ContextLines = 3;
    private const int SegmentLength = 4096;
    private const int CharacterContext = 80;
    private const int PageCharacters = 64 * 1024;
    private readonly List<DisplayLine> _lines = [];
    private readonly List<int> _pageStarts = [0];
    private readonly string _summary;
    private readonly int _rowCount;

    public TextComparisonPreview(SourceTextLine[] before, SourceTextLine[] after, IReadOnlyList<ComparedTextLine> rows,
        string oldEncoding, string newEncoding, bool pairedLargeRange, CancellationToken token)
    {
        var added = 0;
        var modified = 0;
        var deleted = 0;
        var differentEndings = false;
        var rowCount = 0;
        var pageRows = 0;
        var pageCharacters = 0;
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            if (row.Before < 0) added++;
            else if (row.After < 0) deleted++;
            else
            {
                if (before[row.Before].Text != after[row.After].Text) modified++;
                differentEndings |= before[row.Before].Ending != after[row.After].Ending;
            }
        }
        _summary = $"新增 {added} 行 · 修改 {modified} 行 · 删除 {deleted} 行";
        if (differentEndings) _summary += " · 换行方式有变化";
        if (oldEncoding != newEncoding) _summary += $" · {oldEncoding} → {newEncoding}";
        if (pairedLargeRange) _summary += " · 部分密集差异按行配对，可能不是最少改动";

        for (var index = 0; index < rows.Count;)
        {
            token.ThrowIfCancellationRequested();
            if (rows.Count <= TextComparison.PageSize || !Unchanged(rows[index])) { AddRow(rows[index++]); continue; }
            var start = index;
            while (index < rows.Count && Unchanged(rows[index]))
            {
                if ((index & 1023) == 0) token.ThrowIfCancellationRequested();
                index++;
            }
            var leading = start == 0 && index != rows.Count ? 0 : Math.Min(ContextLines, index - start);
            var trailing = index == rows.Count ? 0 : Math.Min(ContextLines, index - start - leading);
            for (var offset = 0; offset < leading; offset++) AddRow(rows[start + offset]);
            var omittedStart = start + leading;
            var omittedEnd = index - trailing;
            if (omittedEnd > omittedStart)
            {
                var first = rows[omittedStart];
                var last = rows[omittedEnd - 1];
                var count = omittedEnd - omittedStart;
                var gap = $"已折叠 {count:N0} 行未变化内容 · 原 {first.Before + 1}–{last.Before + 1} / 新 {first.After + 1}–{last.After + 1}";
                AddDisplayLine(new("省略", default, default, rowCount, 1, false, gap, count));
            }
            for (var offset = index - trailing; offset < index; offset++) AddRow(rows[offset]);
        }
        _rowCount = rowCount;

        bool Unchanged(ComparedTextLine row) => row.Before >= 0 && row.After >= 0 &&
            before[row.Before] == after[row.After];

        void AddRow(ComparedTextLine row)
        {
            token.ThrowIfCancellationRequested();
            var oldLine = row.Before >= 0 ? before[row.Before] : (SourceTextLine?)null;
            var newLine = row.After >= 0 ? after[row.After] : (SourceTextLine?)null;
            var kind = oldLine is null ? "新增" : newLine is null ? "删除" : oldLine.Value.Text == newLine.Value.Text ? "相同" : "修改";
            var endingChanged = oldLine is { } old && newLine is { } next && old.Ending != next.Ending;
            var bounds = kind == "修改" ? ChangeBounds(oldLine!.Value.Text, newLine!.Value.Text, token) : (0, oldLine?.Text.Length ?? 0, newLine?.Text.Length ?? 0);
            var left = Side(oldLine, row.Before, bounds.Item1, bounds.Item2, kind == "相同");
            var right = Side(newLine, row.After, bounds.Item1, bounds.Item3, kind == "相同");
            var count = Math.Max(1, (Math.Max(left.End - left.Start, right.End - right.Start) + SegmentLength - 1) / SegmentLength);
            AddDisplayLine(new(kind, left, right, rowCount, count, endingChanged, "", 0));
        }

        void AddDisplayLine(DisplayLine line)
        {
            _lines.Add(line);
            for (var part = 0; part < line.RowCount; part++)
            {
                token.ThrowIfCancellationRequested();
                var characters = Math.Clamp(line.Before.End - line.Before.Start - part * SegmentLength, 0, SegmentLength) +
                    Math.Clamp(line.After.End - line.After.Start - part * SegmentLength, 0, SegmentLength) + line.GapSummary.Length + 2;
                if (pageRows > 0 && (pageRows >= TextComparison.PageSize || pageCharacters + characters > PageCharacters))
                {
                    _pageStarts.Add(rowCount + part);
                    pageRows = pageCharacters = 0;
                }
                pageRows++;
                pageCharacters += characters;
            }
            rowCount += line.RowCount;
        }
    }

    public TextComparisonResult GetPage(int pageIndex, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var pageCount = _pageStarts.Count;
        pageIndex = Math.Clamp(pageIndex, 0, pageCount - 1);
        var first = _pageStarts[pageIndex];
        var end = pageIndex + 1 < pageCount ? _pageStarts[pageIndex + 1] : _rowCount;
        var low = 0;
        var high = _lines.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (_lines[middle].FirstRow + _lines[middle].RowCount <= first) low = middle + 1;
            else high = middle;
        }
        var result = new List<TextDiffLine>(end - first);
        for (var index = low; index < _lines.Count && _lines[index].FirstRow < end; index++)
        {
            var line = _lines[index];
            for (var part = Math.Max(0, first - line.FirstRow); part < line.RowCount && line.FirstRow + part < end; part++)
            {
                token.ThrowIfCancellationRequested();
                if (line.Kind == "省略")
                {
                    result.Add(new("省略", null, null, null, null) { GapSummary = line.GapSummary, OmittedLineCount = line.OmittedLineCount });
                    continue;
                }
                var left = RenderSide(line.Before, part, line.EndingChanged);
                var right = RenderSide(line.After, part, line.EndingChanged);
                result.Add(new(line.Kind, left.Number, right.Number, left.Text, right.Text)
                {
                    Before = left.Parts, After = right.Parts, BeforeNote = left.Note, AfterNote = right.Note,
                    EndingChanged = line.EndingChanged
                });
            }
        }
        return new(true, _summary, result) { PageIndex = pageIndex, PageCount = pageCount, Preview = this };
    }

    private static LineSide Side(SourceTextLine? line, int index, int changeStart, int changeEnd, bool unchanged)
    {
        if (line is not { } source) return default;
        var start = 0;
        var end = source.Text.Length;
        if (unchanged) { end = Math.Min(end, SegmentLength); changeStart = changeEnd = 0; }
        else if (end > SegmentLength)
        {
            start = SurrogateBoundary(source.Text, Math.Max(0, changeStart - CharacterContext));
            end = Math.Min(end, changeEnd + CharacterContext);
            if (end < source.Text.Length && char.IsLowSurrogate(source.Text[end])) end++;
        }
        return new(source, index + 1, start, end, changeStart, changeEnd);
    }

    private static (int? Number, string? Text, DiffText Parts, string Note) RenderSide(LineSide side, int part, bool endingChanged)
    {
        if (side.Line is not { } source || part > 0 && side.Start + part * SegmentLength >= side.End)
            return (null, null, new("", "", ""), "");
        var start = SurrogateBoundary(source.Text, side.Start + part * SegmentLength);
        var end = SurrogateBoundary(source.Text, Math.Min(side.End, side.Start + (part + 1) * SegmentLength));
        var changeStart = Math.Clamp(side.ChangeStart, start, end);
        var changeEnd = Math.Clamp(side.ChangeEnd, changeStart, end);
        var parts = new DiffText(source.Text[start..changeStart], source.Text[changeStart..changeEnd], source.Text[changeEnd..end]);
        var note = start > 0 || end < source.Text.Length
            ? $"长行片段 · 字符位置 {start + 1:N0}–{end:N0} / {source.Text.Length:N0}（UTF-16）" : "";
        if (endingChanged) note += (note.Length > 0 ? " · " : "") + "换行：" + source.Ending;
        return (side.Number, source.Text[start..end], parts, note);
    }

    private static (int Prefix, int BeforeEnd, int AfterEnd) ChangeBounds(string before, string after, CancellationToken token)
    {
        var common = Math.Min(before.Length, after.Length);
        var prefix = 0;
        while (prefix < common && before[prefix] == after[prefix])
        {
            if ((prefix & 4095) == 0) token.ThrowIfCancellationRequested();
            prefix++;
        }
        var suffix = 0;
        while (suffix < common - prefix && before[^(suffix + 1)] == after[^(suffix + 1)])
        {
            if ((suffix & 4095) == 0) token.ThrowIfCancellationRequested();
            suffix++;
        }
        var left = ElementBounds(before, prefix, before.Length - suffix, token);
        var right = ElementBounds(after, prefix, after.Length - suffix, token);
        prefix = Math.Min(left.Start, right.Start);
        suffix = Math.Min(before.Length - left.End, after.Length - right.End);
        return (prefix, before.Length - suffix, after.Length - suffix);
    }

    private static (int Start, int End) ElementBounds(string text, int start, int end, CancellationToken token)
    {
        var previous = 0;
        var index = 0;
        var elements = 0;
        while (index < text.Length)
        {
            if ((elements++ & 1023) == 0) token.ThrowIfCancellationRequested();
            if (index <= start) previous = index;
            if (index >= end) return (previous, index);
            index += StringInfo.GetNextTextElementLength(text.AsSpan(index));
        }
        return (start == text.Length ? text.Length : previous, text.Length);
    }

    private static int SurrogateBoundary(string text, int index) =>
        index > 0 && index < text.Length && char.IsHighSurrogate(text[index - 1]) && char.IsLowSurrogate(text[index]) ? index - 1 : index;

    private readonly record struct LineSide(SourceTextLine? Line, int? Number, int Start, int End, int ChangeStart, int ChangeEnd);
    private sealed record DisplayLine(string Kind, LineSide Before, LineSide After, int FirstRow, int RowCount,
        bool EndingChanged, string GapSummary, int OmittedLineCount);
}
