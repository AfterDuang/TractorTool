using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using FluentAvalonia.Styling;
using FluentAvalonia.Core;
using TractorTool.Core.Storage;
namespace TractorTool.Services;
public static class ThemeService
{
    public static void Apply(HostSettings settings)
    {
        HostSettings.Validate(settings);
        var app = Application.Current!;
        app.RequestedThemeVariant = settings.Theme switch { "Light" => ThemeVariant.Light, "Dark" => ThemeVariant.Dark, _ => ThemeVariant.Default };
        app.Styles.OfType<FluentAvaloniaTheme>().Single().CustomAccentColor = Color.Parse(settings.Accent);
        FAUISettings.SetAnimationsEnabledAtAppLevel(settings.EnableAnimations);
    }
}
