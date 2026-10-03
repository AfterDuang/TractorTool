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

public class ProbePlugin : IPlugin, IPluginUiModule, IPluginBackgroundService
{
    protected PluginContext Context = null!;
    private int _ticks;
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
    private Control CreatePage()
    {
        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(new ToolIcon { Kind = ToolIconKind.Tools, FontSize = 40, HorizontalAlignment = HorizontalAlignment.Left });
        panel.Children.Add(new TextBlock { Text = "独立插件已运行", Classes = { "sectionTitle" } });
        panel.Children.Add(new TextBlock { Text = FixtureDependency.Message.Value });
        panel.Children.Add(new TextBlock { Text = "备注：" + Context.Settings.Load<ProbeSettings>("settings").Note });
        panel.Children.Add(new TextBlock { Text = $"常驻服务计数：{Volatile.Read(ref _ticks)}（重新进入页面刷新）" });
        panel.Children.Add(new TextBlock { Text = "这是宿主发布后单独编译的机制测试夹具。" });
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
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var ticks = Interlocked.Increment(ref _ticks);
            if (ticks % 5 == 0) Context.Log.Write(Context.PluginId, $"background-tick:{ticks}");
        }
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
