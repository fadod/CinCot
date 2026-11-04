using System.Drawing.Drawing2D;

namespace Sikirincot.Overlay;

internal enum Glyph
{
    SelectText, Blur, Pixelate, Redact, Rect, Arrow, Text, Pen, Highlight, Crop,
    Undo, Redo, CopyText, Copy, Save, Close, Minus, Plus,
    HeadEnd, HeadBoth, HeadNone, Square, Rounded, Chevron,
    Ellipse, Triangle, Diamond, Star, Freehand, SnapText,
}

/// <summary>
/// The toolbar's icon set, drawn as vectors on a 24-unit grid so it is crisp at every DPI and needs no
/// icon font or image files.
/// </summary>
internal static class Icons
{
    private static readonly Color HighlighterYellow = Color.FromArgb(255, 212, 0);

    public static void Draw(Graphics g, Glyph glyph, Rectangle box, Color ink, float size, Color? marker = null)
    {
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TranslateTransform(box.X + (box.Width - size) / 2f, box.Y + (box.Height - size) / 2f);
        g.ScaleTransform(size / 24f, size / 24f);
        using var pen = new Pen(ink, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(ink);
        // Highlighter glyphs show the chosen highlight colour; a dark colour gets a light rim so it stays visible.
        var mark = marker ?? HighlighterYellow;
        bool darkMark = (0.299f * mark.R + 0.587f * mark.G + 0.114f * mark.B) / 255f < 0.3f;
        using var rim = new Pen(Color.FromArgb(130, ink), 1f);

        switch (glyph)
        {
            case Glyph.SelectText:
                using (var selection = new SolidBrush(Color.FromArgb(110, OverlayForm.Accent)))
                    g.FillRectangle(selection, 2.5f, 9f, 15f, 6f);
                g.DrawLine(pen, 4, 5, 20, 5);
                g.DrawLine(pen, 4, 12, 16, 12);
                g.DrawLine(pen, 4, 19, 14, 19);
                g.DrawLine(pen, 20, 9, 20, 17);       // caret
                break;

            case Glyph.Blur:
                using (var outer = new SolidBrush(Color.FromArgb(ink.A * 45 / 255, ink)))
                    g.FillEllipse(outer, 2.5f, 2.5f, 19f, 19f);
                using (var middle = new SolidBrush(Color.FromArgb(ink.A * 110 / 255, ink)))
                    g.FillEllipse(middle, 6f, 6f, 12f, 12f);
                g.FillEllipse(brush, 9.5f, 9.5f, 5f, 5f);
                break;

            case Glyph.Pixelate:
            {
                int[] alpha = { 255, 110, 200, 110, 255, 60, 200, 60, 255 };
                for (int i = 0; i < 9; i++)
                {
                    using var cell = new SolidBrush(Color.FromArgb(ink.A * alpha[i] / 255, ink));
                    g.FillRectangle(cell, 3f + i % 3 * 6.2f, 3f + i / 3 * 6.2f, 5.6f, 5.6f);
                }
                break;
            }

            case Glyph.Redact:
                g.DrawLine(pen, 3, 5, 21, 5);
                using (var bar = Drawing.RoundRect(new RectangleF(3f, 9f, 18f, 6.5f), 1.5f))
                    g.FillPath(brush, bar);
                g.DrawLine(pen, 3, 19.5f, 15, 19.5f);
                break;

            case Glyph.Rect:
                using (var r = Drawing.RoundRect(new RectangleF(3.5f, 5.5f, 17f, 13f), 1.5f))
                    g.DrawPath(pen, r);
                break;

            case Glyph.Arrow:
                g.DrawLine(pen, 5, 19, 19, 5);
                g.DrawLines(pen, new[] { new PointF(10.5f, 5), new PointF(19, 5), new PointF(19, 13.5f) });
                break;

            case Glyph.Text:
                g.DrawLine(pen, 5, 5, 19, 5);
                g.DrawLine(pen, 12, 5, 12, 19.5f);
                g.DrawLine(pen, 9, 19.5f, 15, 19.5f);
                break;

            case Glyph.Pen:
                g.DrawPolygon(pen, new[] { new PointF(15.5f, 4.5f), new PointF(19.5f, 8.5f), new PointF(8.5f, 19.5f), new PointF(4.5f, 19.5f), new PointF(4.5f, 15.5f) });
                g.DrawLine(pen, 13, 7, 17, 11);
                break;

            case Glyph.Highlight:
                g.DrawPolygon(pen, new[] { new PointF(14, 3.5f), new PointF(20.5f, 10), new PointF(13.5f, 17), new PointF(8.5f, 17), new PointF(7, 15.5f), new PointF(7, 10.5f) });
                g.DrawLine(pen, 7, 15.5f, 5, 17.5f);
                using (var yellow = new SolidBrush(mark))
                    g.FillRectangle(yellow, 3f, 19.5f, 18f, 2.6f);
                if (darkMark) g.DrawRectangle(rim, 3f, 19.5f, 18f, 2.6f);
                break;

            case Glyph.Crop:
                g.DrawLines(pen, new[] { new PointF(7, 2.5f), new PointF(7, 17), new PointF(21.5f, 17) });
                g.DrawLines(pen, new[] { new PointF(2.5f, 7), new PointF(17, 7), new PointF(17, 21.5f) });
                break;

            case Glyph.Undo:
                DrawUndo(g, pen);
                break;

            case Glyph.Redo:
                g.TranslateTransform(24, 0);
                g.ScaleTransform(-1, 1);
                DrawUndo(g, pen);
                break;

            case Glyph.CopyText:
            case Glyph.Copy:
                g.DrawLines(pen, new[] { new PointF(8.5f, 6.5f), new PointF(8.5f, 3), new PointF(21, 3), new PointF(21, 15.5f), new PointF(17.5f, 15.5f) });
                using (var front = Drawing.RoundRect(new RectangleF(3, 8.5f, 13.5f, 12.5f), 1.5f))
                    g.DrawPath(pen, front);
                if (glyph == Glyph.CopyText)
                {
                    using var thin = new Pen(ink, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    g.DrawLine(thin, 6, 12.5f, 13.5f, 12.5f);
                    g.DrawLine(thin, 6, 15.5f, 13.5f, 15.5f);
                    g.DrawLine(thin, 6, 18.5f, 10.5f, 18.5f);
                }
                break;

            case Glyph.Save:
                g.DrawLine(pen, 12, 3, 12, 14.5f);
                g.DrawLines(pen, new[] { new PointF(7.5f, 10), new PointF(12, 14.5f), new PointF(16.5f, 10) });
                g.DrawLines(pen, new[] { new PointF(4, 15), new PointF(4, 20.5f), new PointF(20, 20.5f), new PointF(20, 15) });
                break;

            case Glyph.Close:
                g.DrawLine(pen, 6.5f, 6.5f, 17.5f, 17.5f);
                g.DrawLine(pen, 17.5f, 6.5f, 6.5f, 17.5f);
                break;

            case Glyph.Minus:
                g.DrawLine(pen, 6, 12, 18, 12);
                break;

            case Glyph.Plus:
                g.DrawLine(pen, 6, 12, 18, 12);
                g.DrawLine(pen, 12, 6, 12, 18);
                break;

            case Glyph.HeadEnd:
                g.DrawLine(pen, 3, 12, 16, 12);
                g.FillPolygon(brush, new[] { new PointF(21.5f, 12), new PointF(14.5f, 7.5f), new PointF(14.5f, 16.5f) });
                break;

            case Glyph.HeadBoth:
                g.DrawLine(pen, 8, 12, 16, 12);
                g.FillPolygon(brush, new[] { new PointF(21.5f, 12), new PointF(14.5f, 7.5f), new PointF(14.5f, 16.5f) });
                g.FillPolygon(brush, new[] { new PointF(2.5f, 12), new PointF(9.5f, 7.5f), new PointF(9.5f, 16.5f) });
                break;

            case Glyph.HeadNone:
                g.DrawLine(pen, 3, 12, 21, 12);
                break;

            case Glyph.Square:
                g.DrawRectangle(pen, 4.5f, 4.5f, 15f, 15f);
                break;

            case Glyph.Rounded:
                using (var r = Drawing.RoundRect(new RectangleF(4.5f, 4.5f, 15f, 15f), 5f))
                    g.DrawPath(pen, r);
                break;

            case Glyph.Chevron:
                g.DrawLines(pen, new[] { new PointF(7, 9.5f), new PointF(12, 14.5f), new PointF(17, 9.5f) });
                break;

            case Glyph.Ellipse:
                g.DrawEllipse(pen, 3.5f, 5f, 17f, 14f);
                break;

            case Glyph.Triangle:
            case Glyph.Diamond:
            case Glyph.Star:
            {
                var shape = glyph switch { Glyph.Triangle => Core.ShapeKind.Triangle, Glyph.Diamond => Core.ShapeKind.Diamond, _ => Core.ShapeKind.Star };
                var frame = glyph == Glyph.Triangle ? new RectangleF(3.5f, 4.5f, 17f, 15f) : new RectangleF(3.5f, 3.5f, 17f, 17f);
                g.DrawPolygon(pen, Core.RectAnnotation.Outline(shape, frame));
                break;
            }

            case Glyph.Freehand:
                if (darkMark)
                    using (var halo = new Pen(Color.FromArgb(130, ink), 6.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        g.DrawBezier(halo, new PointF(3.5f, 16), new PointF(8, 5), new PointF(14, 21), new PointF(20.5f, 8));
                using (var band = new Pen(Color.FromArgb(220, mark), 4.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawBezier(band, new PointF(3.5f, 16), new PointF(8, 5), new PointF(14, 21), new PointF(20.5f, 8));
                break;

            case Glyph.SnapText:
                using (var band = new SolidBrush(Color.FromArgb(220, mark)))
                {
                    g.FillRectangle(band, 2.5f, 3.5f, 19f, 6.5f);
                    g.FillRectangle(band, 2.5f, 14f, 12f, 6.5f);
                }
                if (darkMark)
                {
                    g.DrawRectangle(rim, 2.5f, 3.5f, 19f, 6.5f);
                    g.DrawRectangle(rim, 2.5f, 14f, 12f, 6.5f);
                }
                using (var text = new Pen(ink, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(text, 4.5f, 6.75f, 19.5f, 6.75f);
                    g.DrawLine(text, 4.5f, 17.25f, 12.5f, 17.25f);
                }
                break;
        }
        g.Restore(state);
    }

    public static Glyph For(Core.ShapeKind shape) => shape switch
    {
        Core.ShapeKind.RoundedRectangle => Glyph.Rounded,
        Core.ShapeKind.Ellipse => Glyph.Ellipse,
        Core.ShapeKind.Triangle => Glyph.Triangle,
        Core.ShapeKind.Diamond => Glyph.Diamond,
        Core.ShapeKind.Star => Glyph.Star,
        _ => Glyph.Rect,
    };

    private static void DrawUndo(Graphics g, Pen pen)
    {
        g.DrawLines(pen, new[] { new PointF(9, 3.5f), new PointF(4, 8.5f), new PointF(9, 13.5f) });
        g.DrawBezier(pen, new PointF(4, 8.5f), new PointF(16, 8.5f), new PointF(22, 11), new PointF(19, 17));
        g.DrawLine(pen, 19, 17, 15.5f, 20);
    }
}
