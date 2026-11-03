using System.Drawing.Drawing2D;
using CInCot.Core;

namespace CInCot.Overlay;

/// <summary>Everything the toolbar shows that can change between frames.</summary>
internal readonly record struct ToolbarState(
    ToolKind Tool, int ColorIndex, float StrokeWidth, LineStyle LineStyle, ArrowHeads Heads, bool Rounded,
    bool CanUndo, bool CanRedo, string Status, bool HasText);

/// <summary>
/// The floating editing toolbar, painted directly on the overlay (no child windows), so showing it is
/// just a layout pass. Rows: Select Blur Pixelate Redact Rect Arrow Text / Pen Highlight Crop Copy-text
/// Undo Copy Save / colours, thickness, Redo, text-recognition status, Close / and, for Rect and Arrow,
/// line styles plus corner or arrowhead options.
/// </summary>
internal sealed class Toolbar : IDisposable
{
    internal enum Kind { Tool, Undo, Redo, Copy, Save, Color, Thinner, Thicker, Thickness, Close, CopyText, Status, Line, Heads, Corners }

    internal sealed record Button(Kind Kind, string Label, string Tip, Rectangle Bounds, ToolKind Tool = default, int Index = 0);

    private const int Columns = 7;

    private static readonly (string Label, ToolKind Tool, string Tip)[] ToolRow =
    {
        ("Select", ToolKind.SelectText, "Select text (S) · right-click for options"),
        ("Blur", ToolKind.Blur, "Blur (B)"), ("Pixelate", ToolKind.Pixelate, "Pixelate (X)"), ("Redact", ToolKind.Redact, "Redact (R)"),
        ("Rect", ToolKind.Rect, "Rectangle (U)"), ("Arrow", ToolKind.Arrow, "Arrow (A)"), ("Text", ToolKind.Text, "Text (T)"),
    };

    private static readonly string[] ColorNames = { "Red", "Yellow", "Green", "Blue", "Black", "White" };
    private static readonly string[] LineNames = { "Solid line", "Dashed line", "Dotted line", "Dash-dot line" };

    private readonly List<Button> _buttons = new();
    private Rectangle _monitor;
    private float _scale;
    private Font? _font;

    public Rectangle Bounds { get; private set; }
    public IReadOnlyList<Button> Buttons => _buttons;
    public Font Font => _font ??= MakeFont(1f);

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

        bool styleRow = HasStyleRow(tool);
        int bw = P(64), bh = P(28), sh = P(22), gap = P(4), pad = P(6), margin = P(8);
        int width = pad * 2 + bw * Columns + gap * (Columns - 1);
        int height = pad * 2 + bh * 2 + sh + gap * 2 + (styleRow ? sh + gap : 0);

        // Below the selection, else above it, else inside its bottom edge; never across monitors.
        int x = selection.Left;
        int y = selection.Bottom + margin;
        if (y + height > monitor.Bottom - P(4)) y = selection.Top - margin - height;
        if (y < monitor.Top + P(4)) y = selection.Bottom - height - margin;
        x = Math.Clamp(x, monitor.Left + P(4), Math.Max(monitor.Left + P(4), monitor.Right - width - P(4)));
        y = Math.Clamp(y, monitor.Top + P(4), Math.Max(monitor.Top + P(4), monitor.Bottom - height - P(4)));
        Bounds = new Rectangle(x, y, width, height);

        _buttons.Clear();
        int row1 = y + pad, row2 = row1 + bh + gap, row3 = row2 + bh + gap, row4 = row3 + sh + gap;
        Rectangle Cell(int column, int top, int h) => new(x + pad + column * (bw + gap), top, bw, h);

