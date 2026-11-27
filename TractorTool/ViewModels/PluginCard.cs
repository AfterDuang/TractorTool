using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TractorTool.UI;

namespace TractorTool.ViewModels;

internal sealed record PluginCardState(string Name, string Version, string Description, string State, string Detail,
    bool HasIssue, ToolIconKind Icon, IRelayCommand? OpenCommand)
{
    public IImage? ImageSource { get; init; }
    public string PendingText { get; init; } = "";
    public IAsyncRelayCommand? ToggleCommand { get; init; }
    public bool RequestedEnabled { get; init; }
    public IRelayCommand? UninstallCommand { get; init; }
    public IRelayCommand? RollbackCommand { get; init; }
    public IRelayCommand? CancelChangeCommand { get; init; }
    public IRelayCommand? FinishUninstallCommand { get; init; }
    public IRelayCommand? CleanRetainedFilesCommand { get; init; }
}

/// <summary>A stable installation row; state refreshes must not replace its animated controls.</summary>
public sealed class PluginCard : ObservableObject
{
    private PluginCardState _state;
    internal PluginCard(string key, PluginCardState state) { Key = key; _state = state; }
    internal string Key { get; }
    internal void Apply(PluginCardState state)
    {
        _state = state;
        // Commit the whole snapshot before bindings run, including commands and the requested state.
        OnPropertyChanged(string.Empty);
    }

    public string Name => _state.Name;
    public string Version => _state.Version;
    public string Description => _state.Description;
    public string State => _state.State;
    public string Detail => _state.Detail;
    public bool HasIssue => _state.HasIssue;
    public ToolIconKind Icon => _state.Icon;
    public IImage? ImageSource => _state.ImageSource;
    public string PendingText => _state.PendingText;
    public bool RequestedEnabled => _state.RequestedEnabled;
    public IRelayCommand? OpenCommand => _state.OpenCommand;
    public IAsyncRelayCommand? ToggleCommand => _state.ToggleCommand;
    public IRelayCommand? UninstallCommand => _state.UninstallCommand;
    public IRelayCommand? RollbackCommand => _state.RollbackCommand;
    public IRelayCommand? CancelChangeCommand => _state.CancelChangeCommand;
    public IRelayCommand? FinishUninstallCommand => _state.FinishUninstallCommand;
    public IRelayCommand? CleanRetainedFilesCommand => _state.CleanRetainedFilesCommand;
    public bool CanOpen => OpenCommand is not null;
    public bool HasPending => PendingText.Length != 0;
    public bool CanToggle => ToggleCommand is not null;
    public bool CanManage => UninstallCommand is not null;
    public bool CanRollback => RollbackCommand is not null;
    public bool CanCancelChange => CancelChangeCommand is not null;
    public bool CanFinishUninstall => FinishUninstallCommand is not null;
    public bool CanCleanRetainedFiles => CleanRetainedFilesCommand is not null;
    public bool HasActions => CanOpen || CanManage || CanRollback || CanCancelChange || CanFinishUninstall || CanCleanRetainedFiles;
}
