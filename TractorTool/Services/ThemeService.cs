using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using FluentAvalonia.Styling;
using TractorTool.Core.Storage;
using TractorTool.UI;
namespace TractorTool.Services;
public static class ThemeService
{
    public static void Apply(HostSettings settings)
    {
        HostSettings.Validate(settings);
        var app = Application.Current!;
        Motion.Configure(app, settings.EnableAnimations);
        app.RequestedThemeVariant = settings.Theme switch { "Light" => ThemeVariant.Light, "Dark" => ThemeVariant.Dark, _ => ThemeVariant.Default };
        app.Styles.OfType<FluentAvaloniaTheme>().Single().CustomAccentColor = Color.Parse(settings.Accent);
    }
}
