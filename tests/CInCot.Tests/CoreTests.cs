using System.Drawing.Imaging;
using CInCot.Core;
using CInCot.Export;
using CInCot.Overlay;

namespace CInCot.Tests;

public class AnnotationDocumentTests
{
    [Fact]
    public void Undo_and_redo_restore_annotations_and_selection()
    {
        using var doc = new AnnotationDocument();
        doc.Reset(new Rectangle(0, 0, 100, 100));
        doc.Add(new RectAnnotation(new Rectangle(10, 10, 20, 20), Color.Red, 2));
        doc.SetSelection(new Rectangle(5, 5, 50, 50));

        Assert.True(doc.Undo());
        Assert.Equal(new Rectangle(0, 0, 100, 100), doc.Selection);
        Assert.True(doc.Undo());
        Assert.True(doc.IsEmpty);
        Assert.False(doc.Undo());

        Assert.True(doc.Redo());
        Assert.Single(doc.Items);
        Assert.True(doc.Redo());
        Assert.Equal(new Rectangle(5, 5, 50, 50), doc.Selection);
    }

    [Fact]
    public void A_new_edit_clears_redo()
    {
        using var doc = new AnnotationDocument();
        doc.Reset(new Rectangle(0, 0, 100, 100));
        doc.Add(new RectAnnotation(new Rectangle(1, 1, 5, 5), Color.Red, 2));
        doc.Undo();
        doc.Add(new RectAnnotation(new Rectangle(2, 2, 5, 5), Color.Blue, 2));
        Assert.False(doc.CanRedo);
    }
}

public class EffectsTests
{
    private static Bitmap Gradient(int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                bmp.SetPixel(x, y, Color.FromArgb(x * 255 / w, y * 255 / h, (x + y) % 256));
        return bmp;
    }

    [Fact]
    public void Pixelate_makes_each_block_one_colour()
    {
        using var source = Gradient(64, 64);
        using var result = Effects.Pixelate(source, new Rectangle(0, 0, 64, 64), 16)!;
        Assert.Equal(new Size(64, 64), result.Size);
        var first = result.GetPixel(0, 0);
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                Assert.Equal(first, result.GetPixel(x, y));
        Assert.NotEqual(first, result.GetPixel(48, 48));
    }

    [Fact]
    public void Dim_scales_every_channel()
    {
        using var source = new Bitmap(4, 4, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(source)) g.Clear(Color.FromArgb(200, 100, 50));
        using var dimmed = Effects.Dim(source, 0.5f);
        var p = dimmed.GetPixel(2, 2);
        Assert.Equal(100, p.R);
        Assert.Equal(50, p.G);
        Assert.Equal(25, p.B);
    }

    [Fact]
    public void Box_redaction_overwrites_pixels_on_export()
    {
        using var source = Gradient(80, 60);
        using var doc = new AnnotationDocument();
        doc.Reset(new Rectangle(10, 10, 60, 40));
        doc.Add(new RedactionAnnotation(RedactionKind.Box, new Rectangle(20, 20, 10, 10), source, 0));

        using var output = Exporter.Flatten(source, doc);
        Assert.Equal(new Size(60, 40), output.Size);
        for (int y = 12; y < 18; y++)
            for (int x = 12; x < 18; x++)
                Assert.Equal(Color.FromArgb(0, 0, 0).ToArgb(), output.GetPixel(x, y).ToArgb());
    }
}

