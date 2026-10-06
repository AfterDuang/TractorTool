using System.Buffers.Binary;
using Avalonia.Media.Imaging;
using FluentAvalonia.UI.Controls;
using TractorTool.Core.Logging;
using TractorTool.Services;
using TractorTool.UI;
using TractorTool.UI.Controls;

internal static class IconChecks
{
    public static void Run(Action<string, Action> check, string published, string output)
    {
        var root = Path.Combine(output, "icons-" + Guid.NewGuid().ToString("N"));
        var package = Path.Combine(root, "package");
        Directory.CreateDirectory(package);
        var source = Path.Combine(Directory.GetDirectories(Path.Combine(published, "Plugins", "Test.Independent")).Single(), "Assets", "icon.png");
        var valid = Path.Combine(package, "valid.png");
        File.Copy(source, valid);
        var log = new AppLog(Path.Combine(root, "logs"));
        using var icons = new NavigationIconStore(log);

        check("Icon decoding is cached and closes the source file", () =>
        {
            var image = icons.GetPluginIcon(package, "valid.png", "Test.Icons");
            Require(image is Bitmap { PixelSize.Width: 256, PixelSize.Height: 256 });
            Require(ReferenceEquals(image, icons.GetPluginIcon(package, "valid.png", "Test.Icons")));
            using var exclusive = new FileStream(valid, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Require(exclusive.Length > 0);
        });

        check("Tool and setting icons switch between images and symbols without changing ownership", () =>
        {
            var image = icons.GetPluginIcon(package, "valid.png", "Test.Icons")!;
            var tool = new ToolIcon { Kind = ToolIconKind.Tools, ImageSource = image };
            var setting = new SettingRow { Icon = ToolIconKind.Settings, ImageSource = image };
            Require(tool.Content is FAImageIcon && setting.IconSource is FAImageIconSource);
            tool.ImageSource = null;
            setting.ImageSource = null;
            Require(tool.Content is FASymbolIcon && setting.IconSource is FASymbolIconSource);
            using var stream = new MemoryStream();
            ((Bitmap)image).Save(stream, PngBitmapEncoderOptions.Default);
            Require(stream.Length > 0);
        });

        check("Missing corrupt and out-of-package icons fall back and do not open external images", () =>
        {
            File.Copy(source, Path.Combine(root, "outside.png"));
            File.WriteAllText(Path.Combine(package, "corrupt.png"), "invalid PNG");
            foreach (var path in new[] { "missing.png", "corrupt.png", "../outside.png", valid, "https://example.invalid/icon.png" })
                Require(icons.GetPluginIcon(package, path, "Test.Icons") is null);
            var before = log.ReadRecent();
            Require(icons.GetPluginIcon(package, "missing.png", "Test.Icons") is null);
            Require(log.ReadRecent() == before);
        });

        check("Icon dimensions and compressed length are bounded before decoding", () =>
        {
            var oversized = File.ReadAllBytes(source);
            BinaryPrimitives.WriteUInt32BigEndian(oversized.AsSpan(16, 4), 1025);
            File.WriteAllBytes(Path.Combine(package, "wide.png"), oversized);
            Require(icons.GetPluginIcon(package, "wide.png", "Test.Icons") is null);
            Require(log.ReadRecent().Contains("1024"));
            using (var large = File.Create(Path.Combine(package, "large.png"))) large.SetLength(2 * 1024 * 1024 + 1);
            Require(icons.GetPluginIcon(package, "large.png", "Test.Icons") is null);
            Require(log.ReadRecent().Contains("2 MiB"));
        });
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Icon regression assertion failed.");
    }
}
