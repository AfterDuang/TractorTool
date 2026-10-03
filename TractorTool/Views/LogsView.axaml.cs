using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using TractorTool.ViewModels;
namespace TractorTool.Views;
public partial class LogsView : UserControl
{
    public LogsView() => InitializeComponent();
    private async void CopyLogs(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LogsViewModel vm) return;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("剪贴板暂不可用。");
            await clipboard.SetTextAsync(vm.Text);
            vm.Status = "已复制当前显示的记录";
        }
        catch (Exception ex) { vm.Status = "复制失败：" + ex.Message; }
    }
}