public class ToolbarTests
{
    [Fact]
    public void Toolbar_stays_on_the_selection_monitor()
    {
        using var toolbar = new Toolbar();
        var monitor = new Rectangle(0, 0, 1920, 1080);

        toolbar.Layout(new Rectangle(1800, 1000, 100, 70), monitor, 1.5f, ToolKind.Arrow);
        Assert.True(monitor.Contains(toolbar.Bounds));
        Assert.Equal(4, toolbar.Buttons.Count(b => b.Kind == Toolbar.Kind.Line));
        Assert.Equal(3, toolbar.Buttons.Count(b => b.Kind == Toolbar.Kind.Heads));

        toolbar.Layout(new Rectangle(0, 0, 1920, 1080), monitor, 1f, ToolKind.Pixelate);
        Assert.True(monitor.Contains(toolbar.Bounds));
        Assert.DoesNotContain(toolbar.Buttons, b => b.Kind is Toolbar.Kind.Line or Toolbar.Kind.Heads or Toolbar.Kind.HighlightMode);

        toolbar.Layout(new Rectangle(0, 0, 900, 500), monitor, 1f, ToolKind.Highlight);
        Assert.Equal(2, toolbar.Buttons.Count(b => b.Kind == Toolbar.Kind.HighlightMode));
        Assert.Equal(14, toolbar.Buttons.Count(b => b.Kind is Toolbar.Kind.Tool or Toolbar.Kind.Undo or Toolbar.Kind.Copy or Toolbar.Kind.Save or Toolbar.Kind.CopyText));
        Assert.Single(toolbar.Buttons, b => b.Kind == Toolbar.Kind.ColorPicker);
        Assert.DoesNotContain(toolbar.Buttons, b => b.Kind == Toolbar.Kind.Color);
    }

    [Fact]
    public void Colour_button_opens_a_ten_colour_popup_on_screen()
    {
        using var toolbar = new Toolbar { ColorPopupOpen = true };
        var monitor = new Rectangle(0, 0, 1920, 1080);
        toolbar.Layout(new Rectangle(100, 0, 600, 80), monitor, 1f, ToolKind.Rect);

        Assert.Equal(10, toolbar.Buttons.Count(b => b.Kind == Toolbar.Kind.Color));
        Assert.True(monitor.Contains(toolbar.PopupBounds));
        Assert.False(toolbar.PopupBounds.IntersectsWith(toolbar.Bounds));
        var swatch = toolbar.Buttons.First(b => b.Kind == Toolbar.Kind.Color && b.Index == 4);
        Assert.Same(swatch, toolbar.HitTest(new Point(swatch.Bounds.X + 5, swatch.Bounds.Y + 5)));
    }
}

public class ShapeStyleTests
{
    [Fact]
    public void Restyling_the_same_shape_repeatedly_is_one_undo_step()
    {
        using var doc = new AnnotationDocument();
        doc.Reset(new Rectangle(0, 0, 200, 200));
        var drawn = new RectAnnotation(new Rectangle(10, 10, 50, 50), new Stroke(Color.Red, 2, LineStyle.Solid));
        doc.Add(drawn);

        Annotation current = drawn;
        for (int w = 3; w <= 6; w++)
        {
            var next = ((IRestylable)current).Restyle(new Stroke(Color.Red, w, LineStyle.Dashed), ArrowHeads.End, ShapeKind.Ellipse);
            doc.Replace(current, next);
            current = next;
        }
        var final = Assert.IsType<RectAnnotation>(Assert.Single(doc.Items));
        Assert.Equal(6, final.Stroke.Width);
        Assert.Equal(LineStyle.Dashed, final.Stroke.Style);
        Assert.Equal(ShapeKind.Ellipse, final.Shape);

        Assert.True(doc.Undo());
        Assert.Same(drawn, Assert.Single(doc.Items));
        Assert.True(doc.Redo());
        Assert.Same(final, Assert.Single(doc.Items));
    }

    [Fact]
    public void Shape_dropdown_lists_all_shapes()
    {
        using var toolbar = new Toolbar { Popup = ToolbarPopup.Shape };
        var monitor = new Rectangle(0, 0, 1920, 1080);
        toolbar.Layout(new Rectangle(100, 300, 600, 300), monitor, 1f, ToolKind.Rect);
        Assert.Equal(Enum.GetValues<ShapeKind>().Length, toolbar.Buttons.Count(b => b.Kind == Toolbar.Kind.Shape));
        Assert.True(monitor.Contains(toolbar.PopupBounds));
    }

