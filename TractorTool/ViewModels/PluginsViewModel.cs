using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TractorTool.Core.Plugins;
using TractorTool.Core.Storage;
using TractorTool.Platform;
using TractorTool.Services;
using TractorTool.UI;

namespace TractorTool.ViewModels;

public sealed record PluginCard(string Name, string Version, string Description, string State, string Detail,
    bool HasIssue, ToolIconKind Icon, IRelayCommand? OpenCommand)
{
    public bool CanOpen => OpenCommand is not null;
    public IImage? ImageSource { get; init; }
}

public partial class PluginsViewModel(AppPaths paths, PluginHost host, IRelayCommand showLogsCommand) : ViewModelBase
{
    public string PluginRoot => paths.PluginRoot;
    public IRelayCommand ShowLogsCommand { get; } = showLogsCommand;
    public ObservableCollection<PluginCard> Items { get; } = [];
    public IEnumerable<PluginCard> FilteredItems => Items.Where(p => string.IsNullOrWhiteSpace(Search) ||
        p.Name.Contains(Search, StringComparison.CurrentCultureIgnoreCase));
    public bool NoMatches => !IsLoading && !HasDirectoryError && HasItems && !string.IsNullOrWhiteSpace(Search) && !FilteredItems.Any();
    public bool HasItems => Items.Count != 0;
    public bool IsEmpty => !IsLoading && !HasDirectoryError && !HasItems;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FilteredItems)), NotifyPropertyChangedFor(nameof(NoMatches))] private string _search = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasOperationStatus))] private string _operationStatus = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasDirectoryError)), NotifyPropertyChangedFor(nameof(IsEmpty)), NotifyPropertyChangedFor(nameof(NoMatches))] private string? _directoryError;
    public bool HasOperationStatus => !string.IsNullOrWhiteSpace(OperationStatus);
    public bool HasDirectoryError => !string.IsNullOrWhiteSpace(DirectoryError);
    [ObservableProperty] private string _summary = "正在检查本地插件…";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsEmpty)), NotifyPropertyChangedFor(nameof(NoMatches))] private bool _isLoading = true;
    public void Update(IReadOnlyList<RegisteredPage> pages, IEnumerable<NavigationItem> navigation)
    {
        Items.Clear();
        foreach (var record in host.Records)
        {
            var page = pages.FirstOrDefault(p => !p.IsSettings && p.PluginId == record.Manifest?.Id);
            var item = navigation.FirstOrDefault(n => n.Key == page?.PluginId + "/" + page?.Id);
            var state = record.State switch {
                PluginState.Loaded => "已启用", PluginState.Disabled => "已禁用",
                PluginState.Incompatible => "不兼容", PluginState.Invalid => "清单无效",
                PluginState.Duplicate => "版本冲突", PluginState.Failed => "加载失败", _ => "等待加载" };
            Items.Add(new(record.Name, record.Manifest?.Version ?? "未知版本",
                record.Manifest?.Description ?? "无法读取插件信息。", state, record.Detail,
                record.State is not (PluginState.Loaded or PluginState.Disabled),
                page?.Options.Icon ?? ToolIconKind.Tools, item?.OpenCommand) { ImageSource = item?.ImageSource });
        }
        DirectoryError = host.DirectoryError;
        Summary = $"{Items.Count} 个本地插件 · {host.Records.Count(p => p.State == PluginState.Loaded)} 个已启用";
        IsLoading = false;
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(FilteredItems));
        OnPropertyChanged(nameof(NoMatches));
    }
    [RelayCommand] private void OpenDirectory()
    {
        try { DesktopActions.OpenDirectory(PluginRoot); OperationStatus = ""; }
        catch (Exception ex) { OperationStatus = "无法打开插件目录：" + ex.Message; }
    }
}
