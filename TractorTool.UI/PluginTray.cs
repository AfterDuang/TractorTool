namespace TractorTool.UI;

/// <summary>Optional UI API 1.5 capability, registered once after plugin initialization.</summary>
public interface IPluginTrayModule
{
    void RegisterTrayMenu(IPluginTrayMenu menu);
}

/// <summary>The host supplies one submenu named after the plugin. Add only during registration.</summary>
public interface IPluginTrayMenu
{
    IPluginTrayItem Add(string id, PluginTrayItemState state, Func<CancellationToken, Task> execute);
}

public interface IPluginTrayItem
{
    /// <summary>Thread-safe; ignored after plugin shutdown. State reflects completed business changes.</summary>
    void Update(PluginTrayItemState state);
}

/// <param name="IsChecked">Null for a normal command; otherwise displays the plugin's checked state.</param>
public sealed record PluginTrayItemState(string Text, bool IsEnabled = true, bool? IsChecked = null);
