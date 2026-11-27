using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace PythonEnvironmentProbePlugin;

internal sealed class ProbeView : ScrollViewer
{
    public ProbeView(ProbeViewModel model)
    {
        Classes.Add("pageScroll");
        DataContext = model;
        Content = new StackPanel
        {
            Classes = { "pageBody" }, Spacing = 16,
            Children =
            {
                new TextBlock { Text = "Python 3.13.18 / idna 3.11", Classes = { "sectionTitle" } },
                new TextBlock { Text = "每行输入一个域名。执行时保存输入并申请独立环境；无效域名会单独列出。切页或隐藏窗口不会停止运行。" },
                new TextBox
                {
                    AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 130, MaxHeight = 210,
                    MaxLength = ProbeViewModel.InputLimit,
                    [!TextBox.TextProperty] = new Binding(nameof(ProbeViewModel.Input)) { Mode = BindingMode.TwoWay },
                    [!IsEnabledProperty] = new Binding(nameof(ProbeViewModel.CanEdit)),
                    [AutomationProperties.NameProperty] = "待转换域名，每行一个"
                },
                new ComboBox
                {
                    ItemsSource = ProbeViewModel.Scenarios, HorizontalAlignment = HorizontalAlignment.Stretch,
                    [!ComboBox.SelectedIndexProperty] = new Binding(nameof(ProbeViewModel.SelectedScenario)) { Mode = BindingMode.TwoWay },
                    [!IsEnabledProperty] = new Binding(nameof(ProbeViewModel.CanEdit)),
                    [AutomationProperties.NameProperty] = "测试场景"
                },
                new WrapPanel
                {
                    ItemSpacing = 8, LineSpacing = 8,
                    Children =
                    {
                        new Button { Content = "准备环境并执行", Command = model.RunCommand, Classes = { "accent" } },
                        new Button { Content = "取消当前操作", Command = model.CancelCommand }
                    }
                },
                new ProgressBar { IsIndeterminate = true, Height = 3, [!IsVisibleProperty] = new Binding(nameof(ProbeViewModel.IsRunning)) },
                new SelectableTextBlock { [!SelectableTextBlock.TextProperty] = new Binding(nameof(ProbeViewModel.Status)), TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "处理结果", Classes = { "sectionTitle" } },
                new TextBox
                {
                    IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 110, MaxHeight = 280,
                    [!TextBox.TextProperty] = new Binding(nameof(ProbeViewModel.ResultText)),
                    [AutomationProperties.NameProperty] = "域名转换结果"
                },
                new TextBlock { Text = "进程输出", Classes = { "sectionTitle" } },
                new TextBox
                {
                    IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, MaxHeight = 200,
                    [!TextBox.TextProperty] = new Binding(nameof(ProbeViewModel.ProcessOutput)),
                    [AutomationProperties.NameProperty] = "Python 进程输出，最多保留末尾 16 KiB 字符"
                },
                new Expander
                {
                    Header = "环境与报告位置", HorizontalAlignment = HorizontalAlignment.Stretch,
                    Content = new SelectableTextBlock
                    {
                        [!SelectableTextBlock.TextProperty] = new Binding(nameof(ProbeViewModel.Diagnostics)),
                        TextWrapping = TextWrapping.Wrap
                    }
                },
                new TextBlock
                {
                    Text = "持续运行用于观察取消和占用；预期进程失败会在“日志 → 任务进度”留下失败记录。离线复用请先完成一次运行，再断网重试。", Classes = { "caption" }
                }
            }
        };
    }
}
