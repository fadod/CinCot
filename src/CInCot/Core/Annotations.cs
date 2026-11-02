using System.Drawing.Drawing2D;

namespace CInCot.Core;

/// <summary>An immutable edit drawn over the capture. Coordinates are frame pixels.</summary>
internal abstract class Annotation : IDisposable
{
    public abstract Rectangle Bounds { get; }
    public virtual bool IsRedaction => false;
    public abstract void Render(Graphics g);
    public virtual void Dispose() { }

    protected static Rectangle BoundsOf(IEnumerable<Point> points, float pad)
    {
        int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
        foreach (var p in points)
        {
            l = Math.Min(l, p.X); t = Math.Min(t, p.Y);
            r = Math.Max(r, p.X); b = Math.Max(b, p.Y);
        }
        if (l > r) return Rectangle.Empty;
        int pd = (int)Math.Ceiling(pad);
        return Rectangle.FromLTRB(l - pd, t - pd, r + pd + 1, b + pd + 1);
    }
}

internal enum RedactionKind { Blur, Pixelate, Box }

/// <summary>
/// Blur, pixelate or solid box. The effect is computed once from the frozen source pixels, never from
/// other annotations, and on export it replaces those pixels outright.
/// </summary>
internal sealed class RedactionAnnotation : Annotation
{
    private readonly Bitmap? _effect;

    public RedactionAnnotation(RedactionKind kind, Rectangle rect, Bitmap source, int strength)
    {
        Kind = kind;
        Rect = Rectangle.Intersect(rect, new Rectangle(Point.Empty, source.Size));
        _effect = kind switch
        {
            RedactionKind.Blur => Effects.Blur(source, Rect, strength),
            RedactionKind.Pixelate => Effects.Pixelate(source, Rect, strength),
            _ => null,
        };
    }

    public RedactionKind Kind { get; }
    public Rectangle Rect { get; }
    public override Rectangle Bounds => Rect;
    public override bool IsRedaction => true;

    public override void Render(Graphics g)
    {
        if (Rect.Width <= 0 || Rect.Height <= 0) return;
        if (_effect is null)
        {
            g.FillRectangle(Brushes.Black, Rect);
            return;
        }
        var state = g.Save();
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_effect, Rect, 0, 0, _effect.Width, _effect.Height, GraphicsUnit.Pixel);
        g.Restore(state);
    }

    public override void Dispose() => _effect?.Dispose();
}

internal sealed class RectAnnotation(Rectangle rect, Color color, float width) : Annotation
{
    public override Rectangle Bounds => Rectangle.Inflate(rect, (int)width + 1, (int)width + 1);

    public override void Render(Graphics g)
    {
        using var pen = new Pen(color, width) { LineJoin = LineJoin.Round };
        g.DrawRectangle(pen, rect);
    }
}

internal sealed class ArrowAnnotation(Point from, Point to, Color color, float width) : Annotation
{
    private float HeadLength => Math.Max(width * 4.5f, 14f);

    public override Rectangle Bounds => BoundsOf(new[] { from, to }, HeadLength + width);

    public override void Render(Graphics g)
    {
        float dx = to.X - from.X, dy = to.Y - from.Y;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 1) return;
        float ux = dx / length, uy = dy / length;
        float head = Math.Min(HeadLength, length);
        float half = head * 0.45f;
        var basePoint = new PointF(to.X - ux * head, to.Y - uy * head);
        var shaftEnd = new PointF(to.X - ux * head * 0.8f, to.Y - uy * head * 0.8f);
        using (var pen = new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Flat })
            g.DrawLine(pen, from, shaftEnd);
        using var brush = new SolidBrush(color);
        g.FillPolygon(brush, new[]
        {
            new PointF(to.X, to.Y),
            new PointF(basePoint.X - uy * half, basePoint.Y + ux * half),
            new PointF(basePoint.X + uy * half, basePoint.Y - ux * half),
        });
    }
}

/// <summary>Freehand pen or highlighter stroke.</summary>
internal sealed class StrokeAnnotation(IReadOnlyList<Point> points, Color color, float width, bool highlighter) : Annotation
{
    private Color InkColor => highlighter ? Color.FromArgb(110, 255, 230, 0) : color;
    private float InkWidth => highlighter ? Math.Max(width * 4f, 16f) : width;

    public override Rectangle Bounds => BoundsOf(points, InkWidth);

    public override void Render(Graphics g)
    {
        if (points.Count == 0) return;
        if (points.Count == 1)
        {
            using var dot = new SolidBrush(InkColor);
            float r = InkWidth / 2;
            g.FillEllipse(dot, points[0].X - r, points[0].Y - r, InkWidth, InkWidth);
            return;
        }
        var cap = highlighter ? LineCap.Square : LineCap.Round;
        using var pen = new Pen(InkColor, InkWidth) { StartCap = cap, EndCap = cap, LineJoin = LineJoin.Round };
        g.DrawLines(pen, points.ToArray());
    }
}

internal sealed class TextAnnotation : Annotation
{
    private static readonly FontFamily Family = FontFamily.Families.FirstOrDefault(f => f.Name == "Segoe UI") ?? FontFamily.GenericSansSerif;
    private readonly GraphicsPath _path = new();
    private readonly Color _color;
    private readonly float _size;

    public TextAnnotation(Point at, string text, Color color, float size)
    {
        At = at;
        Text = text;
        _color = color;
        _size = size;
        if (text.Length > 0)
            _path.AddString(text, Family, (int)FontStyle.Bold, size, at, StringFormat.GenericDefault);
    }

    public Point At { get; }
    public string Text { get; }

    public override Rectangle Bounds
    {
        get
        {
            if (Text.Length == 0) return new Rectangle(At.X, At.Y, 2, (int)(_size * 1.3f));
            var b = Rectangle.Ceiling(_path.GetBounds());
            return Rectangle.Inflate(Rectangle.Union(b, new Rectangle(At, new Size(1, (int)(_size * 1.3f)))), (int)(_size / 6) + 2, (int)(_size / 6) + 2);
        }
    }

    public override void Render(Graphics g)
    {
        if (Text.Length == 0) return;
        float luminance = (0.299f * _color.R + 0.587f * _color.G + 0.114f * _color.B) / 255f;
        var outline = luminance < 0.35f ? Color.FromArgb(220, 255, 255, 255) : Color.FromArgb(180, 0, 0, 0);
        using (var pen = new Pen(outline, Math.Max(2f, _size / 7f)) { LineJoin = LineJoin.Round })
            g.DrawPath(pen, _path);
        using var brush = new SolidBrush(_color);
        g.FillPath(brush, _path);
    }

    public override void Dispose() => _path.Dispose();
}
