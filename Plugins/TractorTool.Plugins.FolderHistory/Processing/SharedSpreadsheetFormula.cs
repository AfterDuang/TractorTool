using System.Globalization;
using System.Text;

namespace TractorTool.Plugins.FolderHistory.Processing;

internal sealed class SharedSpreadsheetFormula
{
    private static readonly string[] Errors = ["#REF!", "#DIV/0!", "#N/A", "#VALUE!", "#NAME?", "#NUM!", "#NULL!", "#GETTING_DATA"];
    private readonly string _expression;
    private readonly int _row, _column;
    private readonly Reference _first, _last;

    internal SharedSpreadsheetFormula(string expression, string range, int row, int column)
    {
        var bounds = range.Length <= 25 ? range.Split(':') : [];
        if (bounds.Length is < 1 or > 2 || !TryReference(bounds[0], out _first) ||
            !TryReference(bounds[^1], out _last) || _first.Row == 0 || _first.Column == 0 ||
            _last.Row < _first.Row || _last.Column < _first.Column)
            throw new InvalidDataException("共享公式的单元格范围无效。");
        _expression = expression;
        _row = row;
        _column = column;
        CheckMember(row, column);
    }

    internal string At(int row, int column, int maxCharacters, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CheckMember(row, column);
        if (_expression.Length > maxCharacters) throw TooLarge();
        var result = new StringBuilder(_expression.Length);
        for (var i = 0; i < _expression.Length;)
        {
            token.ThrowIfCancellationRequested();
            var start = i;
            var current = _expression[i];
            if (current is '"' or '\'')
            {
                i = QuotedEnd(_expression, i, token);
                if (current == '\'')
                {
                    // shortcut: external and 3-D references are refused until their complete grammar is supported.
                    if (i == _expression.Length || _expression[i] != '!' ||
                        _expression.AsSpan(start, i - start).ContainsAny('[', ']', ':')) throw Unsupported();
                    i++;
                }
                Append(_expression.AsSpan(start, i - start));
                continue;
            }
            if (IsWord(current))
            {
                i = WordEnd(_expression, i, token);
                var word = _expression.AsSpan(start, i - start);
                var next = SkipSpaces(_expression, i);
                if (next < _expression.Length && _expression[next] == '!')
                {
                    var previous = start - 1;
                    while (previous >= 0 && char.IsWhiteSpace(_expression[previous])) previous--;
                    if (previous >= 0 && _expression[previous] == ':') throw Unsupported();
                    i = next + 1;
                    Append(_expression.AsSpan(start, i - start));
                }
                else if (next < _expression.Length && _expression[next] == '(')
                {
                    // A1 (B1) is an intersection; LOG10(...) is a function, despite looking like a cell address.
                    if (TryReference(word, out var call) && call.Row != 0 && call.Column != 0 &&
                        (next != i || !word.Equals("LOG10", StringComparison.OrdinalIgnoreCase))) throw Unsupported();
                    Append(word);
                }
                else if (TryReference(word, out var reference))
                {
                    if (reference.Row != 0 && reference.Column != 0) AppendReference(word, reference);
                    else if (next < _expression.Length && _expression[next] == ':' &&
                        TryRangeEnd(next + 1, reference, out var lastStart, out var lastEnd, out var last))
                    {
                        AppendReference(word, reference);
                        Append(_expression.AsSpan(i, lastStart - i));
                        AppendReference(_expression.AsSpan(lastStart, lastEnd - lastStart), last);
                        i = lastEnd;
                    }
                    else
                    {
                        if (word.Contains('$')) throw Unsupported();
                        Append(word);
                    }
                }
                else
                {
                    if (word.Contains('$')) throw Unsupported();
                    var dot = word.IndexOf('.');
                    if (dot > 0 && TryReference(word[..dot], out var field) && field.Row != 0 && field.Column != 0)
                        throw Unsupported();
                    Append(word);
                }
                continue;
            }
            if (current == '#')
            {
                var error = Errors.FirstOrDefault(value => _expression.AsSpan(i).StartsWith(value, StringComparison.OrdinalIgnoreCase));
                if (error is null) throw Unsupported();
                Append(_expression.AsSpan(i, error.Length));
                i += error.Length;
                continue;
            }
            if (!char.IsWhiteSpace(current) && !"+-*/^&=<>%,;(){}:@".Contains(current)) throw Unsupported();
            Append(_expression.AsSpan(i++, 1));
        }
        return result.ToString();

        void Append(ReadOnlySpan<char> text)
        {
            if (text.Length > maxCharacters - result.Length) throw TooLarge();
            result.Append(text);
        }

        void AppendReference(ReadOnlySpan<char> word, Reference reference)
        {
            var targetRow = reference.Row == 0 || reference.RowAbsolute ? reference.Row : reference.Row + row - _row;
            var targetColumn = reference.Column == 0 || reference.ColumnAbsolute ? reference.Column : reference.Column + column - _column;
            if (reference.Row != 0 && targetRow is < 1 or > SpreadsheetWorkbook.LastRow ||
                reference.Column != 0 && targetColumn is < 1 or > SpreadsheetWorkbook.LastColumn)
                throw new InvalidDataException("共享公式的相对引用超出工作表范围，无法可靠还原。");
            if (reference.Column != 0)
            {
                if (targetColumn == reference.Column) Append(word[..reference.ColumnEnd]);
                else
                {
                    var name = SpreadsheetComparison.ColumnName(targetColumn);
                    Append(char.IsLower(word[0]) ? name.ToLowerInvariant() : name);
                }
            }
            if (reference.Row != 0)
            {
                if (targetRow == reference.Row) Append(word[reference.ColumnEnd..]);
                else Append(targetRow.ToString(CultureInfo.InvariantCulture));
            }
        }

        bool TryRangeEnd(int offset, Reference first, out int start, out int end, out Reference last)
        {
            start = SkipSpaces(_expression, offset);
            end = WordEnd(_expression, start, token);
            var next = SkipSpaces(_expression, end);
            return TryReference(_expression.AsSpan(start, end - start), out last) &&
                (first.Row == 0) == (last.Row == 0) && (first.Column == 0) == (last.Column == 0) &&
                (next == _expression.Length || _expression[next] != '!');
        }
    }

