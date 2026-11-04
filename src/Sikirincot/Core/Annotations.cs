using System.Drawing.Drawing2D;

namespace Sikirincot.Core;

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

public enum LineStyle { Solid, Dashed, Dotted, DashDot }

public enum ArrowHeads { End, Both, None }

/// <summary>Colour, thickness and dash pattern shared by the outline shapes.</summary>
internal readonly record struct Stroke(Color Color, float Width, LineStyle Style)
{
    public Pen CreatePen()
    {
        var pen = new Pen(Color, Width) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round, DashCap = DashCap.Round };
        // Patterns are in multiples of the pen width; a near-zero dash with round caps draws a dot.
        switch (Style)
        {
            case LineStyle.Dashed: pen.DashPattern = new[] { 3f, 2f }; break;
            case LineStyle.Dotted: pen.DashPattern = new[] { 0.01f, 2f }; break;
            case LineStyle.DashDot: pen.DashPattern = new[] { 3f, 2f, 0.01f, 2f }; break;
        }
        return pen;
    }
}

public enum ShapeKind { Rectangle, RoundedRectangle, Ellipse, Triangle, Diamond, Star }

/// <summary>An annotation whose colour, thickness and line style can be changed after it is drawn.</summary>
internal interface IRestylable
{
    Annotation Restyle(Stroke stroke, ArrowHeads heads, ShapeKind shape);
}

/// <summary>An outline shape fitted to a dragged rectangle: rectangle, rounded rectangle, ellipse, triangle, diamond or star.</summary>
internal sealed class RectAnnotation(Rectangle rect, Stroke stroke, ShapeKind shape = ShapeKind.Rectangle) : Annotation, IRestylable
{
    public RectAnnotation(Rectangle rect, Color color, float width) : this(rect, new Stroke(color, width, LineStyle.Solid)) { }

    public Stroke Stroke => stroke;
    public ShapeKind Shape => shape;
    public override Rectangle Bounds => Rectangle.Inflate(rect, (int)stroke.Width * 2 + 1, (int)stroke.Width * 2 + 1);

    public override void Render(Graphics g)
    {
        using var pen = stroke.CreatePen();
        switch (shape)
        {
            case ShapeKind.Rectangle:
                g.DrawRectangle(pen, rect);
                break;
            case ShapeKind.RoundedRectangle:
            {
                float radius = Math.Min(Math.Max(stroke.Width * 3f, 10f), Math.Min(rect.Width, rect.Height) / 2f);
                using var path = Overlay.Drawing.RoundRect(rect, radius);
                g.DrawPath(pen, path);
                break;
            }
            case ShapeKind.Ellipse:
                g.DrawEllipse(pen, rect);
                break;
            default:
                if (rect.Width > 0 && rect.Height > 0) g.DrawPolygon(pen, Outline(shape, rect));
                break;
        }
    }

    /// <summary>The polygon for the pointed shapes, fitted to <paramref name="r"/>.</summary>
    internal static PointF[] Outline(ShapeKind shape, RectangleF r)
    {
        float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        switch (shape)
        {
            case ShapeKind.Triangle:
                return new[] { new PointF(cx, r.Top), new PointF(r.Right, r.Bottom), new PointF(r.Left, r.Bottom) };
            case ShapeKind.Diamond:
                return new[] { new PointF(cx, r.Top), new PointF(r.Right, cy), new PointF(cx, r.Bottom), new PointF(r.Left, cy) };
            default:
            {
                // Five-pointed star, point up. A unit star spans x ±0.951 and y -1..0.809; map that onto the box.
                var points = new PointF[10];
                for (int i = 0; i < 10; i++)
                {
                    double angle = -Math.PI / 2 + i * Math.PI / 5;
                    double k = i % 2 == 0 ? 1 : 0.42;
                    double nx = Math.Cos(angle) * k, ny = Math.Sin(angle) * k;
                    points[i] = new PointF(cx + (float)(nx / 0.951) * r.Width / 2, r.Top + (float)((ny + 1) / 1.809) * r.Height);
                }
                return points;
            }
        }
    }

    public Annotation Restyle(Stroke newStroke, ArrowHeads heads, ShapeKind newShape) => new RectAnnotation(rect, newStroke, newShape);
}

/// <summary>Highlighter ink: the chosen colour, see-through so the text underneath stays readable.</summary>
internal static class Highlighter
{
    public const int Alpha = 110;

