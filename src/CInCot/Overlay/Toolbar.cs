using System.Drawing.Drawing2D;
using CInCot.Core;

namespace CInCot.Overlay;

/// <summary>
/// The floating editing toolbar, painted directly on the overlay (no child windows), so showing it is
/// just a layout pass. Rows: Select Blur Pixelate Redact Rect Arrow Text / Pen Highlight Crop Copy-text
/// Undo Copy Save / colours, stroke width, Redo, text-recognition status, Close.
/// </summary>
internal sealed class Toolbar : IDisposable
{
    internal enum Kind { Tool, Undo, Redo, Copy, Save, Color, Width, Close, CopyText, Status }

    internal sealed record Button(Kind Kind, string Label, string Tip, Rectangle Bounds, ToolKind Tool = default, int Index = 0);

    private const int Columns = 7;

    private static readonly (string Label, ToolKind Tool, string Tip)[] ToolRow =
    {
        ("Select", ToolKind.SelectText, "Select text (S) · right-click for options"),
        ("Blur", ToolKind.Blur, "Blur (B)"), ("Pixelate", ToolKind.Pixelate, "Pixelate (X)"), ("Redact", ToolKind.Redact, "Redact (R)"),
        ("Rect", ToolKind.Rect, "Rectangle (U)"), ("Arrow", ToolKind.Arrow, "Arrow (A)"), ("Text", ToolKind.Text, "Text (T)"),
    };

    private static readonly string[] ColorNames = { "Red", "Yellow", "Green", "Blue", "Black", "White" };
    private static readonly string[] WidthLabels = { "S", "M", "L" };

    private readonly List<Button> _buttons = new();
    private Rectangle _monitor;
    private float _scale;
    private Font? _font;

    public Rectangle Bounds { get; private set; }
    public IReadOnlyList<Button> Buttons => _buttons;
    public Font Font => _font ??= MakeFont(1f);

    private int P(float v) => (int)Math.Round(v * _scale);

    public void Layout(Rectangle selection, Rectangle monitor, float scale)
    {
        if (_font is null || Math.Abs(scale - _scale) > 0.01f)
        {
            _font?.Dispose();
            _font = MakeFont(scale);
        }
        _scale = scale;
        _monitor = monitor;

        int bw = P(64), bh = P(28), sh = P(22), gap = P(4), pad = P(6), margin = P(8);
        int width = pad * 2 + bw * Columns + gap * (Columns - 1);
        int height = pad * 2 + bh * 2 + sh + gap * 2;

        // Below the selection, else above it, else inside its bottom edge; never across monitors.
        int x = selection.Left;
        int y = selection.Bottom + margin;
        if (y + height > monitor.Bottom - P(4)) y = selection.Top - margin - height;
        if (y < monitor.Top + P(4)) y = selection.Bottom - height - margin;
        x = Math.Clamp(x, monitor.Left + P(4), Math.Max(monitor.Left + P(4), monitor.Right - width - P(4)));
        y = Math.Clamp(y, monitor.Top + P(4), Math.Max(monitor.Top + P(4), monitor.Bottom - height - P(4)));
        Bounds = new Rectangle(x, y, width, height);

        _buttons.Clear();
        int row1 = y + pad, row2 = row1 + bh + gap, row3 = row2 + bh + gap;
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
        Rectangle Half(int column, int span = 1) => new(x + pad + column * (half + gap), row3, half * span + gap * (span - 1), sh);
        for (int i = 0; i < ColorNames.Length; i++)
            _buttons.Add(new Button(Kind.Color, "", $"{ColorNames[i]} ({i + 1})", Half(i), Index: i));
        for (int i = 0; i < WidthLabels.Length; i++)
            _buttons.Add(new Button(Kind.Width, WidthLabels[i], "Stroke width ([ and ])", Half(6 + i), Index: i));
        _buttons.Add(new Button(Kind.Redo, "↷", "Redo (Ctrl+Y)", Half(9)));
        _buttons.Add(new Button(Kind.Status, "", "Text recognition", Half(10, 2)));
        _buttons.Add(new Button(Kind.Close, "Close", "Cancel (Esc)", Half(12, 2)));
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

    public void Paint(Graphics g, ToolKind tool, int colorIndex, int widthIndex, IReadOnlyList<Color> palette,
        Button? hover, bool canUndo, bool canRedo, string status, bool hasText)
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
            if (b.Kind == Kind.Color)
            {
                var swatch = Rectangle.Inflate(b.Bounds, -P(2), -P(2));
                using (var fill = new SolidBrush(palette[b.Index]))
                    Drawing.FillRoundRect(g, fill, swatch, P(4));
                if (b.Index == colorIndex || isHover)
                {
                    using var ring = new Pen(b.Index == colorIndex ? Color.White : Color.FromArgb(160, 255, 255, 255), Math.Max(1.5f, 2f * _scale));
                    using var path = Drawing.RoundRect(Rectangle.Inflate(b.Bounds, -1, -1), P(5));
                    g.DrawPath(ring, path);
                }
                continue;
            }
            if (b.Kind == Kind.Status)
            {
                TextRenderer.DrawText(g, status, Font, b.Bounds, Color.FromArgb(170, 170, 178), centered);
                continue;
            }

            bool isActive = (b.Kind == Kind.Tool && b.Tool == tool) || (b.Kind == Kind.Width && b.Index == widthIndex);
            Drawing.FillRoundRect(g, isActive ? active : isHover ? hovered : normal, b.Bounds, P(5));
            bool enabled = b.Kind switch
            {
                Kind.Undo => canUndo,
                Kind.Redo => canRedo,
                Kind.CopyText => hasText,
                Kind.Tool when b.Tool == ToolKind.SelectText => hasText,
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
