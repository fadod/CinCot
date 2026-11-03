using System.Drawing.Drawing2D;
using CInCot.Core;

namespace CInCot.Overlay;

/// <summary>Everything the toolbar shows that can change between frames.</summary>
internal readonly record struct ToolbarState(
    ToolKind Tool, int ColorIndex, float StrokeWidth, LineStyle LineStyle, ArrowHeads Heads, bool Rounded,
    bool CanUndo, bool CanRedo, string Status, bool HasText);

/// <summary>
/// The floating editing toolbar, painted directly on the overlay (no child windows), so showing it is
/// just a layout pass. Row 1 holds icon buttons for tools and actions; row 2 holds the colour button,
/// thickness and, for Rect and Arrow, line style and corner/arrowhead options. The colour button opens
/// a small palette pop-up.
/// </summary>
internal sealed class Toolbar : IDisposable
{
    internal enum Kind
    {
        Tool, Undo, Redo, Copy, Save, Close, CopyText,
        ColorPicker, Color, Thinner, Thicker, Thickness, Line, Heads, Corners, Status,
    }

    internal sealed record Button(Kind Kind, Glyph Glyph, string Tip, Rectangle Bounds, ToolKind Tool = default, int Index = 0);

    private static readonly (Glyph Glyph, ToolKind Tool, string Tip)[] RedactTools =
    {
        (Glyph.SelectText, ToolKind.SelectText, "Select text (S) · right-click for copy and redact"),
        (Glyph.Blur, ToolKind.Blur, "Blur (B)"),
        (Glyph.Pixelate, ToolKind.Pixelate, "Pixelate (X)"),
        (Glyph.Redact, ToolKind.Redact, "Redact (R)"),
    };

    private static readonly (Glyph Glyph, ToolKind Tool, string Tip)[] DrawTools =
    {
        (Glyph.Rect, ToolKind.Rect, "Rectangle (U)"),
        (Glyph.Arrow, ToolKind.Arrow, "Arrow (A)"),
        (Glyph.Text, ToolKind.Text, "Text (T)"),
        (Glyph.Pen, ToolKind.Pen, "Pen (P)"),
        (Glyph.Highlight, ToolKind.Highlight, "Highlighter (H)"),
        (Glyph.Crop, ToolKind.Crop, "Crop (C)"),
    };

    internal static readonly string[] ColorNames = { "Red", "Orange", "Yellow", "Green", "Teal", "Blue", "Purple", "Pink", "Black", "White" };
    private static readonly string[] LineNames = { "Solid line", "Dashed line", "Dotted line", "Dash-dot line" };

    private readonly List<Button> _buttons = new();
    private readonly List<Rectangle> _separators = new();
    private Rectangle _monitor;
    private Rectangle _statusBounds;
    private float _scale;
    private Font? _font;

    public Rectangle Bounds { get; private set; }

    /// <summary>The colour palette pop-up, or empty when it is closed.</summary>
    public Rectangle PopupBounds { get; private set; }

    public bool ColorPopupOpen { get; set; }
    public IReadOnlyList<Button> Buttons => _buttons;
    public Font Font => _font ??= MakeFont(1f);

    /// <summary>Toolbar and pop-up together, for repainting.</summary>
    public Rectangle FullBounds => Drawing.Union(Rectangle.Inflate(Bounds, P(4), P(4)), PopupBounds.IsEmpty ? Rectangle.Empty : Rectangle.Inflate(PopupBounds, P(4), P(4)));

    private int P(float v) => (int)Math.Round(v * _scale);

    public static bool HasStyleRow(ToolKind tool) => tool is ToolKind.Rect or ToolKind.Arrow;

