using System.Globalization;
using System.Text.RegularExpressions;

namespace TractorTool.Plugins.MediaCompare.Media;

internal sealed record MediaSourceInfo(string Uri, string Name, bool IsStill, double SequenceRate = 0,
    TimeSpan? SequenceDuration = null)
{
    private static readonly HashSet<string> Stills = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tif", ".tiff" };

    internal static MediaSourceInfo Create(MediaSource source, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(source.Path) || !File.Exists(source.Path))
            throw new FileNotFoundException("请选择存在的本地媒体文件。", source.Path);
        var path = Path.GetFullPath(source.Path);
        var extension = Path.GetExtension(path);
        if (extension.Equals(".exr", StringComparison.OrdinalIgnoreCase) || extension.Equals(".hdr", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".dpx", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("当前预览为 SDR 8 位，尚不支持 EXR、HDR 或 DPX 的高精度审片。");
        if (!source.IsSequence) return new(new Uri(path).AbsoluteUri, Path.GetFileName(path), Stills.Contains(extension));
        if (!Stills.Contains(extension)) throw new NotSupportedException("序列帧请选择 PNG、JPEG、BMP、WebP 或 TIFF 图片。");
        if (!double.IsFinite(source.SequenceFrameRate) || source.SequenceFrameRate is < 1 or > 240)
            throw new ArgumentOutOfRangeException(nameof(source), "序列帧速率必须在 1–240 fps 之间。");
        var match = Regex.Match(Path.GetFileNameWithoutExtension(path), @"^(.*?)(\d+)$", RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, out var start))
            throw new InvalidDataException("序列文件名须以连续帧号结尾，例如 shot_0001.png。");
        var prefix = match.Groups[1].Value;
        var digits = match.Groups[2].Length;
        var directory = Path.GetDirectoryName(path)!;
        var indexes = new SortedSet<int>();
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileNameWithoutExtension(file);
            if (!Path.GetExtension(file).Equals(extension, StringComparison.OrdinalIgnoreCase) ||
                !name.StartsWith(prefix, StringComparison.Ordinal) || name.Length != prefix.Length + digits) continue;
            if (int.TryParse(name.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= start)
                indexes.Add(index);
            if (indexes.Count > 100_000) throw new InvalidDataException("单条序列最多支持 100,000 帧。");
        }
        if (indexes.Count == 0 || indexes.Min != start) throw new InvalidDataException("无法读取所选序列的起始帧。");
        var previous = start;
        foreach (var index in indexes)
        {
            if (index != start && (long)index != (long)previous + 1)
                throw new InvalidDataException($"序列缺少第 {(long)previous + 1} 帧，不能静默跳过。");
            previous = index;
        }
        var (numerator, denominator) = RationalRate(source.SequenceFrameRate);
        var pattern = Path.Combine(directory.Replace("%", "%%", StringComparison.Ordinal),
            prefix.Replace("%", "%%", StringComparison.Ordinal) + $"%0{digits}d" + extension);
        var uri = new Uri(pattern).AbsoluteUri.Replace("file:", "imagesequence:", StringComparison.Ordinal);
        var rate = (double)numerator / denominator;
        return new($"{uri}?start-index={start}&stop-index={indexes.Max}&framerate={numerator}/{denominator}",
            Path.GetFileName(path) + $" 起 · {indexes.Count} 帧", false, rate, TimeSpan.FromSeconds(indexes.Count / rate));
    }

    private static (int Numerator, int Denominator) RationalRate(double rate)
    {
        foreach (var numerator in new[] { 24000, 30000, 60000, 120000 })
            if (Math.Abs(rate - Math.Round(numerator / 1001d, 3)) < 0.00001) return (numerator, 1001);
        var value = checked((int)Math.Round(rate * 1000));
        var divisor = GreatestCommonDivisor(value, 1000);
        return (value / divisor, 1000 / divisor);
    }
    private static int GreatestCommonDivisor(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }
}
