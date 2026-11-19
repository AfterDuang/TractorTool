using System.Text;

namespace TractorTool.Plugins.FolderHistory.Processing;

public sealed record TextDiffLine(string Kind, int? BeforeLine, int? AfterLine, string? BeforeText, string? AfterText)
{
    public bool IsGap => Kind == "省略";
    public bool BeforeChanged => BeforeLine.HasValue && (Kind != "相同" || EndingChanged);
    public bool AfterChanged => AfterLine.HasValue && (Kind != "相同" || EndingChanged);
    public bool MissingAfter => AfterLine is null;
    public bool EndingChanged { get; init; }
    public string GapSummary { get; init; } = "";
    public int OmittedLineCount { get; init; }
    public string BeforeNote { get; init; } = "";
    public string AfterNote { get; init; } = "";
    public DiffText Before { get; init; } = new(BeforeText ?? "", "", "");
    public DiffText After { get; init; } = new(AfterText ?? "", "", "");
}

public sealed record DiffText(string Prefix, string Changed, string Suffix);
public sealed record TextComparisonResult(bool IsText, string Summary, IReadOnlyList<TextDiffLine> Lines)
{
    public int PageIndex { get; init; }
    public int PageCount { get; init; } = 1;
    internal TextComparisonPreview? Preview { get; init; }
    public TextComparisonResult GetPage(int pageIndex, CancellationToken cancellationToken = default) =>
        Preview?.GetPage(pageIndex, cancellationToken) ?? this;

    internal static TextComparisonResult FromChanges(string summary, IReadOnlyList<TextDiffLine> changes, CancellationToken token) =>
        new TextComparisonPreview(summary, changes, token).GetPage(0, token);
}

internal readonly record struct SourceTextLine(string Text, string Ending, string Note = "");
internal readonly record struct ComparedTextLine(int Before, int After);

public static class TextComparison
{
    public const int MaxBytes = 4 * 1024 * 1024;
    public const int PageSize = 240;
    private const int MaxLines = 100_000;
    private const int MaxCells = 1_000_000;

    public static TextComparisonResult Compare(byte[] before, byte[] after, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (before.Length > MaxBytes || after.Length > MaxBytes)
            return Unavailable("文件超过 4 MiB 文本预览上限，可另存原始文件查看。");
        if (!TryDecode(before, cancellationToken, out var oldText, out var oldEncoding) ||
            !TryDecode(after, cancellationToken, out var newText, out var newEncoding))
            return Unavailable("无法可靠识别为文本，或含有二进制控制字符。可另存原始文件查看。");
        var oldLines = SplitLines(oldText, cancellationToken);
        var newLines = SplitLines(newText, cancellationToken);
        if (oldLines is null || newLines is null)
            return Unavailable("文本超过 100,000 行预览上限，可另存原始文件查看。");

        var rows = Align(oldLines, newLines, line => line.Text, cancellationToken, out var approximate);
        return new TextComparisonPreview(oldLines, newLines, rows, oldEncoding, newEncoding, approximate, cancellationToken)
            .GetPage(0, cancellationToken);
    }

    internal static TextComparisonResult CompareParagraphs(string[] before, string[] after, CancellationToken token)
    {
        var rows = AlignLines(before, after, token, out var approximate);
        return new TextComparisonPreview(Array.ConvertAll(before, s => new SourceTextLine(s, "")),
            Array.ConvertAll(after, s => new SourceTextLine(s, "")), rows, "", "", approximate, token, "段")
            .GetPage(0, token);
    }

    internal static IReadOnlyList<ComparedTextLine> AlignLines(string[] before, string[] after, CancellationToken token,
        out bool approximate) => Align(before, after, text => text, token, out approximate);

