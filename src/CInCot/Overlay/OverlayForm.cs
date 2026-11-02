using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Text;
using CInCot.Capture;
using CInCot.Core;
using CInCot.Export;
using CInCot.Native;

namespace CInCot.Overlay;

internal enum OverlayOutcome { Cancelled, Copied, Saved, Failed }

internal sealed record OverlayResult(OverlayOutcome Outcome, Size Size, string? Path = null, string? Error = null);

/// <summary>
/// The frozen-screen overlay and the editor in one. A single borderless topmost window covers the whole
/// virtual desktop in physical pixels, so client coordinates equal capture-frame coordinates. It is created
/// once at startup and only shown and hidden afterwards.
/// </summary>
internal sealed class OverlayForm : Form
{
    private enum Phase { Idle, Selecting, Editing }
    private enum DragKind { None, Select, Draw, Resize, TextRange, TextBox }
    private enum OcrState { Off, Running, Ready }

    internal static readonly Color Accent = Color.FromArgb(0, 120, 212);
    internal static readonly Color[] Palette =
    {
        Color.FromArgb(232, 17, 35), Color.FromArgb(255, 185, 0), Color.FromArgb(16, 160, 16),
        Color.FromArgb(0, 120, 212), Color.Black, Color.White,
    };
    private static readonly float[] StrokeWidths = { 2f, 4f, 8f };
    private static readonly float[] TextSizes = { 18f, 26f, 40f };
    private const string Hint = "Drag to select  ·  Click a window  ·  Hold B / X / R while dragging to start redacting  ·  Esc to cancel";

    private readonly AppSettings _settings;
    private readonly Toolbar _toolbar = new();
    private CaptureFrame? _frame;
    private DibBitmap? _dimmed;
    private AnnotationDocument? _doc;
    private Phase _phase;
    private DragKind _drag;
    private Point _dragStart, _mouse;
    private Rectangle _dragRect;
    private Rectangle? _hoverWindow;
    private int _resizeHandle = -1;
    private Rectangle _resizeOrigin;
    private ToolKind _tool;
    private int _colorIndex, _widthIndex = 1;
    private Annotation? _preview;
    private List<Point>? _stroke;
    private Point? _textAt;
    private readonly StringBuilder _text = new();
    private Toolbar.Button? _hoverButton;
    private Rectangle _lastDynamic;
    private float _scale = 1f;
    private Font _labelFont = new("Segoe UI", 12f, FontStyle.Regular, GraphicsUnit.Pixel);

    // Text recognition and text selection.
    private int _session;
    private OcrPage? _ocr;
    private OcrState _ocrState;
    private readonly SortedSet<int> _textSelection = new();
    private int _anchorWord = -1;
    private readonly ContextMenuStrip _menu = new() { ShowImageMargin = false };
    private readonly ToolStripMenuItem _menuUndo, _menuRedo, _menuCopyText, _menuSelectAll, _menuRedact, _menuBlur, _menuPixelate;
    private readonly System.Windows.Forms.Timer _toastTimer = new() { Interval = 1800 };
    private string? _toast;
    private Rectangle _toastRect;

    public OverlayForm(AppSettings settings)
    {
        _settings = settings;
        Text = "CInCot";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        TopMost = true;
        KeyPreview = true;
        Cursor = Cursors.Cross;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);

