using System.Text;
using TractorTool.Plugins.FolderHistory.Processing;

internal static class TextComparisonChecks
{
    public static void Run()
    {
        var changed = TextComparison.Compare(Bytes("甲\r\n乙\r\n丙"), Bytes("甲\r\n新增\r\n丙"));
        Assert(changed.IsText && changed.Lines.Count(line => line.Kind == "删除") == 1 &&
            changed.Lines.Count(line => line.Kind == "新增") == 1, "line replacement was not isolated");
        Assert(changed.Lines.Where(line => line.Kind != "新增").Select(line => line.BeforeLine).SequenceEqual(new int?[] { 1, 2, 3 }),
            "original line numbers changed");
        Assert(changed.Lines.Where(line => line.Kind != "删除").Select(line => line.AfterLine).SequenceEqual(new int?[] { 1, 2, 3 }),
            "new line numbers changed");
        Assert(TextComparison.Compare(Bytes("a\r\n"), Bytes("a\n")).Lines.All(line => line.Kind != "相同"), "line endings ignored");
        Assert(TextComparison.Compare(Bytes("a"), Bytes("a\n")).Lines.Count == 2, "final newline ignored");
        Assert(TextComparison.Compare([], []).Lines.Count == 0, "empty content gained a line");
        var repeat = TextComparison.Compare(Bytes("a\nb\na\n"), Bytes("a\na\n"));
        Assert(repeat.Lines.Single(line => line.Kind == "删除").Text == "b", "repeated lines lost their alignment");
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
