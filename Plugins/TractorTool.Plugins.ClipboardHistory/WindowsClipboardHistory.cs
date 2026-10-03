using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;

namespace TractorTool.Plugins.ClipboardHistory;

public enum HistoryStatus { Success, Disabled, Denied, Failed }
public enum HistoryKind { Text, Image, Html, RichText, Files, Other }
public enum HistoryCopyStatus { Success, Denied, Deleted, Failed }
public sealed record HistoryEntry(string Id, string Timestamp, HistoryKind Kind, string Summary)
{
    public string TypeName => Kind switch {
        HistoryKind.Text => "文本", HistoryKind.Image => "图片", HistoryKind.Html => "网页片段",
        HistoryKind.RichText => "富文本", HistoryKind.Files => "文件", _ => "其他内容" };
}
public sealed record HistoryResult(HistoryStatus Status, IReadOnlyList<HistoryEntry> Items);
public sealed class HistoryPreview(string text, string detail, Bitmap? image = null) : IDisposable
{
    public string Text { get; } = text;
    public string Detail { get; } = detail;
    public Bitmap? Image { get; } = image;
    public bool HasText => Text.Length != 0;
    public bool HasDetail => Detail.Length != 0;
    public bool HasImage => Image is not null;
    public void Dispose() => Image?.Dispose();
}

public interface IClipboardHistoryService : IDisposable
{
    event EventHandler? HistoryChanged;
    void StartWatching();
    Task<HistoryResult> ReadAsync(CancellationToken cancellationToken);
    Task<HistoryPreview> PreviewAsync(string id, CancellationToken cancellationToken);
    HistoryCopyStatus Copy(string id);
}

internal sealed class WindowsClipboardHistory : IClipboardHistoryService
{
    private readonly Dictionary<string, ClipboardHistoryItem> _items = new(StringComparer.Ordinal);
    private bool _watching;
    private bool _disposed;
    public event EventHandler? HistoryChanged;

