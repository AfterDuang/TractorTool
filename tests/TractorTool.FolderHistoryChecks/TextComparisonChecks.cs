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
        Assert(!TextComparison.Compare(Bytes(new string('x', 4097)), []).IsText, "line length limit ignored");
        Assert(!TextComparison.Compare(Bytes(string.Concat(Enumerable.Repeat("x\n", 1201))), []).IsText, "line count limit ignored");
        Assert(!TextComparison.Compare(Bytes(string.Concat(Enumerable.Repeat("x\n", 1000))),
            Bytes(string.Concat(Enumerable.Repeat("y\n", 1000)))).IsText, "comparison work limit ignored");
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
