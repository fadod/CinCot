using System.Drawing.Drawing2D;

namespace CInCot.Overlay;

internal static class Drawing
{
    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRoundRect(Graphics g, Brush brush, Rectangle r, float radius)
    {
        using var path = RoundRect(r, radius);
        g.FillPath(brush, path);
    }

    /// <summary>Union that treats empty rectangles as "nothing" instead of the origin.</summary>
    public static Rectangle Union(Rectangle a, Rectangle b)
    {
        if (a.Width <= 0 || a.Height <= 0) return b;
        if (b.Width <= 0 || b.Height <= 0) return a;
        return Rectangle.Union(a, b);
    }

    public static Rectangle Normalize(Point a, Point b) =>
        Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    public static Point Clamp(Point p, Rectangle r) =>
        new(Math.Clamp(p.X, r.Left, r.Right - 1), Math.Clamp(p.Y, r.Top, r.Bottom - 1));
}
