using System.Globalization;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace TractorTool.Plugins.FolderHistory.Processing;

internal static partial class SpreadsheetWorkbook
{
    private const int MaxCells = 100_000;
    private const int MaxCharacters = 4 * 1024 * 1024;
    private const int LastColumn = 16_384;
    private const int LastRow = 1_048_576;

    internal static Dictionary<string, List<SheetRow>> Read(byte[]? bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result = new Dictionary<string, List<SheetRow>>(StringComparer.Ordinal);
        if (bytes is null) return result;
        using var stream = new MemoryStream(bytes, writable: false);
        using var document = SpreadsheetDocument.Open(stream, false, OfficeComparison.CreateOpenSettings());
        var workbook = document.WorkbookPart ?? throw new InvalidDataException("找不到 Excel 工作簿。");
        var sheets = workbook.Workbook?.Sheets ?? throw new InvalidDataException("找不到 Excel 工作表清单。");
        if (!sheets.Elements<Sheet>().Any()) throw new InvalidDataException("Excel 工作表清单为空。");
        var strings = ReadSharedStrings(workbook.SharedStringTablePart, token);
        var cellCount = 0;
        var characters = 0;
        var seenParts = new HashSet<Uri>();
        foreach (var sheet in sheets.Elements<Sheet>())
        {
            token.ThrowIfCancellationRequested();
            var name = sheet.Name?.Value;
            var id = sheet.Id?.Value;
            if (string.IsNullOrEmpty(name) || name.Length > 31 || string.IsNullOrEmpty(id))
                throw new InvalidDataException("工作表的名称或关联无效。");
            if (!workbook.TryGetPartById(id, out var part)) throw new InvalidDataException("找不到工作表关联的内容。");
            if (part is ChartsheetPart or DialogsheetPart) continue;
            if (part is not WorksheetPart worksheet) throw new InvalidDataException("工作表关联的内容类型不受支持。");
            var rows = new List<SheetRow>();
            if (!seenParts.Add(part.Uri) || !result.TryAdd(name, rows)) throw new InvalidDataException("工作表名称或数据关联重复。");
            ReadSheet(worksheet, rows, strings, ref cellCount, ref characters, token);
        }
        return result;
    }

