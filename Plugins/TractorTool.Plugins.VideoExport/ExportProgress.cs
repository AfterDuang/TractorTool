using System.Globalization;
using System.Text;

namespace TractorTool.Plugins.VideoExport;

// FFmpeg stdout is a stream of chunks, not lines; retain only the current bounded line.
public sealed class ExportProgress(double duration, Action<double> report)
{
    private readonly StringBuilder _line = new();
    private double _seconds;
    private double _last;
    private bool _discard;

    public void Receive(string chunk)
    {
        foreach (var c in chunk)
        {
            if (c == '\n')
            {
                if (!_discard) ParseLine(_line.ToString().TrimEnd('\r'));
                _line.Clear(); _discard = false;
            }
            else if (!_discard)
            {
                if (_line.Length < 4096) _line.Append(c);
                else { _line.Clear(); _discard = true; }
            }
        }
    }

    private void ParseLine(string line)
    {
        if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
            long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros))
            _seconds = Math.Max(0, micros / 1_000_000d);
        if (line.StartsWith("progress=", StringComparison.Ordinal) && duration > 0)
        {
            // 100% belongs to the caller after the output has been validated and committed.
            var value = Math.Clamp(_seconds / duration * 100, _last, 99);
            if (value > _last) { _last = value; report(value); }
        }
    }
}
