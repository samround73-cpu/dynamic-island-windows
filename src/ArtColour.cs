using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland;

/// <summary>Picks the main colour out of album artwork, for the Media tab's glow.</summary>
public static class ArtColour
{
    /// <summary>
    /// The artwork's most noticeable colour, brightened enough to read on black.
    /// Returns null for missing or black-and-white artwork (the caller falls back to the accent colour).
    /// </summary>
    public static Color? Dominant(ImageSource? image)
    {
        if (image is not BitmapSource source || source.PixelWidth < 2 || source.PixelHeight < 2) return null;
        try
        {
            // Shrink to a small thumbnail first: plenty to find the main colour, and quick.
            var small = new TransformedBitmap(source, new ScaleTransform(32.0 / source.PixelWidth, 32.0 / source.PixelHeight));
            var pixels32 = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int width = pixels32.PixelWidth, height = pixels32.PixelHeight;
            var pixels = new byte[width * height * 4];
            pixels32.CopyPixels(pixels, width * 4, 0);

            // Group pixels by hue, giving colourful, bright pixels far more say than greys and shadows.
            const int Buckets = 24;
            var weight = new double[Buckets];
            var red = new double[Buckets];
            var green = new double[Buckets];
            var blue = new double[Buckets];
            double total = 0;

            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 3] < 128) continue;
                double b = pixels[i] / 255.0, g = pixels[i + 1] / 255.0, r = pixels[i + 2] / 255.0;
                var (hue, saturation, value) = ToHsv(r, g, b);
                if (value < 0.15) continue;
                double w = saturation * saturation * value;
                int bucket = Math.Min(Buckets - 1, (int)(hue / (360.0 / Buckets)));
                weight[bucket] += w;
                red[bucket] += r * w;
                green[bucket] += g * w;
                blue[bucket] += b * w;
                total += w;
            }

            // Mostly grey artwork: no real colour to pick.
            if (total < width * height * 0.02) return null;

            // The strongest hue, together with its two neighbours so similar shades count together.
            int best = 0;
            double bestScore = -1;
            for (int i = 0; i < Buckets; i++)
            {
                double score = weight[i] + 0.5 * (weight[(i + 1) % Buckets] + weight[(i + Buckets - 1) % Buckets]);
                if (score > bestScore) { bestScore = score; best = i; }
            }
            if (weight[best] <= 0) return null;

            double avgR = red[best] / weight[best], avgG = green[best] / weight[best], avgB = blue[best] / weight[best];
            var (h, s, v) = ToHsv(avgR, avgG, avgB);
            return FromHsv(h, Math.Clamp(s, 0.45, 0.85), Math.Max(v, 0.85));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A very dark shade of a colour, for the glyph on a tinted play button.</summary>
    public static Color Darken(Color color)
    {
        var (h, s, _) = ToHsv(color.R / 255.0, color.G / 255.0, color.B / 255.0);
        return FromHsv(h, Math.Min(1, s + 0.1), 0.16);
    }

    public static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static (double Hue, double Saturation, double Value) ToHsv(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        double hue = 0;
        if (delta > 0)
        {
            if (max == r) hue = 60 * (((g - b) / delta) % 6);
            else if (max == g) hue = 60 * ((b - r) / delta + 2);
            else hue = 60 * ((r - g) / delta + 4);
        }
        if (hue < 0) hue += 360;
        return (hue, max <= 0 ? 0 : delta / max, max);
    }

    private static Color FromHsv(double hue, double saturation, double value)
    {
        double c = value * saturation;
        double x = c * (1 - Math.Abs((hue / 60) % 2 - 1));
        double m = value - c;
        var (r, g, b) = (hue % 360) switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