    private static IReadOnlyList<ComparedTextLine> Align<T>(T[] oldLines, T[] newLines, Func<T, string> textOf,
        CancellationToken cancellationToken, out bool approximate)
    {
        var tokens = new Dictionary<string, int>(StringComparer.Ordinal);
        var oldTokens = Tokenize(oldLines);
        var newTokens = Tokenize(newLines);
        var rows = new List<ComparedTextLine>();
        var remainingCells = 4 * MaxCells;
        var remainingAnchorLines = 8 * MaxLines;
        var pairedLargeRange = false;
        CompareRange(0, oldLines.Length, 0, newLines.Length, 0);
        approximate = pairedLargeRange;
        return rows;

        void AppendChanges(int oldStart, int oldEnd, int newStart, int newEnd)
        {
            for (var offset = 0; offset < Math.Max(oldEnd - oldStart, newEnd - newStart); offset++)
            {
                if ((offset & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                rows.Add(new(oldStart + offset < oldEnd ? oldStart + offset : -1,
                    newStart + offset < newEnd ? newStart + offset : -1));
            }
        }

        void CompareRange(int oldStart, int oldEnd, int newStart, int newEnd, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (oldStart < oldEnd && newStart < newEnd && oldTokens[oldStart] == newTokens[newStart])
            {
                if ((oldStart & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                rows.Add(new(oldStart++, newStart++));
            }
            var suffix = 0;
            while (oldStart < oldEnd && newStart < newEnd && oldTokens[oldEnd - 1] == newTokens[newEnd - 1])
            {
                if ((suffix & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                oldEnd--; newEnd--; suffix++;
            }

            var cells = (long)(oldEnd - oldStart + 1) * (newEnd - newStart + 1);
            if (oldStart == oldEnd || newStart == newEnd) AppendChanges(oldStart, oldEnd, newStart, newEnd);
            else if (cells <= MaxCells && cells <= remainingCells)
            {
                remainingCells -= (int)cells;
                CompareSmallRange(oldStart, oldEnd, newStart, newEnd);
            }
            else
            {
                var anchors = depth < 16 && oldEnd - oldStart + newEnd - newStart <= remainingAnchorLines
                    ? FindAnchors(oldStart, oldEnd, newStart, newEnd) : [];
                if (anchors.Count == 0)
                {
                    if (!TryCompareByEdits(oldStart, oldEnd, newStart, newEnd))
                    {
                        // shortcut: ranges beyond both matching budgets use ordered pairing; widen budgets only with measured need.
                        pairedLargeRange = true;
                        AppendChanges(oldStart, oldEnd, newStart, newEnd);
                    }
                }
                else
                {
                    foreach (var anchor in anchors)
                    {
                        CompareRange(oldStart, anchor.Before, newStart, anchor.After, depth + 1);
                        rows.Add(anchor);
                        oldStart = anchor.Before + 1;
                        newStart = anchor.After + 1;
                    }
                    CompareRange(oldStart, oldEnd, newStart, newEnd, depth + 1);
                }
            }
            for (var offset = 0; offset < suffix; offset++)
            {
                if ((offset & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                rows.Add(new(oldEnd + offset, newEnd + offset));
            }
        }

        void CompareSmallRange(int oldStart, int oldEnd, int newStart, int newEnd)
        {
            var lengths = new int[oldEnd - oldStart + 1, newEnd - newStart + 1];
            for (var i = oldEnd - oldStart - 1; i >= 0; i--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var j = newEnd - newStart - 1; j >= 0; j--)
                    lengths[i, j] = oldTokens[oldStart + i] == newTokens[newStart + j] ? lengths[i + 1, j + 1] + 1
                        : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
            var previous = oldStart;
            var current = newStart;
            var pendingOld = previous;
            var pendingNew = current;
            while (previous < oldEnd || current < newEnd)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (previous < oldEnd && current < newEnd && oldTokens[previous] == newTokens[current])
                {
                    AppendChanges(pendingOld, previous, pendingNew, current);
                    rows.Add(new(previous++, current++));
                    pendingOld = previous;
                    pendingNew = current;
                }
                else if (previous < oldEnd && (current == newEnd ||
                    lengths[previous - oldStart + 1, current - newStart] >= lengths[previous - oldStart, current - newStart + 1]))
                    previous++;
                else current++;
            }
            AppendChanges(pendingOld, previous, pendingNew, current);
        }

        bool TryCompareByEdits(int oldStart, int oldEnd, int newStart, int newEnd)
        {
            const int maxEdits = 512;
            var oldLength = oldEnd - oldStart;
            var newLength = newEnd - newStart;
            if (Math.Abs(oldLength - newLength) > maxEdits) return false;
            var trace = new List<int[]>();
            for (var edits = 0; edits <= Math.Min(maxEdits, oldLength + newLength); edits++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frontier = new int[edits + 1];
                var previous = edits == 0 ? [] : trace[^1];
                for (var step = 0; step <= edits; step++)
                {
                    if (remainingCells == 0) return false;
                    remainingCells--;
                    var x = edits == 0 ? 0 : step == 0 ? previous[0] : step == edits ? previous[step - 1] + 1 :
                        previous[step - 1] < previous[step] ? previous[step] : previous[step - 1] + 1;
                    var y = x - (2 * step - edits);
                    while (x < oldLength && y < newLength)
                    {
                        if (remainingCells == 0) return false;
                        remainingCells--;
                        if (oldTokens[oldStart + x] != newTokens[newStart + y]) break;
                        if ((x & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                        x++; y++;
                    }
                    frontier[step] = x;
                    if (x < oldLength || y < newLength) continue;
                    trace.Add(frontier);
                    var matches = new List<ComparedTextLine>();
                    x = oldLength; y = newLength;
                    for (var depth = edits; depth > 0; depth--)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var diagonal = x - y;
                        var position = (diagonal + depth) / 2;
                        var prior = trace[depth - 1];
                        var inserted = position == 0 || position != depth && prior[position - 1] < prior[position];
                        var priorX = prior[inserted ? position : position - 1];
                        var priorY = priorX - (diagonal + (inserted ? 1 : -1));
                        while (x > priorX && y > priorY)
                        {
                            if ((x & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                            matches.Add(new(oldStart + --x, newStart + --y));
                        }
                        x = priorX; y = priorY;
                    }
                    while (x > 0 && y > 0) matches.Add(new(oldStart + --x, newStart + --y));
                    matches.Reverse();
                    var pendingOld = oldStart;
                    var pendingNew = newStart;
                    foreach (var match in matches)
                    {
                        AppendChanges(pendingOld, match.Before, pendingNew, match.After);
                        rows.Add(match);
                        pendingOld = match.Before + 1;
                        pendingNew = match.After + 1;
                    }
                    AppendChanges(pendingOld, oldEnd, pendingNew, newEnd);
                    return true;
                }
                trace.Add(frontier);
            }
            return false;
        }

        List<ComparedTextLine> FindAnchors(int oldStart, int oldEnd, int newStart, int newEnd)
        {
            remainingAnchorLines -= oldEnd - oldStart + newEnd - newStart;
            var oldPositions = UniquePositions(oldTokens, oldStart, oldEnd);
            var newPositions = UniquePositions(newTokens, newStart, newEnd);
            var candidates = new List<ComparedTextLine>();
            for (var index = oldStart; index < oldEnd; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (oldPositions[oldTokens[index]] == index &&
                    newPositions.TryGetValue(oldTokens[index], out var next) && next >= 0)
                    candidates.Add(new(index, next));
            }
            var tails = new List<int>();
            var predecessors = new int[candidates.Count];
            for (var index = 0; index < candidates.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var low = 0;
                var high = tails.Count;
                while (low < high)
                {
                    var middle = (low + high) / 2;
                    if (candidates[tails[middle]].After < candidates[index].After) low = middle + 1;
                    else high = middle;
                }
                predecessors[index] = low == 0 ? -1 : tails[low - 1];
                if (low == tails.Count) tails.Add(index);
                else tails[low] = index;
            }
            var anchors = new List<ComparedTextLine>(tails.Count);
            for (var index = tails.Count == 0 ? -1 : tails[^1]; index >= 0; index = predecessors[index])
                anchors.Add(candidates[index]);
            anchors.Reverse();
            return anchors;
        }

        Dictionary<int, int> UniquePositions(int[] lines, int start, int end)
        {
            var positions = new Dictionary<int, int>();
            for (var index = start; index < end; index++)
            {
                if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (!positions.TryAdd(lines[index], index)) positions[lines[index]] = -1;
            }
            return positions;
        }

        int[] Tokenize(T[] lines)
        {
            var result = new int[lines.Length];
            for (var index = 0; index < lines.Length; index++)
            {
                if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                var text = textOf(lines[index]);
                if (!tokens.TryGetValue(text, out var value))
                {
                    value = tokens.Count;
                    tokens.Add(text, value);
                }
                result[index] = value;
            }
            return result;
        }
    }

    private static bool TryDecode(byte[] bytes, CancellationToken token, out string text, out string name)
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
        for (var index = 0; index < text.Length; index++)
        {
            if ((index & 4095) == 0) token.ThrowIfCancellationRequested();
            if (char.IsControl(text[index]) && text[index] is not '\t' and not '\r' and not '\n') return false;
        }
        return true;
    }

    private static SourceTextLine[]? SplitLines(string text, CancellationToken token)
    {
        var lines = new List<SourceTextLine>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if ((index & 4095) == 0) token.ThrowIfCancellationRequested();
            if (text[index] is not '\r' and not '\n') continue;
            var content = text[start..index];
            var ending = text[index] == '\r' ? "CR" : "LF";
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n') { index++; ending = "CRLF"; }
            lines.Add(new(content, ending));
            start = index + 1;
            if (lines.Count > MaxLines) return null;
        }
        if (start < text.Length) lines.Add(new(text[start..], "无换行"));
        return lines.Count > MaxLines ? null : [.. lines];
    }

    private static TextComparisonResult Unavailable(string message) => new(false, message, []);
}
