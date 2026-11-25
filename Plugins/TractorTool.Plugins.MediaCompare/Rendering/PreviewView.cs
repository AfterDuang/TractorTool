namespace TractorTool.Plugins.MediaCompare.Rendering;

/// <summary>The same normalized image center and zoom are applied to both sources.</summary>
internal readonly record struct PreviewView(double CenterX, double CenterY, double Zoom)
{
    public static PreviewView Fit => new(.5, .5, 1);

    public (double Width, double Height) ImageSize(double width, double height, double viewportWidth, double viewportHeight)
    {
        var scale = Math.Min(viewportWidth / width, viewportHeight / height);
        return (width * scale * Zoom, height * scale * Zoom);
    }

    public PreviewView ZoomAt(double width, double height, double viewportWidth, double viewportHeight,
        double x, double y, double factor)
    {
        var next = this with { Zoom = Math.Clamp(Zoom * factor, .05, 64) };
        var before = ImageSize(width, height, viewportWidth, viewportHeight);
        var after = next.ImageSize(width, height, viewportWidth, viewportHeight);
        var dx = x - viewportWidth / 2;
        var dy = y - viewportHeight / 2;
        return next with
        {
            CenterX = Math.Clamp(CenterX + dx / before.Width - dx / after.Width, 0, 1),
            CenterY = Math.Clamp(CenterY + dy / before.Height - dy / after.Height, 0, 1)
        };
    }

    public PreviewView Pan(double width, double height, double viewportWidth, double viewportHeight, double dx, double dy)
    {
        var size = ImageSize(width, height, viewportWidth, viewportHeight);
        return this with
        {
            CenterX = Math.Clamp(CenterX - dx / size.Width, 0, 1),
            CenterY = Math.Clamp(CenterY - dy / size.Height, 0, 1)
        };
    }
}
