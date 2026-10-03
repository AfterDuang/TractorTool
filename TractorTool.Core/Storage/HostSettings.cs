using TractorTool.Contracts;

namespace TractorTool.Core.Storage;

public sealed record HostSettings : IVersionedSettings
{
    public int SchemaVersion { get; init; } = 1;
    public string Theme { get; init; } = "System";
    public string Accent { get; init; } = "#2563EB";
    public bool EnableAnimations { get; init; } = true;
    public string[] DisabledPlugins { get; init; } = [];

    public static void Validate(HostSettings settings)
    {
        if (settings.Theme is not ("System" or "Light" or "Dark")) throw new InvalidDataException("未知的主题模式。");
        if (!System.Text.RegularExpressions.Regex.IsMatch(settings.Accent ?? "", "^#[0-9A-Fa-f]{6}$"))
            throw new InvalidDataException("主题色必须是 #RRGGBB。");
        if (settings.DisabledPlugins is null) throw new InvalidDataException("禁用插件列表不能为空值。");
        foreach (var id in settings.DisabledPlugins) AppPaths.ValidateId(id);
    }
}

public sealed class PluginSettingsStore(string root) : IPluginSettingsStore
{
    private string FileFor(string name)
    {
        AppPaths.ValidateId(name);
        return Path.Combine(root, name + ".json");
    }
    public T Load<T>(string name) where T : IVersionedSettings, new() => new JsonFileStore<T>(FileFor(name)).Load();
    public void Save<T>(string name, T value) where T : IVersionedSettings, new() => new JsonFileStore<T>(FileFor(name)).Save(value);
}