    public void Layout(Rectangle selection, Rectangle monitor, float scale, ToolKind tool)
    {
        if (_font is null || Math.Abs(scale - _scale) > 0.01f)
        {
            _font?.Dispose();
            _font = MakeFont(scale);
        }
        _scale = scale;
        _monitor = monitor;
        _buttons.Clear();
        _separators.Clear();

        int bs = P(34), gap = P(3), sep = P(13), pad = P(7), rowGap = P(5), margin = P(10);

        // Measure both rows at the origin, then move everything into place.
        int x = pad, y1 = pad, y2 = pad + bs + rowGap;
        void Add(Kind kind, Glyph glyph, string tip, int width, int y, ToolKind t = default, int index = 0)
        {
            _buttons.Add(new Button(kind, glyph, tip, new Rectangle(x, y, width, bs), t, index));
            x += width + gap;
        }
        void Separator(int y)
        {
            x += sep / 2 - gap;
            _separators.Add(new Rectangle(x, y + P(7), 1, bs - P(14)));
            x += sep / 2 + 1;
        }

        foreach (var t in RedactTools) Add(Kind.Tool, t.Glyph, t.Tip, bs, y1, t.Tool);
        Separator(y1);
        foreach (var t in DrawTools) Add(Kind.Tool, t.Glyph, t.Tip, bs, y1, t.Tool);
        Separator(y1);
        Add(Kind.Undo, Glyph.Undo, "Undo (Ctrl+Z)", bs, y1);
        Add(Kind.Redo, Glyph.Redo, "Redo (Ctrl+Y)", bs, y1);
        Separator(y1);
        Add(Kind.CopyText, Glyph.CopyText, "Copy text (selected, or all if none)", bs, y1);
        Add(Kind.Copy, Glyph.Copy, "Copy image and close (Enter)", bs, y1);
        Add(Kind.Save, Glyph.Save, "Save and close (Ctrl+S)", bs, y1);
        Add(Kind.Close, Glyph.Close, "Cancel (Esc)", bs, y1);
        int row1Width = x - gap + pad;

        x = pad;
        Add(Kind.ColorPicker, Glyph.Chevron, "Colour (1-9, 0)", bs + P(14), y2);
        Separator(y2);
        Add(Kind.Thinner, Glyph.Minus, "Thinner ( [ )", bs, y2);
        Add(Kind.Thickness, Glyph.Minus, "Line thickness · scroll here or Ctrl+scroll to change", P(30), y2);
        Add(Kind.Thicker, Glyph.Plus, "Thicker ( ] )", bs, y2);
        if (HasStyleRow(tool))
        {
            Separator(y2);
            for (int i = 0; i < LineNames.Length; i++) Add(Kind.Line, Glyph.Minus, LineNames[i] + " (L to cycle)", P(42), y2, index: i);
            Separator(y2);
            if (tool == ToolKind.Arrow)
            {
                Add(Kind.Heads, Glyph.HeadEnd, "Arrowhead at the end", bs, y2, index: (int)ArrowHeads.End);
                Add(Kind.Heads, Glyph.HeadBoth, "Arrowheads at both ends", bs, y2, index: (int)ArrowHeads.Both);
                Add(Kind.Heads, Glyph.HeadNone, "Plain line, no arrowhead", bs, y2, index: (int)ArrowHeads.None);
            }
            else
            {
                Add(Kind.Corners, Glyph.Square, "Square corners", bs, y2, index: 0);
                Add(Kind.Corners, Glyph.Rounded, "Rounded corners", bs, y2, index: 1);
            }
        }
        int row2Used = x - gap;
        int statusWidth = P(92);
        int width = Math.Max(row1Width, row2Used + P(8) + statusWidth + pad);
        int height = pad * 2 + bs * 2 + rowGap;

        // Below the selection, else above it, else inside its bottom edge; never across monitors.
        int left = selection.Left;
        int top = selection.Bottom + margin;
        if (top + height > monitor.Bottom - P(4)) top = selection.Top - margin - height;
        if (top < monitor.Top + P(4)) top = selection.Bottom - height - margin;
        left = Math.Clamp(left, monitor.Left + P(4), Math.Max(monitor.Left + P(4), monitor.Right - width - P(4)));
        top = Math.Clamp(top, monitor.Top + P(4), Math.Max(monitor.Top + P(4), monitor.Bottom - height - P(4)));
        Bounds = new Rectangle(left, top, width, height);

        for (int i = 0; i < _buttons.Count; i++)
        {
            var b = _buttons[i];
            var r = b.Bounds;
            r.Offset(left, top);
            _buttons[i] = b with { Bounds = r };
        }
        for (int i = 0; i < _separators.Count; i++)
        {
            var r = _separators[i];
            r.Offset(left, top);
            _separators[i] = r;
        }
        _statusBounds = new Rectangle(left + width - pad - statusWidth, top + y2, statusWidth, bs);

        LayoutPopup();
    }