        for (int i = 0; i < ToolRow.Length; i++)
        {
            var t = ToolRow[i];
            _buttons.Add(new Button(Kind.Tool, t.Label, t.Tip, Cell(i, row1, bh), t.Tool));
        }
        _buttons.Add(new Button(Kind.Tool, "Pen", "Pen (P)", Cell(0, row2, bh), ToolKind.Pen));
        _buttons.Add(new Button(Kind.Tool, "Highlight", "Highlighter (H)", Cell(1, row2, bh), ToolKind.Highlight));
        _buttons.Add(new Button(Kind.Tool, "Crop", "Crop (C)", Cell(2, row2, bh), ToolKind.Crop));
        _buttons.Add(new Button(Kind.CopyText, "Copy text", "Copy selected text, or all text if none is selected", Cell(3, row2, bh)));
        _buttons.Add(new Button(Kind.Undo, "Undo", "Undo (Ctrl+Z)", Cell(4, row2, bh)));
        _buttons.Add(new Button(Kind.Copy, "Copy", "Copy image and close (Enter)", Cell(5, row2, bh)));
        _buttons.Add(new Button(Kind.Save, "Save", "Save and close (Ctrl+S)", Cell(6, row2, bh)));

        int half = (bw - gap) / 2;
        Rectangle Half(int column, int top, int span = 1) => new(x + pad + column * (half + gap), top, half * span + gap * (span - 1), sh);
        for (int i = 0; i < ColorNames.Length; i++)
            _buttons.Add(new Button(Kind.Color, "", $"{ColorNames[i]} ({i + 1})", Half(i, row3), Index: i));
        _buttons.Add(new Button(Kind.Thinner, "−", "Thinner ( [ )", Half(6, row3)));
        _buttons.Add(new Button(Kind.Thickness, "", "Line thickness · scroll here or Ctrl+scroll to change", Half(7, row3)));
        _buttons.Add(new Button(Kind.Thicker, "+", "Thicker ( ] )", Half(8, row3)));
        _buttons.Add(new Button(Kind.Redo, "↷", "Redo (Ctrl+Y)", Half(9, row3)));
        _buttons.Add(new Button(Kind.Status, "", "Text recognition", Half(10, row3, 2)));
        _buttons.Add(new Button(Kind.Close, "Close", "Cancel (Esc)", Half(12, row3, 2)));