    [Theory]
    [InlineData(ShapeKind.Ellipse)]
    [InlineData(ShapeKind.Triangle)]
    [InlineData(ShapeKind.Diamond)]
    [InlineData(ShapeKind.Star)]
    public void Shapes_stay_inside_their_box(ShapeKind shape)
    {
        using var bmp = new Bitmap(120, 120);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            new RectAnnotation(new Rectangle(20, 20, 80, 80), new Stroke(Color.Black, 2, LineStyle.Solid), shape).Render(g);
        }
        int inside = 0, outside = 0;
        for (int y = 0; y < 120; y++)
            for (int x = 0; x < 120; x++)
                if (bmp.GetPixel(x, y).R < 128)
                {
                    if (x >= 17 && x <= 103 && y >= 17 && y <= 103) inside++;
                    else outside++;
                }
        Assert.True(inside > 100);
        Assert.Equal(0, outside);
    }

    [Fact]
    public void Text_highlight_fills_only_its_bands()
    {
        using var bmp = new Bitmap(100, 60);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            new TextHighlightAnnotation(new[] { new Rectangle(10, 10, 50, 12), new Rectangle(10, 30, 30, 12) }).Render(g);
        }
        Assert.True(bmp.GetPixel(30, 15).B < 200);   // yellow-ish inside the first band
        Assert.True(bmp.GetPixel(20, 35).B < 200);   // second band
        Assert.Equal(255, bmp.GetPixel(55, 35).B);   // right of the shorter second band stays white
    }

    [Fact]
    public void Dashed_line_leaves_gaps_and_both_heads_are_drawn()
    {
        using var bmp = new Bitmap(220, 60);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            new ArrowAnnotation(new Point(10, 30), new Point(210, 30), new Stroke(Color.Black, 4, LineStyle.Dashed), ArrowHeads.Both).Render(g);
        }
        int dark = 0;
        for (int x = 40; x < 180; x++) if (bmp.GetPixel(x, 30).R < 128) dark++;
        Assert.InRange(dark, 40, 120);              // dashes, not a solid run of 140
        Assert.True(bmp.GetPixel(14, 30).R < 128);   // tip at the start
        Assert.True(bmp.GetPixel(206, 30).R < 128);  // tip at the end
    }
}

public class TextTests
{
    [Fact]
    public void Text_joins_words_with_spaces_and_lines_with_breaks()
    {
        var page = new OcrPage(new[]
        {
            new OcrWord("Hello", new Rectangle(0, 0, 10, 10), 0),
            new OcrWord("world", new Rectangle(12, 0, 10, 10), 0),
            new OcrWord("again", new Rectangle(0, 20, 10, 10), 1),
        }, useSpaces: true);
        Assert.Equal("Hello world" + Environment.NewLine + "again", page.TextOf(new[] { 2, 0, 1 }));
        Assert.Equal("world", page.TextOf(new[] { 1 }));
    }

    [Fact]
    public void AddRange_is_one_undo_step()
    {
        using var source = new Bitmap(50, 50);
        using var doc = new AnnotationDocument();
        doc.Reset(new Rectangle(0, 0, 50, 50));
        doc.AddRange(new Annotation[]
        {
            new RedactionAnnotation(RedactionKind.Box, new Rectangle(0, 0, 10, 5), source, 0),
            new RedactionAnnotation(RedactionKind.Box, new Rectangle(0, 10, 10, 5), source, 0),
        });
        Assert.Equal(2, doc.Items.Count);
        doc.Undo();
        Assert.True(doc.IsEmpty);
    }

    [Fact]
    public async Task Ocr_reads_rendered_text_and_maps_it_back_to_frame_coordinates()
    {
        if (!OcrService.IsAvailable) return; // no OCR language pack on this machine
        using var image = new Bitmap(900, 300, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(image))
        {
            g.Clear(Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using var font = new Font("Segoe UI", 15f, GraphicsUnit.Pixel);
            g.DrawString("Invoice sent to jane.doe@example.com", font, Brushes.Black, 420, 200);
        }

        var snapshots = OcrService.Snapshot(image, new[] { new Rectangle(400, 150, 500, 150) });
        var page = await OcrService.RecognizeAsync(snapshots, 2f);

        var invoice = Assert.Single(page.Words, w => w.Text == "Invoice");
        Assert.InRange(invoice.Bounds.X, 415, 430);
        Assert.InRange(invoice.Bounds.Y, 195, 215);
        Assert.Contains(page.Words, w => w.Text.Contains("jane.doe@example.com"));
    }
}