    public void StartWatching()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_watching) return;
        Clipboard.HistoryChanged += OnHistoryChanged;
        try { Clipboard.HistoryEnabledChanged += OnEnabledChanged; }
        catch { Clipboard.HistoryChanged -= OnHistoryChanged; throw; }
        _watching = true;
    }
    // The callback never reads clipboard data. The page dispatches/coalesces it on its UI thread.
    private void OnHistoryChanged(object? sender, ClipboardHistoryChangedEventArgs args) => HistoryChanged?.Invoke(this, EventArgs.Empty);
    private void OnEnabledChanged(object? sender, object args) => HistoryChanged?.Invoke(this, EventArgs.Empty);

    public async Task<HistoryResult> ReadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var result = await Clipboard.GetHistoryItemsAsync().AsTask(cancellationToken);
        if (result.Status != ClipboardHistoryItemsResultStatus.Success)
        {
            if (result.Status == ClipboardHistoryItemsResultStatus.ClipboardHistoryDisabled) _items.Clear();
            return new(result.Status == ClipboardHistoryItemsResultStatus.ClipboardHistoryDisabled
                ? HistoryStatus.Disabled : result.Status == ClipboardHistoryItemsResultStatus.AccessDenied
                    ? HistoryStatus.Denied : HistoryStatus.Failed, []);
        }
        var entries = new List<HistoryEntry>();
        var snapshot = new Dictionary<string, ClipboardHistoryItem>(StringComparer.Ordinal);
        foreach (var item in result.Items.OrderByDescending(item => item.Timestamp))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var kind = HistoryKind.Other;
            var summary = "选择以预览或复制原内容";
            try
            {
                kind = GetKind(item.Content);
                summary = kind == HistoryKind.Text
                    ? Summary(await item.Content.GetTextAsync().AsTask(cancellationToken))
                    : kind == HistoryKind.Image ? "选择以预览图片" : string.Join(" · ", item.Content.AvailableFormats);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { summary = "内容暂时无法预览"; }
            snapshot[item.Id] = item;
            entries.Add(new(item.Id, item.Timestamp.ToLocalTime().ToString("g"), kind, summary));
        }
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _items.Clear();
        foreach (var pair in snapshot) _items.Add(pair.Key, pair.Value);
        return new(HistoryStatus.Success, entries);
    }

    private static HistoryKind GetKind(DataPackageView data) =>
        data.Contains(StandardDataFormats.Bitmap) ? HistoryKind.Image :
        data.Contains(StandardDataFormats.Text) ? HistoryKind.Text :
        data.Contains(StandardDataFormats.Html) ? HistoryKind.Html :
        data.Contains(StandardDataFormats.Rtf) ? HistoryKind.RichText :
        data.Contains(StandardDataFormats.StorageItems) ? HistoryKind.Files : HistoryKind.Other;

    private static string Summary(string text) => string.IsNullOrWhiteSpace(text) ? "空白文本" :
        text[..Math.Min(text.Length, 180)].Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');

    public async Task<HistoryPreview> PreviewAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed || !_items.TryGetValue(id, out var item)) return new("", "此历史项已失效，请刷新。");
        var data = item.Content;
        switch (GetKind(data))
        {
            case HistoryKind.Image: return await ReadImageAsync(data, cancellationToken);
            case HistoryKind.Text: return TextPreview(await data.GetTextAsync().AsTask(cancellationToken));
            // Display source as inert text; never execute HTML, load remote resources or open files.
            case HistoryKind.Html: return TextPreview(await data.GetHtmlFormatAsync().AsTask(cancellationToken), "网页片段的源码预览；复制时保留原格式。");
            case HistoryKind.RichText: return TextPreview(await data.GetRtfAsync().AsTask(cancellationToken), "富文本的源码预览；复制时保留原格式。");
            case HistoryKind.Files:
                var files = await data.GetStorageItemsAsync().AsTask(cancellationToken);
                return TextPreview(string.Join(Environment.NewLine, files.Select(f => f.Name)), "文件名预览；复制时使用系统原内容。");
            default: return new("", "暂不支持预览此类型，可以复制原内容。\n格式：" + string.Join("、", data.AvailableFormats));
        }
    }

    private static HistoryPreview TextPreview(string text, string detail = "")
    {
        const int limit = 20000;
        if (text.Length > limit) return new(text[..limit], "仅预览前 20,000 个字符，复制时保留完整原内容。");
        return new(text, text.Length == 0 ? "此项目的文本为空。" : detail);
    }

    private static async Task<HistoryPreview> ReadImageAsync(DataPackageView data, CancellationToken token)
    {
        var reference = await data.GetBitmapAsync().AsTask(token);
        using var stream = await reference.OpenReadAsync().AsTask(token);
        if (stream.Size > 32 * 1024 * 1024) return new("", "图片较大，暂不生成预览；仍可复制原图片。");
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token);
        if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0 || (ulong)decoder.PixelWidth * decoder.PixelHeight > 100_000_000)
            return new("", "图片尺寸超出预览范围；仍可复制原图片。");
        var scale = Math.Min(1d, 1600d / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var width = Math.Max(1, (int)(decoder.PixelWidth * scale));
        var height = Math.Max(1, (int)(decoder.PixelHeight * scale));
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            new BitmapTransform { ScaledWidth = (uint)width, ScaledHeight = (uint)height },
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(token);
        token.ThrowIfCancellationRequested();
        var bytes = pixels.DetachPixelData();
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        try
        {
            using var target = bitmap.Lock();
            for (int row = 0; row < height; row++) Marshal.Copy(bytes, row * width * 4, target.Address + row * target.RowBytes, width * 4);
            return new("", $"{decoder.PixelWidth} × {decoder.PixelHeight} 像素", bitmap);
        }
        catch { bitmap.Dispose(); throw; }
    }

    public HistoryCopyStatus Copy(string id)
    {
        if (_disposed || !_items.TryGetValue(id, out var item)) return HistoryCopyStatus.Deleted;
        // Restore the original item, including formats we cannot preview; never copy the truncated display text.
        return Clipboard.SetHistoryItemAsContent(item) switch {
            SetHistoryItemAsContentStatus.Success => HistoryCopyStatus.Success,
            SetHistoryItemAsContentStatus.AccessDenied => HistoryCopyStatus.Denied,
            SetHistoryItemAsContentStatus.ItemDeleted => HistoryCopyStatus.Deleted, _ => HistoryCopyStatus.Failed };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_watching)
            {
                try { Clipboard.HistoryChanged -= OnHistoryChanged; }
                finally { Clipboard.HistoryEnabledChanged -= OnEnabledChanged; }
            }
        }
        finally { _watching = false; _items.Clear(); HistoryChanged = null; }
    }
}
