using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;

namespace NodeEnvironmentProbePlugin;

public sealed class ProbeView : ScrollViewer
{
    public ProbeView(ProbeViewModel model)
    {
        DataContext = model;
        Classes.Add("pageScroll");
        var input = new TextBox
        {
            AcceptsReturn = true, MinHeight = 150, MaxHeight = 240, MaxLength = 16000,
            TextWrapping = TextWrapping.Wrap,
            [!TextBox.TextProperty] = new Binding(nameof(ProbeViewModel.Input)) { Mode = BindingMode.TwoWay },
            [!IsEnabledProperty] = new Binding(nameof(ProbeViewModel.CanEdit))
        };
        AutomationProperties.SetName(input, "每行输入一个数值或文本");
        var scenarios = new ComboBox
        {
            ItemsSource = ProbeViewModel.Scenarios, MinWidth = 220,
            [!ComboBox.SelectedIndexProperty] = new Binding(nameof(ProbeViewModel.ScenarioIndex)) { Mode = BindingMode.TwoWay },
            [!IsEnabledProperty] = new Binding(nameof(ProbeViewModel.CanEdit))
        };
        AutomationProperties.SetName(scenarios, "测试场景");
        Content = new StackPanel
        {
            Classes = { "pageBody" }, Spacing = 16,
            Children =
            {
                new TextBlock { Text = "Node 24.22.3 / is-number 7.0.0", Classes = { "sectionTitle" } },
                new TextBlock { Text = "每行一个数值或文本，空行忽略，最多 200 行。运行时保存输入；切页或隐藏窗口不取消任务。" },
                input,
                new WrapPanel
                {
                    ItemSpacing = 8, LineSpacing = 8,
                    Children =
                    {
                        scenarios,
                        new Button { Content = "准备环境并执行", Command = model.RunCommand, Classes = { "accent" } },
                        new Button { Content = "取消当前操作", Command = model.CancelCommand }
                    }
                },
                new TextBlock { Text = "持续运行会重复真实计算约 60 秒，便于查看占用和取消。预期失败会主动退出并在任务/日志中记一次失败，随后可重新运行。", Classes = { "caption" } },
                new TextBlock { [!TextBlock.TextProperty] = new Binding(nameof(ProbeViewModel.Status)) },
                new ProgressBar { Minimum = 0, Maximum = 100, [!ProgressBar.ValueProperty] = new Binding(nameof(ProbeViewModel.Progress)) },
                new TextBlock { Text = "处理结果", Classes = { "sectionTitle" } },
                new TextBox
                {
                    IsReadOnly = true, AcceptsReturn = true, MinHeight = 120, MaxHeight = 260,
                    TextWrapping = TextWrapping.Wrap,
                    [!TextBox.TextProperty] = new Binding(nameof(ProbeViewModel.ResultText))
                },
                new Expander
                {
                    Header = "进程输出与路径", IsExpanded = true,
                    Content = new StackPanel
                    {
                        Spacing = 8,
                        Children =
                        {
                            new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, [!TextBlock.TextProperty] = new Binding(nameof(ProbeViewModel.Paths)) },
                            new TextBox
                            {
                                IsReadOnly = true, AcceptsReturn = true, MinHeight = 100, MaxHeight = 220,
                                TextWrapping = TextWrapping.Wrap,
                                [!TextBox.TextProperty] = new Binding(nameof(ProbeViewModel.Output))
                            }
                        }
                    }
                }
            }
        };
    }
}
