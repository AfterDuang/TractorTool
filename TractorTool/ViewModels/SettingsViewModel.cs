using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TractorTool.Core.Logging;
using TractorTool.Core.Storage;
using TractorTool.Services;
using System.Collections.ObjectModel;
using TractorTool.Platform;
using Avalonia.Threading;
namespace TractorTool.ViewModels;
public sealed record ThemeChoice(string Id, string Name);
public sealed record AccentChoice(string Value, string Name);
public partial class SettingsViewModel : ViewModelBase
{
    private readonly JsonFileStore<HostSettings> _store;
    private readonly AppLog _log;
    private HostSettings _settings;
    private bool _stopping;
    private bool _applying;
    private HostSettings? _pendingSettings;
    private Task _saveCompletion = Task.CompletedTask;
    // All queue state belongs to the UI thread; only immutable snapshots cross to the writer.
    public Task SaveCompletion => _saveCompletion;
    public AppPaths Paths { get; }
    public string ApplicationVersion => AppInfo.Version;
    public ThemeChoice[] Themes { get; } = [new("System", "跟随系统"), new("Light", "浅色"), new("Dark", "深色")];
    public AccentChoice[] Accents { get; } = [new("#2563EB", "蓝色"), new("#0F766E", "青绿色"), new("#7C3AED", "紫色"), new("#C2410C", "橙色")];
    [ObservableProperty] private ThemeChoice _selectedTheme;
    [ObservableProperty] private AccentChoice _selectedAccent;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSaveError)), NotifyCanExecuteChangedFor(nameof(RetrySaveCommand))]
    private string _saveError = "";
    public bool HasSaveError => SaveError.Length != 0;
    [ObservableProperty] private bool _enableAnimations;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasApplyError))] private string _applyError = "";
    public bool HasApplyError => ApplyError.Length != 0;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RetrySaveCommand))] private bool _isSaving;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasDirectoryStatus))] private string _directoryStatus = "";
    public bool HasDirectoryStatus => DirectoryStatus.Length != 0;
    public ObservableCollection<NavigationItem> PluginSettings { get; } = [];
    public bool HasPluginSettings => PluginSettings.Count != 0;
    public IAsyncRelayCommand? ExitCommand { get; internal set; }
    public SettingsViewModel(AppPaths paths, JsonFileStore<HostSettings> store, HostSettings settings, AppLog log)
    {
        Paths = paths; _store = store; _settings = settings; _log = log;
        _enableAnimations = settings.EnableAnimations;
        PluginSettings.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPluginSettings));
        _selectedTheme = Themes.Single(t => t.Id == settings.Theme);
        _selectedAccent = Accents.FirstOrDefault(a => a.Value.Equals(settings.Accent, StringComparison.OrdinalIgnoreCase)) ?? new(settings.Accent, settings.Accent);
        if (!Accents.Contains(_selectedAccent)) Accents = [.. Accents, _selectedAccent];
    }
    partial void OnSelectedThemeChanged(ThemeChoice value) => ApplyAppearance();
    partial void OnSelectedAccentChanged(AccentChoice value) => ApplyAppearance();
    partial void OnEnableAnimationsChanged(bool value) => ApplyAppearance();

    private void ApplyAppearance()
    {
        Dispatcher.UIThread.VerifyAccess();
        // ComboBox may clear its selection while attaching/rebuilding its template.
        if (_stopping || _applying || SelectedTheme is null || SelectedAccent is null) return;
        _applying = true;
        try
        {
            while (!_stopping && SelectedTheme is not null && SelectedAccent is not null)
            {
                var next = _settings with { Theme = SelectedTheme.Id, Accent = SelectedAccent.Value, EnableAnimations = EnableAnimations };
                if (!HasApplyError && next == _settings) return;
                try { ThemeService.Apply(next); }
                catch (Exception ex)
                {
                    ApplyError = "外观应用失败：" + ex.Message;
                    _log.Write("Settings", ApplyError, ex);
                    return;
                }
                ApplyError = "";
                _settings = next;
                if (_stopping) return;
                // Theme notifications can synchronously change another bound selection.
                // Reapply that final choice before queuing, rather than persisting the outer one.
                if (SelectedTheme is not null && SelectedAccent is not null &&
                    (SelectedTheme.Id != next.Theme || SelectedAccent.Value != next.Accent || EnableAnimations != next.EnableAnimations))
                    continue;
                QueueSave(next);
                return;
            }
        }
        finally { _applying = false; }
    }

    private bool CanRetrySave() => !_stopping && HasSaveError && !IsSaving;

    [RelayCommand(CanExecute = nameof(CanRetrySave))]
    private void RetrySave()
    {
        if (CanRetrySave()) QueueSave(_settings);
    }

    public Task BeginShutdown()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_stopping) return _saveCompletion;
        _stopping = true;
        RetrySaveCommand.NotifyCanExecuteChanged();
        // One retry on exit, never an unbounded failure loop; App owns the overall timeout.
        if (HasSaveError && !IsSaving) QueueSave(_settings);
        return _saveCompletion;
    }

    private void QueueSave(HostSettings settings)
    {
        _pendingSettings = settings;
        SaveError = "";
        if (IsSaving) return;
        // Register completion before starting work or notifying bindings.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _saveCompletion = completion.Task;
        IsSaving = true;
        _ = SavePendingAsync(completion);
    }

    private async Task SavePendingAsync(TaskCompletionSource completion)
    {
        try
        {
            while (_pendingSettings is { } next)
            {
                _pendingSettings = null;
                try
                {
                    await Task.Run(() => _store.Save(next));
                }
                catch (Exception ex)
                {
                    _log.Write("Settings", "外观自动保存失败", ex);
                    if (_pendingSettings is null)
                    {
                        SaveError = "当前外观已生效，但未保存：" + ex.Message;
                    }
                }
            }
        }
        finally
        {
            IsSaving = false;
            completion.TrySetResult();
        }
    }
    [RelayCommand] private void OpenDirectory(string path)
    {
        if (_stopping) return;
        try { DesktopActions.OpenDirectory(path); DirectoryStatus = ""; }
        catch (Exception ex) { DirectoryStatus = "无法打开目录：" + ex.Message; }
    }
}
