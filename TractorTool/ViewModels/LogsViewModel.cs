using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TractorTool.Contracts;
using TractorTool.Core.Logging;
using TractorTool.Core.Storage;
using TractorTool.Platform;

namespace TractorTool.ViewModels;

public sealed record LogFilter(string Name, AppLogLevel? Level);
public sealed record LogRow(LogEntry Entry)
{
    public string Time => Entry.Timestamp.ToLocalTime().ToString("HH:mm:ss");
    public string Summary => Entry.Message.Replace('\r', ' ').Replace('\n', ' ');
    public bool IsInfo => Entry.Level == AppLogLevel.Info;
    public bool IsWarning => Entry.Level == AppLogLevel.Warning;
    public bool IsError => Entry.Level == AppLogLevel.Error;
}

public partial class LogsViewModel(AppPaths paths, AppLog log, TasksViewModel tasks) : ViewModelBase
{
    private LogSnapshot _displayed = new([], 0, 0, null);
    public TasksViewModel Tasks { get; } = tasks;
    public ObservableCollection<LogRow> Entries { get; } = [];
    public IReadOnlyList<LogFilter> Levels { get; } = [new("全部级别", null), new("INFO", AppLogLevel.Info),
        new("WARNING", AppLogLevel.Warning), new("ERROR", AppLogLevel.Error)];
    [ObservableProperty] private LogFilter? _selectedLevel;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _writeError = "";
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private int _selectedTab;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SelectedText)), NotifyPropertyChangedFor(nameof(HasSelection))]
    private LogRow? _selectedEntry;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasUnreadErrors))]
    private long _unreadErrors;
    public bool HasUnreadErrors => UnreadErrors > 0;
    public bool HasSelection => SelectedEntry is not null;
    public bool IsEmpty => Entries.Count == 0;
    public string SelectedText => SelectedEntry?.Entry.Text ?? "";
    public string Text => string.Join(Environment.NewLine, Entries.Select(row => row.Entry.Text));

    partial void OnSelectedLevelChanged(LogFilter? value) => Filter();
    partial void OnSearchChanged(string value) => Filter();
    partial void OnIsPausedChanged(bool value) => Refresh();

    public void Refresh()
    {
        var snapshot = log.Snapshot();
        UnreadErrors = snapshot.UnreadErrors;
        WriteError = snapshot.WriteError is null ? "" : "日志文件写入失败：" + snapshot.WriteError;
        if (!IsPaused) { _displayed = snapshot; Filter(); }
        else UpdateStatus();
    }

    private void Filter()
    {
        var selectedId = SelectedEntry?.Entry.Id;
        var visible = _displayed.Entries.Where(e => (SelectedLevel?.Level is not { } level || e.Level == level) &&
            (string.IsNullOrWhiteSpace(Search) || e.Text.Contains(Search, StringComparison.OrdinalIgnoreCase))).Reverse().ToArray();
        var ids = visible.Select(e => e.Id).ToHashSet();
        foreach (var row in Entries.Where(r => !ids.Contains(r.Entry.Id)).ToArray()) Entries.Remove(row);
        for (var index = 0; index < visible.Length; index++)
        {
            var entry = visible[index];
            if (index < Entries.Count && Entries[index].Entry.Id == entry.Id) continue;
            var existing = Entries.FirstOrDefault(r => r.Entry.Id == entry.Id);
            if (existing is null) Entries.Insert(index, new(entry));
            else Entries.Move(Entries.IndexOf(existing), index);
        }
        SelectedEntry = Entries.FirstOrDefault(r => r.Entry.Id == selectedId);
        OnPropertyChanged(nameof(IsEmpty));
        UpdateStatus();
    }

    private void UpdateStatus() => Status = $"{(IsPaused ? "显示已暂停" : "实时更新")} · {Entries.Count} / {_displayed.Entries.Count} 条记录 · {UnreadErrors} 条未读错误";

    [RelayCommand] private void MarkErrorsRead()
    {
        // Paused/filtered display never acknowledges errors arriving after its snapshot.
        log.AcknowledgeErrors(_displayed.TotalErrors);
        Refresh();
    }
    [RelayCommand] private void OpenDirectory()
    {
        try { DesktopActions.OpenDirectory(paths.LogRoot); }
        catch (Exception ex) { log.Write("Logging", "无法打开日志目录", ex); Status = "无法打开日志目录：" + ex.Message; }
    }
}