        _menuUndo = new ToolStripMenuItem("Undo", null, (_, _) => Undo()) { ShortcutKeyDisplayString = "Ctrl+Z" };
        _menuRedo = new ToolStripMenuItem("Redo", null, (_, _) => Redo()) { ShortcutKeyDisplayString = "Ctrl+Y" };
        _menuCopyText = new ToolStripMenuItem("Copy text", null, (_, _) => CopyText()) { ShortcutKeyDisplayString = "Ctrl+C" };
        _menuSelectAll = new ToolStripMenuItem("Select all text", null, (_, _) => SelectAllText()) { ShortcutKeyDisplayString = "Ctrl+A" };
        _menuRedact = new ToolStripMenuItem("Redact selected text", null, (_, _) => RedactSelectedText(RedactionKind.Box)) { ShortcutKeyDisplayString = "Del" };
        _menuBlur = new ToolStripMenuItem("Blur selected text", null, (_, _) => RedactSelectedText(RedactionKind.Blur));
        _menuPixelate = new ToolStripMenuItem("Pixelate selected text", null, (_, _) => RedactSelectedText(RedactionKind.Pixelate));
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _menuUndo, _menuRedo, new ToolStripSeparator(),
            _menuCopyText, _menuSelectAll, new ToolStripSeparator(), _menuRedact, _menuBlur, _menuPixelate, new ToolStripSeparator(),
            new ToolStripMenuItem("Copy image and close", null, (_, _) => Export(save: false)) { ShortcutKeyDisplayString = "Enter" },
            new ToolStripMenuItem("Save image and close", null, (_, _) => Export(save: true)) { ShortcutKeyDisplayString = "Ctrl+S" },
        });
        _menu.Opening += (_, _) => UpdateMenu();
        _toastTimer.Tick += (_, _) => HideToast();
    }

    public event EventHandler<OverlayResult>? Completed;

    public bool IsActive => _phase != Phase.Idle;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Win32.WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        // The overlay deliberately spans monitors with different DPIs; never let WinForms rescale it.
        if (m.Msg == Win32.WM_DPICHANGED)
        {
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Tab || base.IsInputKey(keyData);

    // ---------------------------------------------------------------- lifecycle

    public void Begin(CaptureFrame frame, CaptureMode mode)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        int session = ++_session;
        _ocr = null;
        _ocrState = OcrState.Off;
        _textSelection.Clear();
        _anchorWord = -1;
        _frame = frame;
        _dimmed = new DibBitmap(frame.Image.Width, frame.Image.Height);
        Effects.Dim(frame.Image, _dimmed.Bitmap, 1f - _settings.DimPercent / 100f);
        Log.Trace($"dim {watch.ElapsedMilliseconds} ms");
        _doc = new AnnotationDocument();
        _tool = _settings.DefaultTool;
        _drag = DragKind.None;
        _preview = null;
        _stroke = null;
        _textAt = null;
        _text.Clear();
        _hoverWindow = null;
        _hoverButton = null;
        _lastDynamic = Rectangle.Empty;
        var cursor = Cursor.Position;
        _mouse = new Point(cursor.X - frame.VirtualBounds.X, cursor.Y - frame.VirtualBounds.Y);
        UpdateScale(_mouse);

        Rectangle? initial = mode switch
        {
            CaptureMode.FullScreen => frame.MonitorAt(_mouse).Bounds,
            CaptureMode.ActiveWindow => frame.ForegroundBounds,
            _ => null,
        };
        if (initial is { Width: > 1, Height: > 1 } start)
        {
            EnterEditing(start);
        }
        else
        {
            _phase = Phase.Selecting;
            Cursor = Cursors.Cross;
            UpdateHover();
        }

        var vb = frame.VirtualBounds;
        Bounds = vb;
        Show();
        Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, vb.X, vb.Y, vb.Width, vb.Height, Win32.SWP_SHOWWINDOW);
        Win32.ForceForeground(Handle);
        Activate();
        Focus();
        Log.Trace($"shown {watch.ElapsedMilliseconds} ms");
        Invalidate();
        _lastDynamic = DynamicBounds();

        // Posted, so it runs after the first frame is on screen and never delays the freeze.
        if (_settings.TextRecognition && OcrService.IsAvailable)
        {
            _ocrState = OcrState.Running;
            BeginInvoke(() => StartTextRecognition(session, frame));
        }
    }

    private void Finish(OverlayOutcome outcome, string? path = null, string? error = null)
    {
        if (_phase == Phase.Idle) return;
        var size = _doc?.Selection.Size ?? Size.Empty;
        _phase = Phase.Idle;
        _session++;
        _menu.Close();
        _toastTimer.Stop();
        _toast = null;
        _ocr = null;
        _textSelection.Clear();
        Hide();
        _preview?.Dispose();
        _preview = null;
        _doc?.Dispose();
        _doc = null;
        _dimmed?.Dispose();
        _dimmed = null;
        _frame?.Dispose();
        _frame = null;
        Completed?.Invoke(this, new OverlayResult(outcome, size, path, error));
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Finish(OverlayOutcome.Cancelled);
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        // Keep the overlay on top if something else grabs focus mid-capture.
        if (_phase != Phase.Idle && !_menu.Visible)
            BeginInvoke(() => { if (_phase != Phase.Idle && !_menu.Visible) Win32.ForceForeground(Handle); });
    }

    // ---------------------------------------------------------------- state helpers

    private Rectangle ImageBounds => _frame?.Bounds ?? Rectangle.Empty;

    private int Px(float v) => (int)Math.Round(v * _scale);

    private Rectangle CurrentSelection => _phase switch
    {
        Phase.Selecting => _drag == DragKind.Select ? _dragRect : _hoverWindow ?? Rectangle.Empty,
        Phase.Editing => _drag == DragKind.Resize ? _dragRect : _doc!.Selection,
        _ => Rectangle.Empty,
    };

    private void UpdateScale(Point framePoint)
    {
        if (_frame is null) return;
        float scale = _frame.MonitorAt(framePoint).Scale;
        if (Math.Abs(scale - _scale) < 0.01f) return;
        _scale = scale;
        _labelFont.Dispose();
        _labelFont = new Font("Segoe UI", 12f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    private void UpdateHover()
    {
        if (_frame is null) return;
        _hoverWindow = _settings.SnapToWindows ? _frame.WindowAt(_mouse) ?? _frame.MonitorAt(_mouse).Bounds : null;
    }

    private void EnterEditing(Rectangle selection)
    {
        selection = Rectangle.Intersect(selection, ImageBounds);
        _doc!.Reset(selection);
        _phase = Phase.Editing;
        _hoverWindow = null;
        LayoutToolbar();
    }

    private void LayoutToolbar()
    {
        if (_frame is null || _doc is null) return;
        var sel = _doc.Selection;
        var anchor = new Point(sel.X + sel.Width / 2, Math.Max(sel.Top, sel.Bottom - 1));
        UpdateScale(anchor);
        _toolbar.Layout(sel, _frame.MonitorAt(anchor).Bounds, _scale);
    }

    private void SetTool(ToolKind tool)
    {
        CommitText();
        // The Select tool outlines every recognised word, so switching to or from it repaints the selection.
        if ((tool == ToolKind.SelectText) != (_tool == ToolKind.SelectText) && _doc is not null)
            Invalidate(Rectangle.Inflate(_doc.Selection, Px(4), Px(4)));
        _tool = tool;
        RefreshDynamic();
    }

    private void SetCursor(Cursor cursor)
    {
        if (Cursor != cursor) Cursor = cursor;
    }

    // ---------------------------------------------------------------- invalidation

    /// <summary>Everything that can change between frames, so only that area is repainted.</summary>
    private Rectangle DynamicBounds()
    {
        var r = Rectangle.Empty;
        var sel = CurrentSelection;
        if (_phase == Phase.Selecting)
        {
            if (!sel.IsEmpty) r = Drawing.Union(r, Rectangle.Inflate(sel, Px(8), Px(8)));
            r = Drawing.Union(r, SizeLabelRect(sel));
            if (_settings.ShowMagnifier) r = Drawing.Union(r, LoupeRect());
            r = Drawing.Union(r, HintRect());
        }
        else if (_phase == Phase.Editing)
        {
            r = Drawing.Union(r, _toolbar.Bounds);
            r = Drawing.Union(r, _toolbar.TooltipBounds(_hoverButton));
            r = Drawing.Union(r, SizeLabelRect(sel));
            if (_drag == DragKind.Resize) r = Drawing.Union(r, Rectangle.Inflate(sel, Px(8), Px(8)));
            if (_drag == DragKind.Draw && _tool == ToolKind.Crop) r = Drawing.Union(r, Rectangle.Inflate(_dragRect, Px(4), Px(4)));
            if (_preview is not null) r = Drawing.Union(r, Rectangle.Inflate(_preview.Bounds, Px(4), Px(4)));
            if (_drag == DragKind.TextBox) r = Drawing.Union(r, Rectangle.Inflate(_dragRect, Px(4), Px(4)));
            if (_toast is not null) r = Drawing.Union(r, _toastRect);
        }
        return r;
    }

    private void RefreshDynamic()
    {
        var now = DynamicBounds();
        var dirty = Drawing.Union(_lastDynamic, now);
        if (!dirty.IsEmpty) Invalidate(dirty);
        _lastDynamic = now;
    }

    private void InvalidateSelectionArea(Rectangle old)
    {
        Invalidate(Rectangle.Inflate(old, Px(12), Px(12)));
        Invalidate(SizeLabelRect(old));
        if (_doc is not null)
        {
            Invalidate(Rectangle.Inflate(_doc.Selection, Px(12), Px(12)));
            Invalidate(SizeLabelRect(_doc.Selection));
        }
    }

    // ---------------------------------------------------------------- painting

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        if (_frame is null || _dimmed is null)
        {
            g.Clear(Color.Black);
            return;
        }

        // Background with plain BitBlt: dimmed everywhere, the original inside the selection.
        var clip = e.ClipRectangle;
        var sel = CurrentSelection;
        var bright = Rectangle.Intersect(sel, clip);
        var hdc = g.GetHdc();
        try
        {
            _dimmed.BlitTo(hdc, clip);
            if (bright.Width > 0 && bright.Height > 0) _frame.Pixels.BlitTo(hdc, bright);
        }
        finally
        {
            g.ReleaseHdc(hdc);
        }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        if (_phase == Phase.Editing && _doc is not null)
        {
            var state = g.Save();
            g.SetClip(sel);
            _doc.Render(g);
            _preview?.Render(g);
            if (_textAt is not null && _preview is not null) DrawCaret(g, _preview.Bounds);
            DrawTextLayer(g);
            g.Restore(state);
            if (_drag == DragKind.TextBox && !_dragRect.IsEmpty)
            {
                using var box = new Pen(Accent, Math.Max(1f, _scale)) { DashStyle = DashStyle.Dash };
                g.DrawRectangle(box, _dragRect);
            }
            if (_drag == DragKind.Draw && _tool == ToolKind.Crop && !_dragRect.IsEmpty)
            {
                using var crop = new Pen(Color.White, Math.Max(1f, _scale)) { DashStyle = DashStyle.Dash };
                g.DrawRectangle(crop, _dragRect);
            }
        }

        if (!sel.IsEmpty) DrawSelectionChrome(g, sel);

        if (_phase == Phase.Editing && _doc is not null)
        {
            _toolbar.Paint(g, _tool, _colorIndex, _widthIndex, Palette, _hoverButton, _doc.CanUndo, _doc.CanRedo, OcrStatusText(), HasText);
            if (_toast is not null)
            {
                using var bg = new SolidBrush(Color.FromArgb(235, 20, 20, 22));
                Drawing.FillRoundRect(g, bg, _toastRect, _toastRect.Height / 2f);
                TextRenderer.DrawText(g, _toast, _labelFont, _toastRect, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }
        }

        if (_phase == Phase.Selecting)
        {
            if (_settings.ShowMagnifier) DrawLoupe(g);
            DrawHint(g);
        }
    }

    private void DrawCaret(Graphics g, Rectangle textBounds)
    {
        float size = TextSizes[_widthIndex] * _scale;
        int x = _text.Length == 0 ? _textAt!.Value.X : textBounds.Right - Px(2);
        using var pen = new Pen(Palette[_colorIndex], Math.Max(1.5f, _scale * 1.5f));
        g.DrawLine(pen, x, _textAt!.Value.Y + size * 0.15f, x, _textAt.Value.Y + size * 1.25f);
    }

    private void DrawSelectionChrome(Graphics g, Rectangle sel)
    {
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.None;
        using (var pen = new Pen(Accent, Math.Max(1f, MathF.Round(1.5f * _scale))))
            g.DrawRectangle(pen, sel.X, sel.Y, sel.Width - 1, sel.Height - 1);

        if (_phase == Phase.Editing)
        {
            int s = Px(7);
            using var fill = new SolidBrush(Color.White);
            using var border = new Pen(Accent, 1f);
            foreach (var p in HandlePoints(sel))
            {
                var r = new Rectangle(p.X - s / 2, p.Y - s / 2, s, s);
                g.FillRectangle(fill, r);
                g.DrawRectangle(border, r);
            }
        }
        g.SmoothingMode = smoothing;

        var label = SizeLabelRect(sel);
        if (!label.IsEmpty)
        {
            using var bg = new SolidBrush(Color.FromArgb(220, 20, 20, 22));
            Drawing.FillRoundRect(g, bg, label, Px(4));
            TextRenderer.DrawText(g, SizeText(sel), _labelFont, label, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
    }

    private static string SizeText(Rectangle sel) => $"{sel.Width} × {sel.Height}";

    private Rectangle SizeLabelRect(Rectangle sel)
    {
        if (_frame is null || sel.Width <= 0 || sel.Height <= 0) return Rectangle.Empty;
        var size = TextRenderer.MeasureText(SizeText(sel), _labelFont);
        int w = size.Width + Px(12), h = size.Height + Px(6);
        var monitor = _frame.MonitorAt(sel.Location).Bounds;
        int x = sel.X, y = sel.Y - h - Px(4);
        if (y < monitor.Top)
        {
            x = sel.X + Px(4);
            y = sel.Y + Px(4);
        }
        return new Rectangle(x, y, w, h);
    }

    private Rectangle LoupeRect()
    {
        if (_frame is null) return Rectangle.Empty;
        int size = Px(120), textHeight = Px(22);
        var monitor = _frame.MonitorAt(_mouse).Bounds;
        int x = _mouse.X + Px(20), y = _mouse.Y + Px(20);
        if (x + size > monitor.Right) x = _mouse.X - Px(20) - size;
        if (y + size + textHeight > monitor.Bottom) y = _mouse.Y - Px(20) - size - textHeight;
        return new Rectangle(x, y, size, size + textHeight);
    }

    private void DrawLoupe(Graphics g)
    {
        if (_frame is null) return;
        var r = LoupeRect();
        const int cells = 15;
        var image = _frame.Image;
        int sx = Math.Clamp(_mouse.X - cells / 2, 0, Math.Max(0, image.Width - cells));
        int sy = Math.Clamp(_mouse.Y - cells / 2, 0, Math.Max(0, image.Height - cells));
        var square = new Rectangle(r.X, r.Y, r.Width, r.Width);

        var state = g.Save();
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImage(image, square, new Rectangle(sx, sy, cells, cells), GraphicsUnit.Pixel);
        g.Restore(state);

        float cell = square.Width / (float)cells;
        var center = new RectangleF(square.X + (_mouse.X - sx) * cell, square.Y + (_mouse.Y - sy) * cell, cell, cell);
        using (var pen = new Pen(Accent, 1.5f))
        {
            g.DrawRectangle(pen, center.X, center.Y, center.Width, center.Height);
            g.DrawRectangle(pen, square);
        }

        var px = Drawing.Clamp(_mouse, image.Width > 0 ? new Rectangle(0, 0, image.Width, image.Height) : Rectangle.Empty);
        var color = image.GetPixel(px.X, px.Y);
        var info = new Rectangle(r.X, square.Bottom, r.Width, r.Height - square.Height);
        using (var bg = new SolidBrush(Color.FromArgb(230, 20, 20, 22)))
            g.FillRectangle(bg, info);
        var vb = _frame.VirtualBounds;
        TextRenderer.DrawText(g, $"{_mouse.X + vb.X}, {_mouse.Y + vb.Y}  #{color.R:X2}{color.G:X2}{color.B:X2}", _labelFont, info,
            Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }

    private Rectangle HintRect()
    {
        if (_frame is null) return Rectangle.Empty;
        var monitor = _frame.MonitorAt(_mouse).Bounds;
        var size = TextRenderer.MeasureText(Hint, _labelFont);
        int w = size.Width + Px(24), h = size.Height + Px(14);
        return new Rectangle(monitor.X + (monitor.Width - w) / 2, monitor.Bottom - h - Px(48), w, h);
    }

    private void DrawHint(Graphics g)
    {
        var r = HintRect();
        if (r.Contains(_mouse)) return;
        using var bg = new SolidBrush(Color.FromArgb(225, 20, 20, 22));
        Drawing.FillRoundRect(g, bg, r, r.Height / 2f);
        TextRenderer.DrawText(g, Hint, _labelFont, r, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }

    // ---------------------------------------------------------------- selection handles

    private static Point[] HandlePoints(Rectangle r)
    {
        int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        return new[]
        {
            new Point(r.Left, r.Top), new Point(cx, r.Top), new Point(r.Right - 1, r.Top), new Point(r.Right - 1, cy),
            new Point(r.Right - 1, r.Bottom - 1), new Point(cx, r.Bottom - 1), new Point(r.Left, r.Bottom - 1), new Point(r.Left, cy),
        };
    }

    private int HandleAt(Rectangle sel, Point p)
    {
        int reach = Px(6);
        var points = HandlePoints(sel);
        for (int i = 0; i < points.Length; i++)
            if (Math.Abs(points[i].X - p.X) <= reach && Math.Abs(points[i].Y - p.Y) <= reach) return i;
        return -1;
    }

    private static Cursor HandleCursor(int handle) => handle switch
    {
        0 or 4 => Cursors.SizeNWSE,
        2 or 6 => Cursors.SizeNESW,
        1 or 5 => Cursors.SizeNS,
        _ => Cursors.SizeWE,
    };

    private Rectangle ResizeRect(Rectangle o, int handle, int dx, int dy)
    {
        int l = o.Left, t = o.Top, r = o.Right, b = o.Bottom;
        switch (handle)
        {
            case 0: l += dx; t += dy; break;
            case 1: t += dy; break;
            case 2: r += dx; t += dy; break;
            case 3: r += dx; break;
            case 4: r += dx; b += dy; break;
            case 5: b += dy; break;
            case 6: l += dx; b += dy; break;
            case 7: l += dx; break;
        }
        var rect = Rectangle.FromLTRB(Math.Min(l, r), Math.Min(t, b), Math.Max(l, r), Math.Max(t, b));
        return Rectangle.Intersect(rect, ImageBounds);
    }

    // ---------------------------------------------------------------- mouse

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_frame is null || _phase == Phase.Idle) return;
        _mouse = e.Location;

        if (e.Button == MouseButtons.Right)
        {
            if (_phase == Phase.Selecting) Finish(OverlayOutcome.Cancelled);
            else if (_drag == DragKind.None && !_toolbar.Bounds.Contains(e.Location)) ShowContextMenu(e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        if (_phase == Phase.Selecting)
        {
            _drag = DragKind.Select;
            _dragStart = e.Location;
            _dragRect = Rectangle.Empty;
            return;
        }

        if (_toolbar.Bounds.Contains(e.Location))
        {
            if (_toolbar.HitTest(e.Location) is { } button) OnToolbarButton(button);
            return;
        }

        CommitText();
        var sel = _doc!.Selection;
        int handle = HandleAt(sel, e.Location);
        if (handle >= 0)
        {
            _drag = DragKind.Resize;
            _resizeHandle = handle;
            _resizeOrigin = sel;
            _dragRect = sel;
            _dragStart = e.Location;
            _lastDynamic = Drawing.Union(_lastDynamic, Rectangle.Inflate(sel, Px(12), Px(12)));
            return;
        }

        if (!sel.Contains(e.Location))
        {
            // Nothing drawn yet: dragging outside starts a fresh selection.
            if (_doc.IsEmpty)
            {
                var old = sel;
                _doc.Dispose();
                _doc = new AnnotationDocument();
                _phase = Phase.Selecting;
                _drag = DragKind.Select;
                _dragStart = e.Location;
                _dragRect = Rectangle.Empty;
                _hoverButton = null;
                InvalidateSelectionArea(old);
                RefreshDynamic();
            }
            return;
        }

        if (_tool == ToolKind.SelectText)
        {
            _dragStart = e.Location;
            if (WordAt(e.Location) is { } word)
            {
                // Shift+click extends from the previous anchor, like a text editor.
                if (!ModifierKeys.HasFlag(Keys.Shift) || _anchorWord < 0) _anchorWord = word;
                _drag = DragKind.TextRange;
                SetTextSelection(WordRange(_anchorWord, word));
            }
            else
            {
                _drag = DragKind.TextBox;
                _dragRect = new Rectangle(e.Location, Size.Empty);
                SetTextSelection(Array.Empty<int>());
            }
            RefreshDynamic();
            return;
        }

        if (_tool == ToolKind.Text)
        {
            _textAt = e.Location;
            _text.Clear();
            UpdateTextPreview();
            return;
        }

        _drag = DragKind.Draw;
        _dragStart = e.Location;
        _dragRect = new Rectangle(e.Location, Size.Empty);
        if (_tool is ToolKind.Pen or ToolKind.Highlight) _stroke = new List<Point> { e.Location };
        UpdatePreview(e.Location, ModifierKeys.HasFlag(Keys.Shift));
        RefreshDynamic();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_frame is null || _phase == Phase.Idle) return;
        _mouse = e.Location;

        if (_phase == Phase.Selecting)
        {
            UpdateScale(e.Location);
            if (_drag == DragKind.Select) _dragRect = Drawing.Normalize(_dragStart, e.Location);
            else UpdateHover();
        }
        else if (_drag == DragKind.Resize)
        {
            _dragRect = ResizeRect(_resizeOrigin, _resizeHandle, e.X - _dragStart.X, e.Y - _dragStart.Y);
        }
        else if (_drag == DragKind.Draw)
        {
            UpdatePreview(e.Location, ModifierKeys.HasFlag(Keys.Shift));
        }
        else if (_drag == DragKind.TextRange)
        {
            if (NearestWord(e.Location) is { } word) SetTextSelection(WordRange(_anchorWord, word));
        }
        else if (_drag == DragKind.TextBox)
        {
            _dragRect = Drawing.Normalize(_dragStart, Drawing.Clamp(e.Location, _doc!.Selection));
            SetTextSelection(VisibleWords().Where(i => _ocr!.Words[i].Bounds.IntersectsWith(_dragRect)));
        }
        else
        {
            UpdateEditingHover(e.Location);
        }
        RefreshDynamic();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        // Double-click a word with the Select tool to select its whole line.
        if (_phase != Phase.Editing || _tool != ToolKind.SelectText || e.Button != MouseButtons.Left) return;
        if (WordAt(e.Location) is not { } word) return;
        int line = _ocr!.Words[word].Line;
        _drag = DragKind.None;
        SetTextSelection(VisibleWords().Where(i => _ocr.Words[i].Line == line));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_frame is null || _phase == Phase.Idle || e.Button != MouseButtons.Left) return;

        if (_phase == Phase.Selecting)
        {
            var selection = Rectangle.Empty;
            if (_drag == DragKind.Select && _dragRect.Width >= 3 && _dragRect.Height >= 3) selection = _dragRect;
            else if (_hoverWindow is { } window) selection = window;
            _drag = DragKind.None;
            if (selection.Width >= 2 && selection.Height >= 2)
            {
                var preset = HeldRedactionKey();
                EnterEditing(selection);
                if (preset is { } tool) _tool = tool;
                Invalidate(Rectangle.Inflate(selection, Px(12), Px(12)));
            }
            else
            {
                UpdateHover();
            }
            RefreshDynamic();
            return;
        }

        if (_drag == DragKind.Resize)
        {
            _drag = DragKind.None;
            var old = _resizeOrigin;
            if (_dragRect.Width >= 2 && _dragRect.Height >= 2) _doc!.SetSelection(_dragRect);
            LayoutToolbar();
            InvalidateSelectionArea(old);
            InvalidateSelectionArea(_dragRect);
        }
        else if (_drag == DragKind.Draw)
        {
            _drag = DragKind.None;
            CommitPreview();
        }
        else if (_drag is DragKind.TextRange or DragKind.TextBox)
        {
            _drag = DragKind.None;
        }
        RefreshDynamic();
    }

    private void UpdateEditingHover(Point p)
    {
        _hoverButton = _toolbar.HitTest(p);
        if (_toolbar.Bounds.Contains(p))
        {
            SetCursor(Cursors.Hand);
            return;
        }
        var sel = _doc!.Selection;
        int handle = HandleAt(sel, p);
        if (handle >= 0) SetCursor(HandleCursor(handle));
        else if (!sel.Contains(p)) SetCursor(_doc.IsEmpty ? Cursors.Cross : Cursors.Default);
        else if (_tool == ToolKind.Text || (_tool == ToolKind.SelectText && WordAt(p) is not null)) SetCursor(Cursors.IBeam);
        else SetCursor(Cursors.Cross);
    }

    private static ToolKind? HeldRedactionKey()
    {
        if (Win32.IsKeyDown((int)Keys.B)) return ToolKind.Blur;
        if (Win32.IsKeyDown((int)Keys.X)) return ToolKind.Pixelate;
        if (Win32.IsKeyDown((int)Keys.R)) return ToolKind.Redact;
        return null;
    }

    // ---------------------------------------------------------------- drawing tools

    private void UpdatePreview(Point p, bool constrain)
    {
        var sel = _doc!.Selection;
        p = Drawing.Clamp(p, sel);
        if (constrain && _tool is ToolKind.Rect or ToolKind.Blur or ToolKind.Pixelate or ToolKind.Redact or ToolKind.Crop)
        {
            int side = Math.Max(Math.Abs(p.X - _dragStart.X), Math.Abs(p.Y - _dragStart.Y));
            p = Drawing.Clamp(new Point(_dragStart.X + Math.Sign(p.X - _dragStart.X) * side, _dragStart.Y + Math.Sign(p.Y - _dragStart.Y) * side), sel);
        }
        _dragRect = Drawing.Normalize(_dragStart, p);
        var color = Palette[_colorIndex];
        float width = StrokeWidths[_widthIndex] * _scale;

        _preview?.Dispose();
        _preview = _tool switch
        {
            ToolKind.Blur => Redaction(RedactionKind.Blur, _settings.BlurStrength),
            ToolKind.Pixelate => Redaction(RedactionKind.Pixelate, _settings.PixelateBlockSize),
            ToolKind.Redact => Redaction(RedactionKind.Box, 0),
            ToolKind.Rect => new RectAnnotation(_dragRect, color, width),
            ToolKind.Arrow => new ArrowAnnotation(_dragStart, constrain ? Snap45(_dragStart, p) : p, color, width),
            ToolKind.Pen or ToolKind.Highlight => Stroke(p, constrain, color, width),
            _ => null,
        };

        Annotation? Redaction(RedactionKind kind, int strength) =>
            _dragRect.Width > 0 && _dragRect.Height > 0
                ? new RedactionAnnotation(kind, _dragRect, _frame!.Image, (int)Math.Round(strength * _scale))
                : null;
    }

    private Annotation Stroke(Point p, bool straight, Color color, float width)
    {
        var points = _stroke ??= new List<Point> { _dragStart };
        if (straight)
        {
            // Shift keeps highlighter and pen strokes on a straight line from the start point.
            var end = Snap45(_dragStart, p);
            points.Clear();
            points.Add(_dragStart);
            points.Add(end);
        }
        else if (points[^1] != p)
        {
            points.Add(p);
        }
        return new StrokeAnnotation(points, color, width, _tool == ToolKind.Highlight);
    }

    private static Point Snap45(Point from, Point to)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        double angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
        double length = Math.Sqrt(dx * dx + dy * dy);
        return new Point(from.X + (int)Math.Round(Math.Cos(angle) * length), from.Y + (int)Math.Round(Math.Sin(angle) * length));
    }

    private void CommitPreview()
    {
        var doc = _doc!;
        if (_tool == ToolKind.Crop)
        {
            var old = doc.Selection;
            var crop = Rectangle.Intersect(_dragRect, old);
            if (crop.Width >= 4 && crop.Height >= 4)
            {
                doc.SetSelection(crop);
                LayoutToolbar();
                InvalidateSelectionArea(old);
            }
            _dragRect = Rectangle.Empty;
            return;
        }

        var preview = _preview;
        _preview = null;
        if (preview is null) return;

        bool keep = preview switch
        {
            RedactionAnnotation r => r.Rect.Width >= 2 && r.Rect.Height >= 2,
            RectAnnotation => _dragRect.Width >= 2 && _dragRect.Height >= 2,
            ArrowAnnotation => Math.Abs(_mouse.X - _dragStart.X) + Math.Abs(_mouse.Y - _dragStart.Y) >= 6,
            _ => true,
        };
        if (!keep)
        {
            Invalidate(Rectangle.Inflate(preview.Bounds, Px(4), Px(4)));
            preview.Dispose();
            return;
        }
        if (preview is StrokeAnnotation)
        {
            preview.Dispose();
            preview = new StrokeAnnotation(_stroke!.ToList(), Palette[_colorIndex], StrokeWidths[_widthIndex] * _scale, _tool == ToolKind.Highlight);
        }
        _stroke = null;
        doc.Add(preview);
        Invalidate(Rectangle.Inflate(preview.Bounds, Px(4), Px(4)));
    }

    private void UpdateTextPreview()
    {
        if (_textAt is not { } at) return;
        _preview?.Dispose();
        _preview = new TextAnnotation(at, _text.ToString(), Palette[_colorIndex], TextSizes[_widthIndex] * _scale);
        RefreshDynamic();
    }

    private void CommitText()
    {
        if (_textAt is null) return;
        var preview = _preview;
        _preview = null;
        _textAt = null;
        if (preview is TextAnnotation { Text.Length: > 0 }) _doc!.Add(preview);
        else preview?.Dispose();
        _text.Clear();
        RefreshDynamic();
        if (preview is not null) Invalidate(Rectangle.Inflate(preview.Bounds, Px(6), Px(6)));
    }

    private void CancelText()
    {
        if (_textAt is null) return;
        if (_preview is not null) Invalidate(Rectangle.Inflate(_preview.Bounds, Px(6), Px(6)));
        _preview?.Dispose();
        _preview = null;
        _textAt = null;
        _text.Clear();
        RefreshDynamic();
    }

    // ---------------------------------------------------------------- toolbar & keyboard

    private void OnToolbarButton(Toolbar.Button button)
    {
        switch (button.Kind)
        {
            case Toolbar.Kind.Tool: SetTool(button.Tool); break;
            case Toolbar.Kind.Undo: Undo(); break;
            case Toolbar.Kind.Redo: Redo(); break;
            case Toolbar.Kind.Copy: Export(save: false); break;
            case Toolbar.Kind.CopyText: CopyText(); break;
            case Toolbar.Kind.Save: Export(save: true); break;
            case Toolbar.Kind.Color: _colorIndex = button.Index; UpdateTextPreview(); RefreshDynamic(); break;
            case Toolbar.Kind.Width: _widthIndex = button.Index; UpdateTextPreview(); RefreshDynamic(); break;
            case Toolbar.Kind.Close: Finish(OverlayOutcome.Cancelled); break;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_frame is null || _phase == Phase.Idle) return;

        if (_textAt is not null)
        {
            switch (e.KeyCode)
            {
                case Keys.Escape:
                    CancelText();
                    e.SuppressKeyPress = true;
                    return;
                case Keys.Enter when e.Shift:
                    _text.Append('\n');
                    UpdateTextPreview();
                    e.SuppressKeyPress = true;
                    return;
                case Keys.Enter:
                    CommitText();
                    e.SuppressKeyPress = true;
                    return;
                case Keys.Back:
                    if (_text.Length > 0) _text.Length--;
                    UpdateTextPreview();
                    e.SuppressKeyPress = true;
                    return;
            }
            if (!e.Control) return; // printable keys arrive in OnKeyPress
            CommitText();
        }

        bool editing = _phase == Phase.Editing;
        switch (e.KeyCode)
        {
            case Keys.Escape:
                if (editing && _textSelection.Count > 0) SetTextSelection(Array.Empty<int>());
                else Finish(OverlayOutcome.Cancelled);
                break;
            case Keys.A when e.Control && editing:
                SelectAllText();
                break;
            case Keys.Delete or Keys.Back when editing && SelectedWords().Any():
                RedactSelectedText(RedactionKind.Box);
                break;
            case Keys.Enter:
                if (editing) Export(save: false);
                else if (_hoverWindow is { } window) { EnterEditing(window); Invalidate(); }
                break;
            case Keys.C when e.Control && editing:
                // With text selected, Ctrl+C copies the text and keeps the capture open.
                if (SelectedWords().Any()) CopyText();
                else Export(save: false);
                break;
            case Keys.S when e.Control && editing:
                Export(save: true);
                break;
            case Keys.Z when e.Control && e.Shift && editing:
            case Keys.Y when e.Control && editing:
                Redo();
                break;
            case Keys.Z when e.Control && editing:
                Undo();
                break;
            case Keys.Left or Keys.Right or Keys.Up or Keys.Down when editing && !e.Control:
                Nudge(e.KeyCode, e.Shift ? 10 : 1);
                break;
            case Keys.OemOpenBrackets when editing:
                _widthIndex = Math.Max(0, _widthIndex - 1);
                RefreshDynamic();
                break;
            case Keys.OemCloseBrackets when editing:
                _widthIndex = Math.Min(StrokeWidths.Length - 1, _widthIndex + 1);
                RefreshDynamic();
                break;
            case >= Keys.D1 and <= Keys.D6 when editing && !e.Control:
                _colorIndex = e.KeyCode - Keys.D1;
                RefreshDynamic();
                break;
            default:
                if (editing && !e.Control && !e.Alt && ToolForKey(e.KeyCode) is { } tool) SetTool(tool);
                else return;
                break;
        }
        e.SuppressKeyPress = true;
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (_textAt is null || char.IsControl(e.KeyChar)) return;
        _text.Append(e.KeyChar);
        UpdateTextPreview();
        e.Handled = true;
    }

    private static ToolKind? ToolForKey(Keys key) => key switch
    {
        Keys.B => ToolKind.Blur,
        Keys.X => ToolKind.Pixelate,
        Keys.R => ToolKind.Redact,
        Keys.U => ToolKind.Rect,
        Keys.A => ToolKind.Arrow,
        Keys.T => ToolKind.Text,
        Keys.P => ToolKind.Pen,
        Keys.H => ToolKind.Highlight,
        Keys.C => ToolKind.Crop,
        Keys.S => ToolKind.SelectText,
        _ => null,
    };

    // ---------------------------------------------------------------- text recognition & selection

    private async void StartTextRecognition(int session, CaptureFrame frame)
    {
        if (session != _session) return;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        float upscale = _settings.OcrUpscale;
        OcrPage page;
        try
        {
            var snapshots = OcrService.Snapshot(frame.Image, frame.Monitors.Select(m => m.Bounds));
            page = await Task.Run(() => OcrService.RecognizeAsync(snapshots, upscale));
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            page = OcrPage.Empty;
        }
        if (session != _session || _phase == Phase.Idle) return;
        _ocr = page;
        _ocrState = OcrState.Ready;
        Log.Trace($"ocr {page.Words.Count} words in {watch.ElapsedMilliseconds} ms (upscale {upscale})");
        if (_tool == ToolKind.SelectText && _doc is not null) Invalidate(Rectangle.Inflate(_doc.Selection, Px(4), Px(4)));
        RefreshDynamic();
    }

    private bool HasText => VisibleWords().Any();

    private string OcrStatusText() => _ocrState switch
    {
        OcrState.Running => "Reading text…",
        OcrState.Ready => VisibleWords().Count() is var n and > 0 ? $"{n} words" : "No text",
        _ => "OCR off",
    };

    /// <summary>Words inside the selection that are not already under a redaction, in reading order.</summary>
    private IEnumerable<int> VisibleWords()
    {
        if (_ocr is null || _doc is null) yield break;
        var selection = _drag == DragKind.Resize ? _dragRect : _doc.Selection;
        var redacted = _doc.Items.OfType<RedactionAnnotation>().Select(r => r.Rect).ToList();
        for (int i = 0; i < _ocr.Words.Count; i++)
        {
            var b = _ocr.Words[i].Bounds;
            var center = new Point(b.X + b.Width / 2, b.Y + b.Height / 2);
            if (!selection.Contains(center) || redacted.Any(r => r.Contains(center))) continue;
            yield return i;
        }
    }

    private IEnumerable<int> SelectedWords()
    {
        if (_textSelection.Count == 0) return Array.Empty<int>();
        return VisibleWords().Where(_textSelection.Contains);
    }

    private int? WordAt(Point p)
    {
        if (_ocr is null) return null;
        foreach (int i in VisibleWords())
            if (Rectangle.Inflate(_ocr.Words[i].Bounds, Px(3), Px(2)).Contains(p)) return i;
        return null;
    }

    /// <summary>The closest word, favouring the same line, so a drag past the end of a line still selects to it.</summary>
    private int? NearestWord(Point p)
    {
        if (_ocr is null) return null;
        int? best = null;
        long bestScore = long.MaxValue;
        foreach (int i in VisibleWords())
        {
            var b = _ocr.Words[i].Bounds;
            long dx = Math.Max(0, Math.Max(b.Left - p.X, p.X - b.Right));
            long dy = Math.Max(0, Math.Max(b.Top - p.Y, p.Y - b.Bottom));
            long score = dx + dy * 4;
            if (score < bestScore) { bestScore = score; best = i; }
        }
        return best;
    }

    private IEnumerable<int> WordRange(int a, int b)
    {
        int lo = Math.Min(a, b), hi = Math.Max(a, b);
        return VisibleWords().Where(i => i >= lo && i <= hi);
    }

    /// <summary>Selected words merged into one rectangle per run on a line, for highlighting and redaction.</summary>
    private List<Rectangle> Segments(IEnumerable<int> words)
    {
        var result = new List<Rectangle>();
        int previous = -2, line = -1;
        foreach (int i in words.Order())
        {
            var word = _ocr!.Words[i];
            if (i == previous + 1 && word.Line == line && result.Count > 0) result[^1] = Rectangle.Union(result[^1], word.Bounds);
            else result.Add(word.Bounds);
            previous = i;
            line = word.Line;
        }
        return result;
    }

    private void SetTextSelection(IEnumerable<int> words)
    {
        var before = _textSelection.Count > 0 && _ocr is not null ? Segments(_textSelection) : new List<Rectangle>();
        _textSelection.Clear();
        _textSelection.UnionWith(words);
        var after = _textSelection.Count > 0 ? Segments(_textSelection) : new List<Rectangle>();
        foreach (var r in before.Concat(after)) Invalidate(Rectangle.Inflate(r, Px(4), Px(4)));
        RefreshDynamic();
    }

    private void DrawTextLayer(Graphics g)
    {
        if (_ocr is null) return;
        if (_tool == ToolKind.SelectText)
        {
            using var outline = new Pen(Color.FromArgb(120, Accent), 1f);
            foreach (int i in VisibleWords())
                if (!_textSelection.Contains(i)) g.DrawRectangle(outline, Rectangle.Inflate(_ocr.Words[i].Bounds, 1, 1));
        }
        var selected = SelectedWords().ToList();
        if (selected.Count == 0) return;
        using var fill = new SolidBrush(Color.FromArgb(95, Accent));
        foreach (var r in Segments(selected)) g.FillRectangle(fill, Rectangle.Inflate(r, Px(2), Px(1)));
    }

    private bool TextNotReady()
    {
        if (_ocr is not null) return false;
        ShowToast(_ocrState == OcrState.Running ? "Still reading text… try again in a moment" : "Text recognition is off or unavailable");
        return true;
    }

    private void SelectAllText()
    {
        if (TextNotReady()) return;
        CommitText();
        if (_tool != ToolKind.SelectText) SetTool(ToolKind.SelectText);
        var all = VisibleWords().ToList();
        Log.Trace($"select all: {all.Count} words in {_doc?.Selection}");
        SetTextSelection(all);
        if (all.Count == 0) ShowToast("No text found in the selection");
    }

    /// <summary>Copies the selected text, or all text in the capture area if nothing is selected. The capture stays open.</summary>
    private void CopyText()
    {
        if (TextNotReady()) return;
        var words = SelectedWords().ToList();
        bool all = words.Count == 0;
        if (all) words = VisibleWords().ToList();
        if (words.Count == 0)
        {
            ShowToast("No text found in the selection");
            return;
        }
        try
        {
            Log.Trace($"copy text: {words.Count} words");
            Clipboard.SetDataObject(_ocr!.TextOf(words), true, 10, 50);
            ShowToast(all ? $"Copied all text ({words.Count} words)" : $"Copied {words.Count} word{(words.Count == 1 ? "" : "s")}");
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            ShowToast("The clipboard is busy; try again");
        }
    }

    private void RedactSelectedText(RedactionKind kind)
    {
        if (TextNotReady() || _frame is null || _doc is null) return;
        CommitText();
        var words = SelectedWords().ToList();
        if (words.Count == 0)
        {
            ShowToast("Select some text first (S, then drag over it)");
            return;
        }
        int strength = kind switch
        {
            RedactionKind.Blur => _settings.BlurStrength,
            RedactionKind.Pixelate => _settings.PixelateBlockSize,
            _ => 0,
        };
        var annotations = Segments(words)
            .Select(r => (Annotation)new RedactionAnnotation(kind, Rectangle.Inflate(r, Px(2), Px(2)), _frame.Image, (int)Math.Round(strength * _scale)))
            .ToList();
        _doc.AddRange(annotations);
        SetTextSelection(Array.Empty<int>());
        foreach (var a in annotations) Invalidate(Rectangle.Inflate(a.Bounds, Px(4), Px(4)));
        ShowToast($"Redacted {words.Count} word{(words.Count == 1 ? "" : "s")} · Ctrl+Z to undo");
    }

    private void ShowContextMenu(Point p)
    {
        CommitText();
        // Right-clicking a word that is not selected selects just that word, as in a text editor.
        if (WordAt(p) is { } word && !_textSelection.Contains(word))
        {
            _anchorWord = word;
            SetTextSelection(new[] { word });
        }
        _menu.Show(this, p);
    }

    private void UpdateMenu()
    {
        bool ready = _ocr is not null;
        bool selected = ready && SelectedWords().Any();
        bool any = ready && HasText;
        _menuCopyText.Text = selected || !any ? "Copy text" : "Copy all text";
        _menuCopyText.Enabled = any;
        _menuSelectAll.Enabled = any;
        _menuRedact.Enabled = _menuBlur.Enabled = _menuPixelate.Enabled = selected;
        _menuUndo.Enabled = _doc?.CanUndo == true;
        _menuRedo.Enabled = _doc?.CanRedo == true;
        if (!ready) _menuCopyText.Text = _ocrState == OcrState.Running ? "Reading text…" : "Copy text (OCR off)";
    }

    private void ShowToast(string text)
    {
        if (_toast is not null) Invalidate(_toastRect);
        _toast = text;
        var size = TextRenderer.MeasureText(text, _labelFont);
        int w = size.Width + Px(24), h = size.Height + Px(12);
        var anchor = _toolbar.Bounds;
        int y = anchor.Top - h - Px(6);
        if (_frame is not null && y < _frame.MonitorAt(anchor.Location).Bounds.Top) y = anchor.Bottom + Px(6);
        _toastRect = new Rectangle(anchor.X + (anchor.Width - w) / 2, y, w, h);
        Invalidate(_toastRect);
        _toastTimer.Stop();
        _toastTimer.Start();
        RefreshDynamic();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        if (_toast is null) return;
        _toast = null;
        Invalidate(_toastRect);
        RefreshDynamic();
    }

    private void Nudge(Keys key, int step)
    {
        var old = _doc!.Selection;
        var moved = old;
        moved.Offset(key switch { Keys.Left => -step, Keys.Right => step, _ => 0 }, key switch { Keys.Up => -step, Keys.Down => step, _ => 0 });
        var bounds = ImageBounds;
        moved.X = Math.Clamp(moved.X, 0, Math.Max(0, bounds.Width - moved.Width));
        moved.Y = Math.Clamp(moved.Y, 0, Math.Max(0, bounds.Height - moved.Height));
        if (moved == old) return;
        _doc.SetSelection(moved);
        LayoutToolbar();
        InvalidateSelectionArea(old);
        RefreshDynamic();
    }

    private void Undo()
    {
        CommitText();
        var old = _doc!.Selection;
        if (!_doc.Undo()) return;
        LayoutToolbar();
        InvalidateSelectionArea(old);
        RefreshDynamic();
    }

    private void Redo()
    {
        var old = _doc!.Selection;
        if (!_doc.Redo()) return;
        LayoutToolbar();
        InvalidateSelectionArea(old);
        RefreshDynamic();
    }

    // ---------------------------------------------------------------- export

    private void Export(bool save)
    {
        if (_frame is null || _doc is null) return;
        CommitText();
        try
        {
            using var image = Exporter.Flatten(_frame.Image, _doc);
            string? path = null;
            if (save) path = Exporter.Save(image, _settings);
            if (!save || _settings.CopyWhenSaving) Exporter.CopyToClipboard(image, _doc.HasRedactions);
            Finish(save ? OverlayOutcome.Saved : OverlayOutcome.Copied, path);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            Finish(OverlayOutcome.Failed, error: ex.Message);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolbar.Dispose();
            _labelFont.Dispose();
            _menu.Dispose();
            _toastTimer.Dispose();
            _doc?.Dispose();
            _dimmed?.Dispose();
            _frame?.Dispose();
        }
        base.Dispose(disposing);
    }
}
