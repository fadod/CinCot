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

        toolbar.Layout(new Rectangle(1800, 1000, 100, 70), monitor, 1.5f);
        Assert.True(monitor.Contains(toolbar.Bounds));

        toolbar.Layout(new Rectangle(0, 0, 1920, 1080), monitor, 1f);
        Assert.True(monitor.Contains(toolbar.Bounds));
        Assert.Equal(14, toolbar.Buttons.Count(b => b.Kind is Toolbar.Kind.Tool or Toolbar.Kind.Undo or Toolbar.Kind.Copy or Toolbar.Kind.Save or Toolbar.Kind.CopyText));
        Assert.Null(toolbar.HitTest(toolbar.Buttons.Single(b => b.Kind == Toolbar.Kind.Status).Bounds.Location + new Size(2, 2)));
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
