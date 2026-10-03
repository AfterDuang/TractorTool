using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;

namespace TractorTool.UI.Controls;

/// <summary>One icon vocabulary for the host and independently loaded plugins.</summary>
public sealed class ToolIcon : ContentControl
{
    public static readonly StyledProperty<ToolIconKind> KindProperty =
        AvaloniaProperty.Register<ToolIcon, ToolIconKind>(nameof(Kind));
    public ToolIconKind Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    // The owner loads/caches/disposes images. This control only presents an existing image.
    public static readonly StyledProperty<IImage?> ImageSourceProperty =
        AvaloniaProperty.Register<ToolIcon, IImage?>(nameof(ImageSource));
    public IImage? ImageSource { get => GetValue(ImageSourceProperty); set => SetValue(ImageSourceProperty, value); }

    public ToolIcon()
    {
        Focusable = false;
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center;
        UpdateIcon();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty || change.Property == FontSizeProperty || change.Property == ImageSourceProperty) UpdateIcon();
    }

    private void UpdateIcon() => Content = ImageSource is { } image
        ? new FAImageIcon { Source = image, Width = FontSize, Height = FontSize }
        : new FASymbolIcon { Symbol = SymbolFor(Kind), FontSize = FontSize };
    internal static FASymbol SymbolFor(ToolIconKind kind) => kind switch
    {
        ToolIconKind.Home => FASymbol.Home,
        ToolIconKind.Logs => FASymbol.Document,
        ToolIconKind.Settings => FASymbol.Settings,
        ToolIconKind.Folder => FASymbol.Folder,
        ToolIconKind.Theme => FASymbol.DarkTheme,
        ToolIconKind.Palette => FASymbol.ColorLine,
        ToolIconKind.Motion => FASymbol.Play,
        ToolIconKind.Info => FASymbol.ContactInfo,
        ToolIconKind.Refresh => FASymbol.Refresh,
        ToolIconKind.Clipboard => FASymbol.Clipboard,
        ToolIconKind.Video => FASymbol.Video,
        ToolIconKind.Check => FASymbol.Checkmark,
        ToolIconKind.Warning => FASymbol.Alert,
        _ => FASymbol.AllApps
    };
}
