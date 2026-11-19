using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using TractorTool.Plugins.FolderHistory.ViewModels;
using TractorTool.Plugins.FolderHistory.Views;

internal static partial class InteractionChecks
{
    internal static void CheckTreeBadges(string? imageDirectory)
    {
        var app = CreateHeadlessApplication();
        var view = new FolderHistoryView();
        var tree = view.FindControl<ListBox>("FileTree")!;
        // Mount the production tree pane alone so the fixture needs no history library or engine.
        var pane = (Grid)view.FindControl<Grid>("TreeArea")!.Parent!;
        ((Grid)pane.Parent!).Children.Remove(pane);
        view.Content = pane;
        TreeItem[] samples = [
            FileRow("说明.md", TreePresence.Recorded),
            FileRow("草稿.txt", TreePresence.Untracked),
            FileRow("设计.md", TreePresence.Modified),
            FileRow("计划.md", TreePresence.OlderRevision) with { CurrentVersionNumber = 10 },
            FileRow("旧稿.txt", TreePresence.Historical),
            new("缓存", "缓存", 0, true, false, TreePresence.Excluded),
            FileRow("日志.txt", TreePresence.Unavailable)
        ];
        tree.ItemsSource = samples;
        var window = new Window { Width = 360, Height = 288, Content = view };
        window.Bind(Window.BackgroundProperty, window.GetResourceObservable("AppCardBrush"));
        window.Bind(Window.ForegroundProperty, window.GetResourceObservable("AppTextBrush"));
        try
        {
            window.Show();
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            foreach (var width in new[] { 360, 190 })
            {
                app.RequestedThemeVariant = theme;
                window.Width = width;
                Until(() => window.ClientSize.Width == width && tree.Classes.Contains("compact") == (width == 190) &&
                    tree.ContainerFromItem(samples[^1]) is ListBoxItem last && IsPresented(last), "tree layout did not settle");
                var shapes = new HashSet<string>();
                foreach (var item in samples)
                {
                    var row = (ListBoxItem)tree.ContainerFromItem(item)!;
                    var children = row.GetVisualDescendants().OfType<Control>().ToArray();
                    Require(children.Any(c => Equals(ToolTip.GetTip(c), item.StatusDescription) &&
                        AutomationProperties.GetName(c) == item.StatusDescription), $"missing accessible status: {item.Name}");
                    var badge = children.OfType<Border>().Single(b => b.Classes.Contains("treeStatusBadge"));
                    Require(IsPresented(badge) == (item.Presence != TreePresence.Recorded), $"wrong badge visibility: {item.Name}");
                    if (!IsPresented(badge)) continue;
                    Require(!badge.IsHitTestVisible, $"badge intercepted tree input: {item.Name}");
                    var icon = badge.GetVisualDescendants().OfType<PathIcon>().Single();
                    Require(IsPresented(icon) && icon.Foreground is ISolidColorBrush { Color.A: > 0 } &&
                        icon.Data is { } geometry && shapes.Add(ShapeMask(geometry)), $"missing or indistinct badge: {item.Name}");
                    var slot = (Control)badge.Parent!;
                    var corner = badge.TranslatePoint(new Point(badge.Bounds.Width, badge.Bounds.Height), slot)!.Value;
                    Require(Math.Abs(corner.X - slot.Bounds.Width) <= 1 && Math.Abs(corner.Y - slot.Bounds.Height) <= 1,
                        $"badge is not in the lower-right corner: {item.Name}");
                    var label = children.OfType<TextBlock>().Single(t => t.Classes.Contains("treeStatusText"));
                    Require(label.Text == item.StatusText && IsPresented(label) == (width == 360 || item.Presence == TreePresence.OlderRevision),
                        $"wrong status label at width {width}: {item.Name}");
                }
                if (imageDirectory is null) continue;
                Directory.CreateDirectory(imageDirectory);
                using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("missing tree frame");
                frame.Save(Path.Combine(imageDirectory, $"file-tree-{theme.Key}-{width}.png"), PngBitmapEncoderOptions.Default);
            }
        }
        finally { window.Close(); }

        static TreeItem FileRow(string name, TreePresence status) => new(name, name, 0, false, false, status);
    }

    private static string ShapeMask(Geometry geometry)
    {
        var bounds = geometry.Bounds;
        var pixels = new char[16 * 16];
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = geometry.FillContains(new Point(bounds.X + (i % 16 + 0.5) * bounds.Width / 16,
                bounds.Y + (i / 16 + 0.5) * bounds.Height / 16)) ? '#' : '.';
        Require(pixels.Contains('#'), "empty badge geometry");
        return new string(pixels);
    }
}
