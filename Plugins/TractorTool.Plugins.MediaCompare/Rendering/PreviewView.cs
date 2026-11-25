namespace TractorTool.Plugins.MediaCompare.Rendering;

internal enum PreviewScale { Fit, Fill, ActualPixels }

/// <summary>The same normalized image center and zoom are applied to both sources.</summary>
internal readonly record struct PreviewView(double CenterX, double CenterY, double Zoom, PreviewScale Scale)
{
    public static PreviewView Fit => new(.5, .5, 1, PreviewScale.Fit);
    public static PreviewView Fill => new(.5, .5, 1, PreviewScale.Fill);
    public static PreviewView ActualPixels => new(.5, .5, 1, PreviewScale.ActualPixels);

    public (double Width, double Height) ImageSize(int width, int height, double viewportWidth, double viewportHeight)
    {
        var scale = Scale switch
        {
            PreviewScale.ActualPixels => 1,
            PreviewScale.Fill => Math.Max(viewportWidth / width, viewportHeight / height),
            _ => Math.Min(viewportWidth / width, viewportHeight / height)
        };
        return (width * scale * Zoom, height * scale * Zoom);
    }

    public PreviewView ZoomAt(int width, int height, double viewportWidth, double viewportHeight,
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

    public PreviewView Pan(int width, int height, double viewportWidth, double viewportHeight, double dx, double dy)
    {
        var size = ImageSize(width, height, viewportWidth, viewportHeight);
        return this with
        {
            CenterX = Math.Clamp(CenterX - dx / size.Width, 0, 1),
            CenterY = Math.Clamp(CenterY - dy / size.Height, 0, 1)
        };
    }
}