    private void CheckMember(int row, int column)
    {
        if (row < _first.Row || row > _last.Row || column < _first.Column || column > _last.Column)
            throw new InvalidDataException("共享公式的成员不在基准声明的范围内。");
    }

    private static bool TryReference(ReadOnlySpan<char> word, out Reference reference)
    {
        reference = default;
        if (word.IsEmpty) return false;
        var absolute = word[0] == '$';
        var i = absolute ? 1 : 0;
        var start = i;
        var column = 0;
        while (i < word.Length && char.IsAsciiLetter(word[i]))
        {
            if (i - start == 3) return false;
            column = column * 26 + char.ToUpperInvariant(word[i++]) - 'A' + 1;
        }
        if (column > SpreadsheetWorkbook.LastColumn) return false;
        var columnEnd = column == 0 ? 0 : i;
        var rowAbsolute = column == 0 && absolute;
        if (column != 0 && i < word.Length && word[i] == '$') { rowAbsolute = true; i++; }
        var row = 0;
        if (i < word.Length && (!int.TryParse(word[i..], NumberStyles.None, CultureInfo.InvariantCulture, out row) ||
            row is < 1 or > SpreadsheetWorkbook.LastRow)) return false;
        if (row == 0 && (column == 0 || rowAbsolute)) return false;
        reference = new(row, column, columnEnd, rowAbsolute, column != 0 && absolute);
        return true;
    }

    private static bool IsWord(char value) => char.IsLetterOrDigit(value) || value is '_' or '.' or '\\' or '$';

    private static int WordEnd(string text, int start, CancellationToken token)
    {
        while (start < text.Length && IsWord(text[start]))
        {
            if ((start & 1023) == 0) token.ThrowIfCancellationRequested();
            start++;
        }
        return start;
    }

    private static int SkipSpaces(string text, int start)
    {
        while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
        return start;
    }

    private static int QuotedEnd(string text, int start, CancellationToken token)
    {
        var quote = text[start++];
        while (start < text.Length)
        {
            if ((start & 1023) == 0) token.ThrowIfCancellationRequested();
            if (text[start++] != quote) continue;
            if (start == text.Length || text[start] != quote) return start;
            start++;
        }
        throw Unsupported();
    }

    private static InvalidDataException Unsupported() => new("共享公式含有暂不支持的引用语法，无法可靠还原每个单元格的公式。");
    private static InvalidDataException TooLarge() => new("共享公式展开后超过单元格文本与公式的预览上限。");
    private readonly record struct Reference(int Row, int Column, int ColumnEnd, bool RowAbsolute, bool ColumnAbsolute);
}
