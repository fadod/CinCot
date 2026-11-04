using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Sikirincot.Native;
using Sikirincot.Overlay;

namespace Sikirincot.App;

/// <summary>Draws the tray and window icon at runtime: a blue tile with crop corners and a redaction bar.</summary>
internal static class AppIcon
{
    public static Icon Create(int size = 32)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float s = size / 32f;
            using (var tile = Drawing.RoundRect(new RectangleF(1 * s, 1 * s, 30 * s, 30 * s), 7 * s))
            using (var blue = new SolidBrush(OverlayForm.Accent))
                g.FillPath(blue, tile);
            using var pen = new Pen(Color.White, 3 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, new[] { new PointF(8 * s, 14 * s), new PointF(8 * s, 8 * s), new PointF(14 * s, 8 * s) });
            g.DrawLines(pen, new[] { new PointF(18 * s, 24 * s), new PointF(24 * s, 24 * s), new PointF(24 * s, 18 * s) });
            using var bar = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
            g.FillRectangle(bar, 11 * s, 14.5f * s, 10 * s, 3 * s);
        }
        IntPtr handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            Win32.DestroyIcon(handle);
        }
    }
}