    private void LayoutPopup()
    {
        PopupBounds = Rectangle.Empty;
        if (!ColorPopupOpen) return;
        var picker = _buttons.First(b => b.Kind == Kind.ColorPicker).Bounds;
        int sw = P(28), gap = P(6), pad = P(8), cols = 5, rows = (ColorNames.Length + cols - 1) / cols;
        int w = pad * 2 + cols * sw + (cols - 1) * gap, h = pad * 2 + rows * sw + (rows - 1) * gap;
        int x = Math.Clamp(picker.X, _monitor.Left + P(4), Math.Max(_monitor.Left, _monitor.Right - w - P(4)));
        // Open away from the toolbar: above it when there is room, otherwise below.
        int y = Bounds.Top - h - P(6);
        if (y < _monitor.Top + P(4)) y = Bounds.Bottom + P(6);
        PopupBounds = new Rectangle(x, y, w, h);
        for (int i = 0; i < ColorNames.Length; i++)
        {
            var r = new Rectangle(x + pad + i % cols * (sw + gap), y + pad + i / cols * (sw + gap), sw, sw);
            _buttons.Add(new Button(Kind.Color, Glyph.Minus, $"{ColorNames[i]} ({(i + 1) % 10})", r, Index: i));
        }
    }

    public bool Contains(Point p) => Bounds.Contains(p) || PopupBounds.Contains(p);

    public Button? HitTest(Point p)
    {
        if (!Contains(p)) return null;
        // Pop-up swatches are added last and sit on top.
        for (int i = _buttons.Count - 1; i >= 0; i--)
            if (_buttons[i].Kind != Kind.Status && _buttons[i].Bounds.Contains(p)) return _buttons[i];
        return null;
    }

    public Rectangle TooltipBounds(Button? hover)
    {
        if (hover is null) return Rectangle.Empty;
        var size = TextRenderer.MeasureText(hover.Tip, Font);
        int w = size.Width + P(14), h = size.Height + P(9);
        int x = Math.Clamp(hover.Bounds.X + hover.Bounds.Width / 2 - w / 2, _monitor.Left + P(2), Math.Max(_monitor.Left, _monitor.Right - w - P(2)));
        var anchor = hover.Kind == Kind.Color ? PopupBounds : Bounds;
        int y = anchor.Bottom + P(5);
        if (y + h > _monitor.Bottom || (hover.Kind != Kind.Color && PopupBounds.IntersectsWith(new Rectangle(x, y, w, h))))
            y = anchor.Top - h - P(5);
        return new Rectangle(x, y, w, h);
    }