    public static Color Ink(Color color) => Color.FromArgb(Alpha, color.R, color.G, color.B);
}

/// <summary>Highlighter bands fitted exactly to recognised words, one per run of words on a line.</summary>
internal sealed class TextHighlightAnnotation(IReadOnlyList<Rectangle> bands, Color color) : Annotation, IRestylable
{
    public IReadOnlyList<Rectangle> Bands => bands;
    public Color Color => color;

    public override Rectangle Bounds => bands.Aggregate(Rectangle.Empty, (a, b) => a.IsEmpty ? b : Rectangle.Union(a, b));

    public override void Render(Graphics g)
    {
        using var brush = new SolidBrush(Highlighter.Ink(color));
        foreach (var band in bands) g.FillRectangle(brush, band);
    }

    // Bands follow the words, so only the colour changes.
    public Annotation Restyle(Stroke stroke, ArrowHeads heads, ShapeKind shape) => new TextHighlightAnnotation(bands, stroke.Color);
}

internal sealed class ArrowAnnotation(Point from, Point to, Stroke stroke, ArrowHeads heads = ArrowHeads.End) : Annotation, IRestylable
{
    public ArrowAnnotation(Point from, Point to, Color color, float width) : this(from, to, new Stroke(color, width, LineStyle.Solid)) { }

    public Stroke Stroke => stroke;
    public ArrowHeads Heads => heads;
    private float HeadLength => Math.Max(stroke.Width * 4.5f, 14f);

    public override Rectangle Bounds => BoundsOf(new[] { from, to }, HeadLength + stroke.Width);

    public override void Render(Graphics g)
    {
        float dx = to.X - from.X, dy = to.Y - from.Y;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 1) return;
        float ux = dx / length, uy = dy / length;
        int headCount = heads switch { ArrowHeads.Both => 2, ArrowHeads.End => 1, _ => 0 };
        float head = headCount == 0 ? 0 : Math.Min(HeadLength, length / headCount);

        // The shaft stops inside each head so a dashed line never shows a gap under the tip.
        var start = heads == ArrowHeads.Both ? new PointF(from.X + ux * head * 0.8f, from.Y + uy * head * 0.8f) : new PointF(from.X, from.Y);
        var end = headCount > 0 ? new PointF(to.X - ux * head * 0.8f, to.Y - uy * head * 0.8f) : new PointF(to.X, to.Y);
        using (var pen = stroke.CreatePen())
        {
            if (headCount > 0) pen.EndCap = LineCap.Flat;
            if (heads == ArrowHeads.Both) pen.StartCap = LineCap.Flat;
            g.DrawLine(pen, start, end);
        }

        using var brush = new SolidBrush(stroke.Color);
        if (headCount > 0) g.FillPolygon(brush, Head(to, ux, uy, head));
        if (heads == ArrowHeads.Both) g.FillPolygon(brush, Head(from, -ux, -uy, head));
    }

    private static PointF[] Head(Point tip, float ux, float uy, float head)
    {
        float half = head * 0.45f;
        var basePoint = new PointF(tip.X - ux * head, tip.Y - uy * head);
        return new[]
        {
            new PointF(tip.X, tip.Y),
            new PointF(basePoint.X - uy * half, basePoint.Y + ux * half),
            new PointF(basePoint.X + uy * half, basePoint.Y - ux * half),
        };
    }

    public Annotation Restyle(Stroke newStroke, ArrowHeads newHeads, ShapeKind shape) => new ArrowAnnotation(from, to, newStroke, newHeads);
}

/// <summary>Freehand pen or highlighter stroke.</summary>
internal sealed class StrokeAnnotation(IReadOnlyList<Point> points, Color color, float width, bool highlighter) : Annotation, IRestylable
{
    public bool IsHighlighter => highlighter;
    public Color Color => color;
    private Color InkColor => highlighter ? Highlighter.Ink(color) : color;
    private float InkWidth => highlighter ? Math.Max(width * 4f, 16f) : width;

    public Annotation Restyle(Stroke stroke, ArrowHeads heads, ShapeKind shape) => new StrokeAnnotation(points, stroke.Color, stroke.Width, highlighter);

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

internal sealed class TextAnnotation : Annotation, IRestylable
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

    // Recolours only; the size was chosen while typing.
    public Annotation Restyle(Stroke stroke, ArrowHeads heads, ShapeKind shape) => new TextAnnotation(At, Text, stroke.Color, _size);

    public override void Dispose() => _path.Dispose();
}
