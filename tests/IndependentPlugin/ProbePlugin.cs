using Avalonia.Controls;
using Avalonia.Layout;
using TractorTool.Contracts;
using TractorTool.UI;
using TractorTool.UI.Controls;

namespace IndependentPlugin;

public record ProbeSettings : IVersionedSettings
{
    public int SchemaVersion { get; init; } = 1;
    public string Note { get; init; } = "来自独立 DLL";
}

public class ProbePlugin : IPlugin, IPluginUiModule, IPluginBackgroundService, IPluginTrayModule
{
    protected PluginContext Context = null!;
    public ProbePlugin()
    {
        var marker = Environment.GetEnvironmentVariable("TRACTORTOOL_TEST_ENTRY_MARKER");
        if (marker is not null) File.AppendAllText(marker, "constructor\n");
    }
    public virtual ValueTask InitializeAsync(PluginContext context, CancellationToken cancellationToken)
    {
        Context = context;
        Context.Log.Write(context.PluginId, FixtureDependency.Message.Value);
        return ValueTask.CompletedTask;
    }
    public virtual void RegisterPages(IPluginPageRegistry registry)
    {
        registry.AddNavigationPage("home", "独立插件", CreatePage, new PluginPageOptions(ToolIconKind.Tools, "独立加载的示例插件，使用宿主共享的图标、控件和主题。") { IconPath = "Assets/icon.png" });
        registry.AddSettingsPage("settings", "独立插件设置", CreateSettings, new PluginPageOptions(ToolIconKind.Settings, "保存一条备注，检查插件自己的设置持久化。"));
    }
    public virtual void RegisterTrayMenu(IPluginTrayMenu menu)
    {
        var enabled = true;
        var count = 0;
        IPluginTrayItem toggle = null!, counter = null!;
        counter = menu.Add("count", new("菜单计数：0"), token =>
        {
            token.ThrowIfCancellationRequested();
            counter.Update(new($"菜单计数：{++count}"));
            Context.Log.Write(Context.PluginId, $"独立插件托盘计数：{count}");
            return Task.CompletedTask;
        });
        toggle = menu.Add("enabled", new("允许计数", IsChecked: true), token =>
        {
            token.ThrowIfCancellationRequested();
            enabled = !enabled;
            toggle.Update(new("允许计数", IsChecked: enabled));
            counter.Update(new($"菜单计数：{count}", IsEnabled: enabled));
            return Task.CompletedTask;
        });
    }
    private Control CreatePage()
    {
        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(new ToolIcon { Kind = ToolIconKind.Tools, FontSize = 40, HorizontalAlignment = HorizontalAlignment.Left });
        panel.Children.Add(new TextBlock { Text = "独立插件已运行", Classes = { "sectionTitle" } });
        panel.Children.Add(new TextBlock { Text = FixtureDependency.Message.Value });
        panel.Children.Add(new TextBlock { Text = "备注：" + Context.Settings.Load<ProbeSettings>("settings").Note });
        panel.Children.Add(new TextBlock { Text = "常驻服务随插件启停，切换页面或隐藏窗口不影响。" });
        panel.Children.Add(new TextBlock { Text = "这是宿主发布后单独编译的机制测试夹具。" });
        panel.Children.Add(new TextBlock { Text = "托盘右键 → 独立测试插件：点击菜单计数，或取消“允许计数”验证禁用；切换页面后计数仍保留。" });
        return new Border { Classes = { "card" }, MaxWidth = 960, VerticalAlignment = VerticalAlignment.Top, Child = panel };
    }
    private Control CreateSettings()
    {
        var edit = new TextBox { Text = Context.Settings.Load<ProbeSettings>("settings").Note, PlaceholderText = "测试备注", Width = 230 };
        var status = new TextBlock();
        var save = new Button { Content = "保存插件设置", Classes = { "accent" } };
        save.Click += (_, _) =>
        {
            try { Context.Settings.Save("settings", new ProbeSettings { Note = edit.Text ?? "" }); status.Text = "插件设置已保存"; }
            catch (Exception ex) { status.Text = "保存失败：" + ex.Message; Context.Log.Write(Context.PluginId, status.Text, ex); }
        };
        return new StackPanel { Spacing = 16, MaxWidth = 960, Children = {
            new SettingRow { Header = "测试备注", Description = "随插件保存，重启后仍可读取。", Icon = ToolIconKind.Info, Content = edit },
            save, status } };
    }
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = stoppingToken.Register(() => stopped.TrySetResult());
        await stopped.Task;
    }
    public ValueTask StopAsync(CancellationToken cancellationToken) { Context?.Log.Write("Test", "StopAsync"); return ValueTask.CompletedTask; }
    public ValueTask DisposeAsync() { Context?.Log.Write("Test", "DisposeAsync"); return ValueTask.CompletedTask; }
}

public sealed class ThrowingPlugin : ProbePlugin
{
    public override ValueTask InitializeAsync(PluginContext context, CancellationToken token) => throw new InvalidOperationException("fixture initialization failed");
}
public sealed class BadRegistrationPlugin : ProbePlugin
{
    public override void RegisterPages(IPluginPageRegistry registry)
    {
        registry.AddNavigationPage("home", "不应提交", () => new TextBlock());
        throw new InvalidOperationException("fixture registration failed");
    }
}
public sealed class BrokenPagePlugin : ProbePlugin
{
    public override void RegisterPages(IPluginPageRegistry registry) => registry.AddNavigationPage("broken", "故障页面", () => throw new InvalidOperationException("fixture page failed"));
}
public sealed class BadTrayRegistrationPlugin : ProbePlugin
{
    public override void RegisterTrayMenu(IPluginTrayMenu menu)
    {
        base.RegisterTrayMenu(menu);
        throw new InvalidOperationException("fixture tray registration failed");
    }
}
