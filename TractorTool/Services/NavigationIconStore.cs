using System.Buffers.Binary;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using TractorTool.Contracts;
using TractorTool.Core.Plugins;
using TractorTool.UI;

namespace TractorTool.Services;

/// <summary>Window-owned images, loaded once on the UI thread and released after the window closes.</summary>
public sealed class NavigationIconStore(IAppLog log) : IDisposable
{
    private readonly Dictionary<string, Bitmap?> _images = new(OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private bool _disposed;

    public IImage? GetHostIcon(ToolIconKind kind)
    {
        var name = kind switch
        {
            ToolIconKind.Home => "home", ToolIconKind.Logs => "logs",
            ToolIconKind.Settings => "settings", _ => "tools"
        };
        var uri = new Uri($"avares://TractorTool/Assets/Icons/{name}.png");
        return GetOrLoad(uri.ToString(), () => AssetLoader.Open(uri), "Host");
    }

    public IImage? GetPluginIcon(string pluginDirectory, string? relativePath, string pluginId)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        // Cache failures as well: repeated pages sharing a bad icon should produce one diagnostic.
        return GetOrLoad($"plugin:{pluginDirectory}/{relativePath}", () =>
        {
            if (Path.IsPathRooted(relativePath) || relativePath.Contains(':') ||
                relativePath.Split('/', '\\').Any(part => part is ".." or "." or "") ||
                !relativePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("图标必须为插件版本包内的 PNG 相对路径。");
            var path = ManifestValidator.PackagePath(pluginDirectory, Path.GetFullPath(Path.Combine(pluginDirectory, relativePath)));
            return File.OpenRead(path);
        }, pluginId);
    }

    private Bitmap? GetOrLoad(string key, Func<Stream> open, string source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_images.TryGetValue(key, out var cached)) return cached;
        Bitmap? image = null;
        try
        {
            using var stream = open();
            // Bound both compressed input and decoded dimensions before invoking the image decoder.
            if (stream.Length > 2 * 1024 * 1024) throw new InvalidDataException("图标超过 2 MiB。");
            Span<byte> header = stackalloc byte[24];
            stream.ReadExactly(header);
            if (!header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                BinaryPrimitives.ReadUInt32BigEndian(header[8..12]) != 13 || !header[12..16].SequenceEqual("IHDR"u8))
                throw new InvalidDataException("图标不是有效的 PNG。");
            var width = BinaryPrimitives.ReadUInt32BigEndian(header[16..20]);
            var height = BinaryPrimitives.ReadUInt32BigEndian(header[20..24]);
            if (width is 0 or > 1024 || height is 0 or > 1024)
                throw new InvalidDataException("图标尺寸必须在 1–1024 像素之间。");
            stream.Position = 0;
            image = new Bitmap(stream);
        }
        catch (Exception error)
        {
            log.Write(AppLogLevel.Warning, source, $"图标加载失败，使用默认符号：{key}", error);
        }
        _images.Add(key, image);
        return image;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var image in _images.Values) image?.Dispose();
        _images.Clear();
    }
}
