using System.Text;
using TractorTool.Plugins.FolderHistory.Processing;

internal static class TextComparisonChecks
{
    public static void Run()
    {
        var changed = TextComparison.Compare(Bytes("甲\r\n乙\r\n丙"), Bytes("甲\r\n新增\r\n丙"));
        Assert(changed.IsText && changed.Lines.Single(line => line.Kind == "修改") is
            { BeforeLine: 2, AfterLine: 2, BeforeText: "乙", AfterText: "新增" }, "replacement not paired on a single row");
        Assert(changed.Lines.Select(line => line.BeforeLine).SequenceEqual(new int?[] { 1, 2, 3 }),
            "original line numbers changed");
        Assert(changed.Lines.Select(line => line.AfterLine).SequenceEqual(new int?[] { 1, 2, 3 }),
            "new line numbers changed");
        var fragment = TextComparison.Compare(Bytes("前👨‍💻后"), Bytes("前👨‍🔬后")).Lines.Single();
        Assert(fragment.Before == new DiffText("前", "👨‍💻", "后") && fragment.After == new DiffText("前", "👨‍🔬", "后"),
            "intraline highlight split a grapheme or colored unchanged text");
        var insertion = TextComparison.Compare(Bytes("ab"), Bytes("a中b")).Lines.Single();
        Assert(insertion.Before == new DiffText("a", "", "b") && insertion.After == new DiffText("a", "中", "b"),
            "an insertion inside a line must preserve both edges on both sides");
        var endings = TextComparison.Compare(Bytes("a\r\nb"), Bytes("a\nb\n"));
        Assert(endings.Lines.All(line => line.Kind == "相同") && endings.Summary.Contains("换行"), "line endings must be reported separately");
        var uneven = TextComparison.Compare(Bytes("a\nb\nc"), Bytes("x\nc"));
        Assert(TextComparison.Compare(Bytes("old\r\n"), Bytes("new\n")).Summary.Contains("换行"),
            "line endings changed on a replacement but were omitted");
        Assert(uneven.Lines is [{ Kind: "修改" }, { Kind: "删除", BeforeText: "b", AfterText: null, AfterLine: null },
            { Kind: "相同", BeforeLine: 3, AfterLine: 2 }], "unequal hunk shifted subsequent lines");
        Assert(TextComparison.Compare([], []).Lines.Count == 0, "empty content gained a line");
        var repeat = TextComparison.Compare(Bytes("a\nb\na\n"), Bytes("a\na\n"));
        Assert(repeat.Lines.Single(line => line.Kind == "删除").BeforeText == "b", "repeated lines lost their alignment");
        var unicode = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("中文\r\n")).ToArray();
        Assert(TextComparison.Compare(unicode, Bytes("中文\r\n")).Lines.All(line => line.Kind == "相同"), "BOM UTF-16 decoding failed");
        Assert(!TextComparison.Compare([0xff, 0x00], []).IsText, "invalid UTF-8 accepted");
        Assert(!TextComparison.Compare([0x41, 0x00, 0x42], []).IsText, "binary NUL accepted");
        Assert(!TextComparison.Compare([0xff, 0xfe, 0x00], []).IsText, "truncated UTF-16 accepted");
        Assert(!TextComparison.Compare(new byte[TextComparison.MaxBytes + 1], []).IsText, "byte limit ignored");
        Assert(!TextComparison.Compare(Bytes(new string('\n', 100_001)), []).IsText, "line count bound ignored");
        CheckLargeAddition();
        CheckScatteredChanges();
        CheckRepeatedLines();
        CheckLongLines();
        var dense = TextComparison.Compare(Bytes(string.Concat(Enumerable.Repeat("x\n", 2000))),
            Bytes(string.Concat(Enumerable.Repeat("y\n", 2000))));
        Assert(dense.IsText && dense.Summary.Contains("修改 2000 行") && dense.Summary.Contains("按行配对") &&
            Pages(dense).Count == 2000, "bounded dense comparison hid changes or claimed exact alignment");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        AssertCancelled(() => TextComparison.Compare(Bytes("a"), Bytes("b"), cancelled.Token));
        AssertCancelled(() => dense.GetPage(1, cancelled.Token));
    }

    private static void CheckLargeAddition()
    {
        var markdown = string.Join('\n', Enumerable.Range(1, 5000).Select(index => $"## {index:D4} " + new string('x', 89)))
            .PadRight(491_213, 'x'); // 479.7 KiB, including a longer final line.
        var bytes = Bytes(markdown);
        var original = bytes.ToArray();
        var added = TextComparison.Compare([], bytes);
        var rows = Pages(added);
        Assert(added.IsText && added.PageCount > 1 && added.Lines.Count <= TextComparison.PageSize &&
            added.Summary.Contains("新增 5000 行"), "large Markdown addition was rejected or fully expanded");
        Assert(rows.Count == 5000 && rows.All(line => line.Kind == "新增" && line.BeforeLine is null) &&
            rows.Select(line => line.AfterLine).SequenceEqual(Enumerable.Range(1, 5000).Select(index => (int?)index)) &&
            string.Join('\n', rows.Select(line => line.AfterText)) == markdown,
            "paging omitted, duplicated or reordered an added line");
        Assert(bytes.SequenceEqual(original), "preview modified original file bytes");
        var removed = TextComparison.Compare(bytes, []);
        Assert(Pages(removed).All(line => line.Kind == "删除" && line.AfterLine is null) && removed.Summary.Contains("删除 5000 行"),
            "large deletion lost its missing side or full count");
    }

    private static void CheckScatteredChanges()
    {
        var before = Enumerable.Range(1, 6000).Select(index => $"entry {index:D5}: " + new string('a', 80)).ToArray();
        var after = before.ToList();
        after[27] = "changed near the beginning";
        after[3100] = "changed in the middle";
        after[5998] = "changed near the end";
        after.Insert(1500, "an inserted line");
        after.RemoveAt(4501);
        var result = TextComparison.Compare(Bytes(string.Join('\n', before)), Bytes(string.Join('\n', after)));
        var rows = Pages(result);
        Assert(result.IsText && rows.Count < 60 && result.Summary.StartsWith("新增 1 行 · 修改 3 行 · 删除 1 行"),
            "large scattered changes did not retain a compact contextual diff");
        Assert(rows.Where(line => line.Kind == "修改").Select(line => line.BeforeLine).SequenceEqual(new int?[] { 28, 3101, 5999 }) &&
            rows.Single(line => line.Kind == "新增").AfterLine == 1501 &&
            rows.Single(line => line.Kind == "删除").BeforeLine == 4501,
            "anchors shifted original or current changed line numbers");
        Assert(rows.First() is { IsGap: true, OmittedLineCount: 24 } first && first.GapSummary.Contains("原 1–24 / 新 1–24") &&
            rows.Count(line => line.BeforeLine.HasValue) + rows.Sum(line => line.OmittedLineCount) == before.Length &&
            rows.Count(line => line.AfterLine.HasValue) + rows.Sum(line => line.OmittedLineCount) == after.Count,
            "omission labels hid changed lines or reported inaccurate ranges/counts");
        var endings = before.ToArray();
        var withEndingChange = TextComparison.Compare(Bytes(string.Join('\n', before)),
            Bytes(string.Join('\n', endings.Take(5999)) + "\r\n" + endings[^1]));
        Assert(Pages(withEndingChange).Single(line => line.EndingChanged) is
            { BeforeLine: 5999, AfterLine: 5999, BeforeNote: "换行：LF", AfterNote: "换行：CRLF" },
            "line-ending-only change was hidden as unchanged context");
    }

    private static void CheckLongLines()
    {
        var prefix = new string('a', 12000);
        var tail = TextComparison.Compare(Bytes(prefix + "旧尾巴"), Bytes(prefix + "新尾巴")).Lines.Single();
        Assert(tail.Before.Changed == "旧" && tail.After.Changed == "新" && tail.BeforeText!.EndsWith("旧尾巴") &&
            tail.AfterText!.EndsWith("新尾巴") && tail.BeforeText.Length < 200 && tail.BeforeNote.Contains("字符位置"),
            "long-line preview hid a tail edit or omitted its clipping notice");
        var longAdded = new string('x', 4095) + "😀" + new string('y', 1_100_000);
        var result = TextComparison.Compare([], Bytes(longAdded));
        var rows = Pages(result);
        Assert(result.PageCount > 1 && rows.All(line => line.AfterLine == 1 && line.AfterNote.Length > 0) &&
            string.Concat(rows.Select(line => line.AfterText)) == longAdded &&
            rows.All(line => !char.IsLowSurrogate(line.AfterText![0]) && !char.IsHighSurrogate(line.AfterText[^1])),
            "long added line could not be fully paged or a surrogate pair was split");
        var first = new string('a', 4093);
        var second = "👨‍💻" + new string('b', 4090);
        const string last = "e\u0301尾";
        var graphemes = first + second + last;
        Assert(Pages(TextComparison.Compare([], Bytes(graphemes))).Select(row => row.AfterText).SequenceEqual([first, second, last]) &&
            TextComparison.Compare(Bytes(graphemes), Bytes(graphemes)).Lines.Single().AfterText == first,
            "segmenting or clipping a long line split a joined emoji or a combining sequence");
        var leftContext = "👨‍💻" + new string('a', 78);
        var rightContext = new string('b', 79) + "e\u0301";
        var clipped = TextComparison.Compare(Bytes(prefix + leftContext + "旧" + rightContext + "尾"),
            Bytes(prefix + leftContext + "新" + rightContext + "尾")).Lines.Single();
        Assert(clipped.After == new DiffText(leftContext, "新", rightContext), "clipped context split a grapheme");
        var oversizedElement = "中" + new string('\u0301', 80_000);
        var chunks = Pages(TextComparison.Compare([], Bytes(oversizedElement))).Select(row => row.AfterText!).ToArray();
        Assert(chunks.All(chunk => chunk.Length is > 0 and <= 4096) && string.Concat(chunks) == oversizedElement,
            "a single oversized grapheme bypassed the segment budget or lost content");
        var bytes = Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(prefix + "\r\n")).ToArray();
        Assert(TextComparison.Compare(bytes, Bytes(prefix + "\r\n")).Summary.Contains("UTF-16 BE → UTF-8"),
            "long text lost its original encoding report");
    }

    private static void CheckRepeatedLines()
    {
        var before = Enumerable.Range(0, 10_000).Select(index => index % 2 == 0 ? "A" : "B").ToArray();
        var after = new[] { "X" }.Concat(before).ToArray();
        after[^1] = "changed tail";
        var result = TextComparison.Compare(Bytes(string.Join('\n', before)), Bytes(string.Join('\n', after)));
        var rows = Pages(result);
        Assert(result.IsText && !result.Summary.Contains("按行配对") &&
            result.Summary.StartsWith("新增 1 行 · 修改 1 行 · 删除 0 行") && rows.Count < 20 &&
            rows.Single(line => line.Kind == "新增").AfterLine == 1 &&
            rows.Single(line => line.Kind == "修改") is { BeforeLine: 10000, AfterLine: 10001 },
            "a small shift in repeated lines was reported as a whole-file replacement");
        Assert(rows.Count(line => line.BeforeLine.HasValue) + rows.Sum(line => line.OmittedLineCount) == before.Length &&
            rows.Count(line => line.AfterLine.HasValue) + rows.Sum(line => line.OmittedLineCount) == after.Length,
            "bounded repeated-line alignment omitted or duplicated original lines");
    }

    private static IReadOnlyList<TextDiffLine> Pages(TextComparisonResult result)
    {
        var rows = new List<TextDiffLine>();
        for (var index = 0; index < result.PageCount; index++)
        {
            var page = result.GetPage(index);
            Assert(page.PageIndex == index && page.Lines.Count <= TextComparison.PageSize && page.Summary == result.Summary,
                "page size or full-file summary changed between pages");
            Assert(page.Lines.Sum(line => (line.BeforeText?.Length ?? 0) + (line.AfterText?.Length ?? 0)) <= 64 * 1024,
                "long lines exceeded the per-page character budget");
            rows.AddRange(page.Lines);
        }
        return rows;
    }

    private static void AssertCancelled(Action action)
    {
        try { action(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("text comparison ignored cancellation");
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