    public void Paint(Graphics g, in ToolbarState state, IReadOnlyList<Color> palette, Button? hover)
    {
        if (Bounds.IsEmpty) return;
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        PaintPanel(g, Bounds);

        using var hovered = new SolidBrush(Color.FromArgb(62, 62, 70));
        using var active = new SolidBrush(OverlayForm.Accent);
        using var well = new SolidBrush(Color.FromArgb(46, 46, 52));
        using var divider = new SolidBrush(Color.FromArgb(70, 255, 255, 255));
        var ink = Color.FromArgb(236, 236, 240);
        var dim = Color.FromArgb(100, 100, 110);
        float iconSize = 20f * _scale;
        const TextFormatFlags centered = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

        foreach (var s in _separators) g.FillRectangle(divider, s);

        foreach (var b in _buttons)
        {
            if (b.Kind == Kind.Color) continue; // drawn with the pop-up
            bool isHover = ReferenceEquals(b, hover);
            bool isActive = b.Kind switch
            {
                Kind.Tool => b.Tool == state.Tool,
                Kind.Heads => b.Index == (int)state.Heads,
                Kind.Corners => (b.Index == 1) == state.Rounded,
                Kind.Line => b.Index == (int)state.LineStyle,
                _ => false,
            };
            bool enabled = b.Kind switch
            {
                Kind.Undo => state.CanUndo,
                Kind.Redo => state.CanRedo,
                Kind.CopyText => state.HasText,
                Kind.Tool when b.Tool == ToolKind.SelectText => state.HasText,
                Kind.Thinner => state.StrokeWidth > OverlayForm.MinStroke,
                Kind.Thicker => state.StrokeWidth < OverlayForm.MaxStroke,
                _ => true,
            };

            var face = Rectangle.Inflate(b.Bounds, -1, -1);
            if (b.Kind == Kind.Thickness) Drawing.FillRoundRect(g, isHover ? hovered : well, face, P(6));
            else if (isActive) Drawing.FillRoundRect(g, active, face, P(6));
            // The colour button stays neutral while open so a blue swatch never disappears into the accent.
            else if ((isHover && enabled) || (b.Kind == Kind.ColorPicker && ColorPopupOpen)) Drawing.FillRoundRect(g, hovered, face, P(6));

            var color = enabled ? ink : dim;
            switch (b.Kind)
            {
                case Kind.Thickness:
                    TextRenderer.DrawText(g, $"{state.StrokeWidth:0}", Font, b.Bounds, ink, centered);
                    break;
                case Kind.Line:
                {
                    var sample = new Stroke(color, Math.Clamp(state.StrokeWidth, 2f, 3.5f) * _scale, (LineStyle)b.Index);
                    using var pen = sample.CreatePen();
                    int cy = b.Bounds.Y + b.Bounds.Height / 2, inset = P(9);
                    g.DrawLine(pen, b.Bounds.Left + inset, cy, b.Bounds.Right - inset, cy);
                    break;
                }
                case Kind.ColorPicker:
                {
                    // A swatch of the current colour with a chevron: click to open the palette.
                    int d = P(20);
                    var dot = new Rectangle(b.Bounds.X + P(7), b.Bounds.Y + (b.Bounds.Height - d) / 2, d, d);
                    using (var fill = new SolidBrush(palette[state.ColorIndex])) g.FillEllipse(fill, dot);
                    using (var ring = new Pen(Color.FromArgb(200, 255, 255, 255), Math.Max(1.2f, 1.5f * _scale))) g.DrawEllipse(ring, dot);
                    var chevron = new Rectangle(dot.Right, b.Bounds.Y, b.Bounds.Right - dot.Right, b.Bounds.Height);
                    Icons.Draw(g, Glyph.Chevron, chevron, ink, 13f * _scale);
                    break;
                }
                default:
                    Icons.Draw(g, b.Glyph, b.Bounds, color, iconSize);
                    break;
            }
        }

        TextRenderer.DrawText(g, state.Status, Font, _statusBounds, Color.FromArgb(150, 150, 160),
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

        if (!PopupBounds.IsEmpty)
        {
            PaintPanel(g, PopupBounds);
            foreach (var b in _buttons.Where(b => b.Kind == Kind.Color))
            {
                bool selected = b.Index == state.ColorIndex;
                var dot = Rectangle.Inflate(b.Bounds, -P(3), -P(3));
                using (var fill = new SolidBrush(palette[b.Index])) g.FillEllipse(fill, dot);
                using (var edge = new Pen(Color.FromArgb(90, 255, 255, 255), 1f)) g.DrawEllipse(edge, dot);
                if (selected || ReferenceEquals(b, hover))
                {
                    using var ring = new Pen(selected ? Color.White : Color.FromArgb(170, 255, 255, 255), Math.Max(1.5f, 2f * _scale));
                    g.DrawEllipse(ring, Rectangle.Inflate(b.Bounds, -1, -1));
                }
            }
        }

        if (hover is not null)
        {
            var tip = TooltipBounds(hover);
            using var tipBrush = new SolidBrush(Color.FromArgb(245, 16, 16, 18));
            Drawing.FillRoundRect(g, tipBrush, tip, P(5));
            TextRenderer.DrawText(g, hover.Tip, Font, tip, Color.White, centered);
        }

        g.SmoothingMode = smoothing;
    }

    private void PaintPanel(Graphics g, Rectangle bounds)
    {
        // Soft shadow, then the panel.
        using (var shadowPath = Drawing.RoundRect(new Rectangle(bounds.X, bounds.Y + P(2), bounds.Width, bounds.Height), P(12)))
        using (var shadow = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
            g.FillPath(shadow, shadowPath);
        using var path = Drawing.RoundRect(bounds, P(12));
        using var background = new SolidBrush(Color.FromArgb(248, 30, 30, 34));
        using var border = new Pen(Color.FromArgb(55, 255, 255, 255), 1f);
        g.FillPath(background, path);
        g.DrawPath(border, path);
    }

    private static Font MakeFont(float scale) => new("Segoe UI", 12f * scale, FontStyle.Regular, GraphicsUnit.Pixel);

    public void Dispose() => _font?.Dispose();
}
