using Avalonia.Controls;
namespace TractorTool.UI;
public static class UiApi
{
    public const string Version = "1.5";
    public const string AvaloniaVersion = "12.1.2";
}
public interface IPluginUiModule { void RegisterPages(IPluginPageRegistry registry); }
public interface IPluginPageRegistry
{
    void AddNavigationPage(string id, string title, Func<Control> factory);
    void AddSettingsPage(string id, string title, Func<Control> factory);
    // Keep the original signatures so UI 1.0 plugins continue to load.
    void AddNavigationPage(string id, string title, Func<Control> factory, PluginPageOptions options)
        => AddNavigationPage(id, title, factory);
    void AddSettingsPage(string id, string title, Func<Control> factory, PluginPageOptions options)
        => AddSettingsPage(id, title, factory);
}
public sealed record PluginPageOptions(ToolIconKind Icon = ToolIconKind.Tools, string Description = "")
{
    /// <summary>UI API 1.3. The page supplies its own heading and local navigation.</summary>
    public bool UsesCustomHeader { get; init; }
    /// <summary>Optional PNG path relative to this plugin's version directory (UI API 1.2).</summary>
    public string? IconPath { get; init; }
}
public enum ToolIconKind { Tools, Logs, Settings, Folder, Theme, Palette, Motion, Info, Refresh, Clipboard, Video, Check, Warning, Home }
/// <summary>Page-only cleanup; must not cancel jobs or the plugin lifetime.</summary>
public interface IPluginPageLifetime { void OnNavigatedFrom(); }