        if (!styleRow) return;
        for (int i = 0; i < LineNames.Length; i++)
            _buttons.Add(new Button(Kind.Line, "", LineNames[i] + " (L to cycle)", Cell(i, row4, sh), Index: i));
        if (tool == ToolKind.Arrow)
        {
            _buttons.Add(new Button(Kind.Heads, "→", "Arrowhead at the end", Cell(4, row4, sh), Index: (int)ArrowHeads.End));
            _buttons.Add(new Button(Kind.Heads, "↔", "Arrowheads at both ends", Cell(5, row4, sh), Index: (int)ArrowHeads.Both));
            _buttons.Add(new Button(Kind.Heads, "—", "Plain line, no arrowhead", Cell(6, row4, sh), Index: (int)ArrowHeads.None));
        }
        else
        {
            _buttons.Add(new Button(Kind.Corners, "Square", "Square corners", Cell(4, row4, sh), Index: 0));
            _buttons.Add(new Button(Kind.Corners, "Rounded", "Rounded corners", Cell(5, row4, sh), Index: 1));
        }
    }

    public Button? HitTest(Point p)
    {
        if (!Bounds.Contains(p)) return null;
        foreach (var b in _buttons)
            if (b.Kind != Kind.Status && b.Bounds.Contains(p)) return b;
        return null;
    }

    public Rectangle TooltipBounds(Button? hover)
    {
        if (hover is null) return Rectangle.Empty;
        var size = TextRenderer.MeasureText(hover.Tip, Font);
        int w = size.Width + P(12), h = size.Height + P(8);
        int x = Math.Clamp(hover.Bounds.X + hover.Bounds.Width / 2 - w / 2, _monitor.Left + P(2), Math.Max(_monitor.Left, _monitor.Right - w - P(2)));
        int y = Bounds.Bottom + P(4);
        if (y + h > _monitor.Bottom) y = Bounds.Top - h - P(4);
        return new Rectangle(x, y, w, h);
    }

    public void Paint(Graphics g, in ToolbarState state, IReadOnlyList<Color> palette, Button? hover)
    {
        if (Bounds.IsEmpty) return;
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var background = new SolidBrush(Color.FromArgb(244, 32, 32, 36)))
        using (var path = Drawing.RoundRect(Bounds, P(10)))
        using (var border = new Pen(Color.FromArgb(80, 255, 255, 255), 1f))
        {
            g.FillPath(background, path);
            g.DrawPath(border, path);
        }

        using var normal = new SolidBrush(Color.FromArgb(48, 48, 54));
        using var hovered = new SolidBrush(Color.FromArgb(70, 70, 78));
        using var active = new SolidBrush(OverlayForm.Accent);
        const TextFormatFlags centered = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

        foreach (var b in _buttons)
        {
            bool isHover = ReferenceEquals(b, hover);
            switch (b.Kind)
            {
                case Kind.Color:
                {
                    var swatch = Rectangle.Inflate(b.Bounds, -P(2), -P(2));
                    using (var fill = new SolidBrush(palette[b.Index]))
                        Drawing.FillRoundRect(g, fill, swatch, P(4));
                    if (b.Index == state.ColorIndex || isHover)
                    {
                        using var ring = new Pen(b.Index == state.ColorIndex ? Color.White : Color.FromArgb(160, 255, 255, 255), Math.Max(1.5f, 2f * _scale));
                        using var path = Drawing.RoundRect(Rectangle.Inflate(b.Bounds, -1, -1), P(5));
                        g.DrawPath(ring, path);
                    }
                    continue;
                }
                case Kind.Status:
                    TextRenderer.DrawText(g, state.Status, Font, b.Bounds, Color.FromArgb(170, 170, 178), centered);
                    continue;
                case Kind.Thickness:
                    Drawing.FillRoundRect(g, isHover ? hovered : normal, b.Bounds, P(5));
                    TextRenderer.DrawText(g, $"{state.StrokeWidth:0}", Font, b.Bounds, Color.White, centered);
                    continue;
                case Kind.Line:
                {
                    bool selected = b.Index == (int)state.LineStyle;
                    Drawing.FillRoundRect(g, selected ? active : isHover ? hovered : normal, b.Bounds, P(5));
                    // A live sample of the dash pattern at a readable thickness.
                    var sample = new Stroke(Color.White, Math.Clamp(state.StrokeWidth * _scale, 2f * _scale, 4f * _scale), (LineStyle)b.Index);
                    using var pen = sample.CreatePen();
                    int cy = b.Bounds.Y + b.Bounds.Height / 2, inset = P(12);
                    g.DrawLine(pen, b.Bounds.Left + inset, cy, b.Bounds.Right - inset, cy);
                    continue;
                }
            }

            bool isActive = b.Kind switch
            {
                Kind.Tool => b.Tool == state.Tool,
                Kind.Heads => b.Index == (int)state.Heads,
                Kind.Corners => (b.Index == 1) == state.Rounded,
                _ => false,
            };
            Drawing.FillRoundRect(g, isActive ? active : isHover ? hovered : normal, b.Bounds, P(5));
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
            var color = enabled ? Color.White : Color.FromArgb(110, 110, 118);
            TextRenderer.DrawText(g, b.Label, Font, b.Bounds, color, centered);
        }

        if (hover is not null)
        {
            var tip = TooltipBounds(hover);
            using var tipBrush = new SolidBrush(Color.FromArgb(240, 20, 20, 22));
            Drawing.FillRoundRect(g, tipBrush, tip, P(4));
            TextRenderer.DrawText(g, hover.Tip, Font, tip, Color.White, centered);
        }

        g.SmoothingMode = smoothing;
    }

    private static Font MakeFont(float scale) => new("Segoe UI", 12f * scale, FontStyle.Regular, GraphicsUnit.Pixel);

    public void Dispose() => _font?.Dispose();
}
