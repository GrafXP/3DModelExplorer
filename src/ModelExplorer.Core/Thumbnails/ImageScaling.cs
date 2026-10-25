namespace ModelExplorer.Core.Thumbnails;

public static class ImageScaling
{
    /// <summary>
    /// Shrinks an image to fit a <paramref name="size"/> square by averaging every
    /// source pixel it covers. Thumbnails are shown at the size they are stored, one
    /// texel per screen pixel, so they stay sharp without mip levels; a plain
    /// bilinear minification would alias the silhouettes.
    /// </summary>
    /// <remarks>
    /// Colours are weighted by alpha while averaged, so the transparent background
    /// does not bleed a dark fringe into the model's edge.
    /// </remarks>
    public static RgbaImage Fit(RgbaImage source, int size)
    {
        if (source.Width <= size && source.Height <= size)
        {
            return source;
        }

        var scale = Math.Min((double)size / source.Width, (double)size / source.Height);
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var output = new byte[width * height * 4];
        var pixels = source.Pixels;

        for (var y = 0; y < height; y++)
        {
            var y0 = y * source.Height / (double)height;
            var y1 = (y + 1) * source.Height / (double)height;
            for (var x = 0; x < width; x++)
            {
                var x0 = x * source.Width / (double)width;
                var x1 = (x + 1) * source.Width / (double)width;
                double r = 0, g = 0, b = 0, a = 0, area = 0;
                for (var sy = (int)y0; sy < Math.Min(source.Height, (int)Math.Ceiling(y1)); sy++)
                {
                    var wy = Math.Min(y1, sy + 1) - Math.Max(y0, sy);
                    for (var sx = (int)x0; sx < Math.Min(source.Width, (int)Math.Ceiling(x1)); sx++)
                    {
                        var weight = wy * (Math.Min(x1, sx + 1) - Math.Max(x0, sx));
                        var i = ((sy * source.Width) + sx) * 4;
                        var alpha = pixels[i + 3] * weight;
                        r += pixels[i] * alpha;
                        g += pixels[i + 1] * alpha;
                        b += pixels[i + 2] * alpha;
                        a += alpha;
                        area += weight;
                    }
                }

                var o = ((y * width) + x) * 4;
                if (a > 0)
                {
                    output[o] = (byte)Math.Round(r / a);
                    output[o + 1] = (byte)Math.Round(g / a);
                    output[o + 2] = (byte)Math.Round(b / a);
                    output[o + 3] = (byte)Math.Round(a / area);
                }
            }
        }

        return new RgbaImage(width, height, output);
    }
}
