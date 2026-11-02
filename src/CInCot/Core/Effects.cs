using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace CInCot.Core;

/// <summary>
/// Pixel effects. Blur and pixelate both go through a heavy downsample, so the original pixels are
/// destroyed rather than merely smoothed; pixelate also jitters each block so it cannot be matched
/// back against re-pixelated candidate text.
/// </summary>
internal static class Effects
{
    /// <summary>Copy of <paramref name="source"/> with every channel multiplied by <paramref name="keep"/> (0..1).</summary>
    public static Bitmap Dim(Bitmap source, float keep)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppRgb);
        Dim(source, result, keep);
        return result;
    }

    /// <summary>Writes the dimmed copy into <paramref name="result"/>, which must be the same size.</summary>
    public static unsafe void Dim(Bitmap source, Bitmap result, float keep)
    {
        uint k = (uint)(Math.Clamp(keep, 0f, 1f) * 256);
        var rect = new Rectangle(Point.Empty, source.Size);
        var src = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        var dst = result.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            IntPtr s0 = src.Scan0, d0 = dst.Scan0;
            int sStride = src.Stride, dStride = dst.Stride, width = source.Width;
            Parallel.For(0, source.Height, y =>
            {
                uint* s = (uint*)((byte*)s0 + (long)y * sStride);
                uint* d = (uint*)((byte*)d0 + (long)y * dStride);
                for (int x = 0; x < width; x++)
                {
                    uint p = s[x];
                    uint r = ((p >> 16) & 0xFF) * k >> 8;
                    uint g = ((p >> 8) & 0xFF) * k >> 8;
                    uint b = (p & 0xFF) * k >> 8;
                    d[x] = 0xFF000000u | (r << 16) | (g << 8) | b;
                }
            });
        }
        finally
        {
            source.UnlockBits(src);
            result.UnlockBits(dst);
        }
    }

    public static Bitmap? Pixelate(Bitmap source, Rectangle area, int block)
    {
        area = Rectangle.Intersect(area, new Rectangle(Point.Empty, source.Size));
        if (area.Width <= 0 || area.Height <= 0) return null;
        block = Math.Max(2, block);
        int cols = (area.Width + block - 1) / block, rows = (area.Height + block - 1) / block;
        using var small = Resample(source, area, new Size(cols, rows), InterpolationMode.HighQualityBicubic);
        Jitter(small, 2);
        var result = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(result);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImage(small, new Rectangle(0, 0, cols * block, rows * block), 0, 0, cols, rows, GraphicsUnit.Pixel);
        return result;
    }

    public static Bitmap? Blur(Bitmap source, Rectangle area, int strength)
    {
        area = Rectangle.Intersect(area, new Rectangle(Point.Empty, source.Size));
        if (area.Width <= 0 || area.Height <= 0) return null;
        int factor = Math.Max(2, strength);
        var small = new Size(Math.Max(1, area.Width / factor), Math.Max(1, area.Height / factor));
        using var down = Resample(source, area, small, InterpolationMode.HighQualityBicubic);
        Jitter(down, 1);
        return Resample(down, new Rectangle(Point.Empty, small), area.Size, InterpolationMode.HighQualityBicubic);
    }

    private static Bitmap Resample(Bitmap source, Rectangle sourceRect, Size size, InterpolationMode mode)
    {
        var result = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(result);
        g.InterpolationMode = mode;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingMode = CompositingMode.SourceCopy;
        using var attributes = new ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY);
        g.DrawImage(source, new Rectangle(Point.Empty, size),
            sourceRect.X, sourceRect.Y, sourceRect.Width, sourceRect.Height, GraphicsUnit.Pixel, attributes);
        return result;
    }

    private static unsafe void Jitter(Bitmap bitmap, int amount)
    {
        var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
        try
        {
            var rng = Random.Shared;
            for (int y = 0; y < bitmap.Height; y++)
            {
                byte* row = (byte*)data.Scan0 + (long)y * data.Stride;
                for (int x = 0; x < bitmap.Width * 4; x += 4)
                    for (int c = 0; c < 3; c++)
                        row[x + c] = (byte)Math.Clamp(row[x + c] + rng.Next(-amount, amount + 1), 0, 255);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
