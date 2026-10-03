using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;

namespace TractorTool.UI.Controls;

/// <summary>Shared responsive setting row with host-owned appearance and an optional action control.</summary>
public sealed class SettingRow : ContentControl
{
    public static readonly StyledProperty<string> HeaderProperty = AvaloniaProperty.Register<SettingRow, string>(nameof(Header), "");
    public static readonly StyledProperty<string> DescriptionProperty = AvaloniaProperty.Register<SettingRow, string>(nameof(Description), "");
    public static readonly StyledProperty<ToolIconKind> IconProperty = AvaloniaProperty.Register<SettingRow, ToolIconKind>(nameof(Icon), ToolIconKind.Settings);
    public string Header { get => GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public string Description { get => GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public ToolIconKind Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public static readonly StyledProperty<IImage?> ImageSourceProperty = AvaloniaProperty.Register<SettingRow, IImage?>(nameof(ImageSource));
    public IImage? ImageSource { get => GetValue(ImageSourceProperty); set => SetValue(ImageSourceProperty, value); }
    public static readonly DirectProperty<SettingRow, FAIconSource> IconSourceProperty =
        AvaloniaProperty.RegisterDirect<SettingRow, FAIconSource>(nameof(IconSource), row => row.IconSource);
    private FAIconSource _iconSource = new FASymbolIconSource { Symbol = FASymbol.Settings };
    public FAIconSource IconSource => _iconSource;
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IconProperty || change.Property == ImageSourceProperty)
            SetAndRaise(IconSourceProperty, ref _iconSource, ImageSource is { } image
                ? new FAImageIconSource { Source = image }
                : new FASymbolIconSource { Symbol = ToolIcon.SymbolFor(Icon) });
    }
}
