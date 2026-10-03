using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TractorTool.Core.Logging;
using TractorTool.Core.Storage;
using TractorTool.Platform;

namespace TractorTool.ViewModels;
public partial class LogsViewModel(AppPaths paths, AppLog log) : ViewModelBase
{
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _status = "实时显示最近 300 条记录";
    [ObservableProperty] private bool _isPaused;
    partial void OnIsPausedChanged(bool value)
    {
        Status = value ? "已暂停界面更新，日志仍在记录" : "实时显示最近 300 条记录";
        if (!value) Refresh();
    }
    public void Refresh() { if (!IsPaused) Text = log.ReadRecent(); }
    [RelayCommand] private void OpenDirectory()
    {
        try { DesktopActions.OpenDirectory(paths.LogRoot); }
        catch (Exception ex) { Status = "无法打开日志目录：" + ex.Message; }
    }
}
