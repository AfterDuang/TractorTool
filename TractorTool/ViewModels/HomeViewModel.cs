using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TractorTool.ViewModels;

public sealed record HomePluginCard(PluginCard Plugin, IRelayCommand OpenCommand)
{
    public string ActionText => Plugin.CanOpen ? "打开工具" : "查看插件";
}

public partial class HomeViewModel(IRelayCommand showPluginsCommand, IRelayCommand showSettingsCommand) : ViewModelBase
{
    public IRelayCommand ShowPluginsCommand { get; } = showPluginsCommand;
    public IRelayCommand ShowSettingsCommand { get; } = showSettingsCommand;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsEmpty))]
    private IReadOnlyList<HomePluginCard> _installedPlugins = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoading = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasDirectoryError)), NotifyPropertyChangedFor(nameof(IsEmpty))]
    private string? _directoryError;
    [ObservableProperty] private string _summary = "正在加载本地插件…";

    public bool HasDirectoryError => !string.IsNullOrWhiteSpace(DirectoryError);
    public bool IsEmpty => !IsLoading && !HasDirectoryError && InstalledPlugins.Count == 0;

    public void Update(IEnumerable<PluginCard> plugins, string summary, string? directoryError)
    {
        // Reuse the host's catalog and navigation commands. Never instantiate plugin pages here.
        InstalledPlugins = plugins.Select(plugin => new HomePluginCard(
            plugin, plugin.OpenCommand ?? ShowPluginsCommand)).ToArray();
        Summary = summary;
        DirectoryError = directoryError;
        IsLoading = false;
    }
}
