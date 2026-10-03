using Avalonia.Controls;
using TractorTool.Core.Storage;
using TractorTool.UI;
namespace TractorTool.Services;
public sealed record RegisteredPage(string PluginId, string Id, string Title, bool IsSettings, Func<Control> Factory, PluginPageOptions Options);
public sealed class PageRegistry(string pluginId) : IPluginPageRegistry
{
    public List<RegisteredPage> Pages { get; } = [];
    public void AddNavigationPage(string id, string title, Func<Control> factory) => Add(id, title, factory, false, new());
    public void AddSettingsPage(string id, string title, Func<Control> factory) => Add(id, title, factory, true, new(ToolIconKind.Settings));
    public void AddNavigationPage(string id, string title, Func<Control> factory, PluginPageOptions options) => Add(id, title, factory, false, options);
    public void AddSettingsPage(string id, string title, Func<Control> factory, PluginPageOptions options) => Add(id, title, factory, true, options);
    private void Add(string id, string title, Func<Control> factory, bool settings, PluginPageOptions options)
    {
        AppPaths.ValidateId(id);
        ArgumentNullException.ThrowIfNull(factory);
        if (string.IsNullOrWhiteSpace(title) || Pages.Any(p => p.Id == id)) throw new InvalidDataException("页面名称为空或页面 ID 重复。");
        ArgumentNullException.ThrowIfNull(options);
        Pages.Add(new(pluginId, id, title, settings, factory, options));
    }
}