    private static List<string> ReadSharedStrings(SharedStringTablePart? part, CancellationToken token)
    {
        var strings = new List<string>();
        if (part is null) return strings;
        var characters = 0;
        using var reader = OpenXmlReader.Create(part);
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (!reader.IsStartElement || reader.ElementType != typeof(SharedStringItem)) continue;
            var item = (SharedStringItem)reader.LoadCurrentElement()!;
            var text = VisibleText(item);
            if (strings.Count >= MaxCells || text.Length > MaxCharacters - characters)
                throw new InvalidDataException("工作簿的共享文本超过预览上限。");
            characters += text.Length;
            strings.Add(text);
        }
        return strings;
    }

    private static string VisibleText(OpenXmlCompositeElement item) =>
        string.Concat(item.ChildElements.Select(child => child switch
        {
            Text text => DecodeString(text.Text),
            Run run when run.GetFirstChild<Text>() is { } text => DecodeString(text.Text),
            _ => null // Formatting and phonetic annotations are not part of the cell value.
        }));

    // Decode each ST_Xstring once. Escaping the initial underscore produces a literal such as "_x000D_".
    [GeneratedRegex("_x([0-9A-Fa-f]{4})_")]
    private static partial Regex EscapedCharacter();

    private static string DecodeString(string text) => text.Contains("_x", StringComparison.Ordinal)
        ? EscapedCharacter().Replace(text, static match =>
            ((char)int.Parse(match.Groups[1].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString())
        : text;

    private static void ReadSheet(WorksheetPart part, List<SheetRow> rows, List<string> strings, ref int cellCount,
        ref int characters, CancellationToken token)
    {
        var sharedFormulas = new Dictionary<uint, string>();
        var cells = new List<SheetCell>();
        var row = 0;
        var column = 0;
        var foundSheetData = false;
        using var reader = OpenXmlReader.Create(part);
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.Depth == 0 && reader.IsStartElement && reader.ElementType != typeof(Worksheet))
                throw new InvalidDataException("工作表内容无效。");
            if (reader.IsStartElement && reader.ElementType == typeof(SheetData))
            {
                if (foundSheetData || reader.Depth != 1) throw new InvalidDataException("工作表的单元格区域无效。");
                foundSheetData = true;
            }
            else if (reader.ElementType == typeof(Row))
            {
                if (reader.IsStartElement)
                {
                    var index = reader.Attributes.FirstOrDefault(attribute => attribute.LocalName == "r" && attribute.NamespaceUri.Length == 0).Value;
                    var next = index is null ? row + 1 : PositiveIndex(index, LastRow);
                    if (next <= row || next > LastRow) throw new InvalidDataException("工作表行号无效或顺序异常。");
                    row = next;
                    column = 0;
                    cells.Clear();
                }
                else if (reader.IsEndElement && cells.Count != 0) rows.Add(new(row, cells.ToArray()));
            }
            else if (reader.IsStartElement && reader.ElementType == typeof(Cell))
            {
                if (++cellCount > MaxCells) throw new InvalidDataException("工作簿超过 100,000 个单元格的预览上限。");
                var cell = (Cell)reader.LoadCurrentElement()!;
                var reference = cell.CellReference?.Value;
                var next = reference is null ? column + 1 : ReadColumn(reference, row);
                if (row < 1 || next <= column || next > LastColumn) throw new InvalidDataException("工作表单元格位置无效或顺序异常。");
                column = next;
                var value = ReadCell(cell, row, column, strings, sharedFormulas);
                if (value is null) continue;
                var length = (value.Value?.Length ?? 0) + (value.Formula?.Length ?? 0);
                if (length > MaxCharacters - characters) throw new InvalidDataException("工作簿的单元格文本与公式超过 4 Mi 字符的预览上限。");
                characters += length;
                cells.Add(value);
            }
        }
        if (!foundSheetData) throw new InvalidDataException("工作表缺少单元格区域。");
    }

    private static int PositiveIndex(ReadOnlySpan<char> text, int limit) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0 && value <= limit
            ? value : throw new InvalidDataException("工作表含有无效坐标。");

    private static int ReadColumn(string address, int row)
    {
        var i = 0;
        var column = 0;
        while (i < address.Length && address[i] is >= 'A' and <= 'Z' && i < 3)
            column = column * 26 + address[i++] - 'A' + 1;
        if (column is 0 or > LastColumn || PositiveIndex(address.AsSpan(i), LastRow) != row)
            throw new InvalidDataException("工作表单元格地址与行号不一致。");
        return column;
    }

    private static SheetCell? ReadCell(Cell cell, int row, int column, List<string> strings, Dictionary<uint, string> sharedFormulas)
    {
        var formula = cell.CellFormula;
        var type = cell.DataType?.InnerText ?? "n";
        var raw = cell.CellValue?.Text;
        if (raw is null && formula is null && cell.InlineString is null) return null;
        string? value = raw;
        string kind;
        switch (type)
        {
            case "s":
                if (raw is not null)
                {
                    if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || (uint)index >= strings.Count)
                        throw new InvalidDataException("工作表单元格的共享文本索引无效。");
                    value = strings[index];
                }
                kind = "文本";
                break;
            case "inlineStr":
                value = VisibleText(cell.InlineString ?? throw new InvalidDataException("单元格缺少文本。"));
                kind = "文本";
                break;
            case "str":
                if (raw is not null) value = DecodeString(raw);
                kind = "文本";
                break;
            case "n":
                if (!string.IsNullOrEmpty(raw))
                {
                    // Excel stores numeric cells as double-precision numbers; text cells retain every literal digit.
                    if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                        throw new InvalidDataException("工作表含有无效的数值。");
                    value = number == 0 ? "0" : number.ToString("R", CultureInfo.InvariantCulture);
                }
                kind = "数值";
                break;
            case "b":
                value = raw switch { null => null, "1" or "true" => "TRUE", "0" or "false" => "FALSE", _ => throw new InvalidDataException("单元格的布尔值无效。") };
                kind = "逻辑值";
                break;
            case "e": kind = "错误值"; break;
            case "d": kind = "日期"; break;
            default: throw new InvalidDataException("工作表含有无法识别的单元格类型。");
        }
        if (formula is null) return new(column, kind, value, null);
        var expression = "=" + formula.Text;
        switch (formula.FormulaType?.InnerText)
        {
            case null or "normal": break;
            case "shared":
                var id = formula.SharedIndex?.Value ?? throw new InvalidDataException("共享公式缺少索引。");
                if (!string.IsNullOrEmpty(formula.Reference?.Value))
                {
                    if (formula.Text.Length == 0) throw new InvalidDataException("共享公式基准缺少表达式。");
                    if (!sharedFormulas.TryAdd(id, $"共享公式（基准 {SpreadsheetComparison.ColumnName(column)}{row}）{expression}"))
                        throw new InvalidDataException("共享公式的基准单元格重复。");
                }
                // The master overrides even a follower that carries its own formula text (SpreadsheetML §18.3.1.40).
                else if (!sharedFormulas.TryGetValue(id, out expression))
                    throw new InvalidDataException("无法找到共享公式的基准单元格。");
                break;
            case "array":
                expression = $"数组公式（{formula.Reference?.Value}）{expression}";
                break;
            default: throw new InvalidDataException("该工作簿含有暂不支持的特殊公式，无法完整对比。");
        }
        return new(column, $"公式 · {kind}", value, expression);
    }
}
