using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Sikirincot.Core;

/// <summary>One recognised word in frame coordinates. <see cref="Line"/> is a page-wide line number.</summary>
internal sealed record OcrWord(string Text, Rectangle Bounds, int Line);

/// <summary>All words found on a capture, in reading order.</summary>
internal sealed class OcrPage
{
    public static readonly OcrPage Empty = new(Array.Empty<OcrWord>(), true);

    public OcrPage(IReadOnlyList<OcrWord> words, bool useSpaces)
    {
        Words = words;
        UseSpaces = useSpaces;
    }

    public IReadOnlyList<OcrWord> Words { get; }

    /// <summary>False for languages written without spaces between words (Chinese, Japanese).</summary>
    public bool UseSpaces { get; }

    /// <summary>Joins the given word indices: spaces within a line, line breaks between lines.</summary>
    public string TextOf(IEnumerable<int> indices)
    {
        var text = new System.Text.StringBuilder();
        int line = -1;
        foreach (int i in indices.Order())
        {
            var word = Words[i];
            if (line >= 0) text.Append(word.Line != line ? Environment.NewLine : UseSpaces ? " " : "");
            text.Append(word.Text);
            line = word.Line;
        }
        return text.ToString();
    }
}

/// <summary>A copy of one monitor's pixels, taken on the UI thread so recognition can run off it.</summary>
internal sealed record OcrSnapshot(byte[] Pixels, int Width, int Height, Point Origin);

/// <summary>
/// On-device text recognition with Windows.Media.Ocr, in the user's Windows display languages.
/// Nothing leaves the machine.
/// </summary>
internal static class OcrService
{
    private static readonly Lazy<OcrEngine?> Engine = new(() =>
    {
        try
        {
            return OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            return null;
        }
    });

    public static bool IsAvailable => Engine.Value is not null;

    /// <summary>Copies each area of <paramref name="image"/> into an opaque BGRA buffer. Fast; call on the UI thread.</summary>
    public static unsafe List<OcrSnapshot> Snapshot(Bitmap image, IEnumerable<Rectangle> areas)
    {
        var result = new List<OcrSnapshot>();
        foreach (var raw in areas)
        {
            var area = Rectangle.Intersect(raw, new Rectangle(Point.Empty, image.Size));
            if (area.Width < 8 || area.Height < 8) continue;
            var pixels = new byte[area.Width * area.Height * 4];
            var data = image.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                fixed (byte* dst = pixels)
                {
                    for (int y = 0; y < area.Height; y++)
                    {
                        uint* s = (uint*)((byte*)data.Scan0 + (long)y * data.Stride);
                        uint* d = (uint*)(dst + (long)y * area.Width * 4);
                        for (int x = 0; x < area.Width; x++) d[x] = s[x] | 0xFF000000u;
                    }
                }
            }
            finally
            {
                image.UnlockBits(data);
            }
            result.Add(new OcrSnapshot(pixels, area.Width, area.Height, area.Location));
        }
        return result;
    }

    /// <summary>
    /// Recognises every snapshot. Screen text is small, so images are upscaled (up to
    /// <paramref name="upscale"/>×, within the engine's size limit) before recognition.
    /// </summary>
    public static async Task<OcrPage> RecognizeAsync(IReadOnlyList<OcrSnapshot> snapshots, float upscale)
    {
        var engine = Engine.Value;
        if (engine is null) return OcrPage.Empty;

        var words = new List<OcrWord>();
        int lineNumber = 0;
        uint max = OcrEngine.MaxImageDimension;
        foreach (var snapshot in snapshots)
        {
            float scale = Math.Min(upscale, Math.Min((float)max / snapshot.Width, (float)max / snapshot.Height));
            var (pixels, width, height) = scale == 1f
                ? (snapshot.Pixels, snapshot.Width, snapshot.Height)
                : Resize(snapshot, scale);

            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
            var result = await engine.RecognizeAsync(bitmap);
            float sx = (float)width / snapshot.Width, sy = (float)height / snapshot.Height;
            foreach (var line in result.Lines)
            {
                foreach (var word in line.Words)
                {
                    var r = word.BoundingRect;
                    var bounds = Rectangle.FromLTRB(
                        snapshot.Origin.X + (int)Math.Floor(r.X / sx),
                        snapshot.Origin.Y + (int)Math.Floor(r.Y / sy),
                        snapshot.Origin.X + (int)Math.Ceiling((r.X + r.Width) / sx),
                        snapshot.Origin.Y + (int)Math.Ceiling((r.Y + r.Height) / sy));
                    words.Add(new OcrWord(word.Text, bounds, lineNumber));
                }
                lineNumber++;
            }
        }

        var tag = engine.RecognizerLanguage.LanguageTag;
        bool useSpaces = !(tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase) || tag.StartsWith("ja", StringComparison.OrdinalIgnoreCase));
        return new OcrPage(words, useSpaces);
    }

    private static (byte[] Pixels, int Width, int Height) Resize(OcrSnapshot snapshot, float scale)
    {
        int width = Math.Max(1, (int)(snapshot.Width * scale)), height = Math.Max(1, (int)(snapshot.Height * scale));
        var handle = GCHandle.Alloc(snapshot.Pixels, GCHandleType.Pinned);
        try
        {
            using var source = new Bitmap(snapshot.Width, snapshot.Height, snapshot.Width * 4, PixelFormat.Format32bppArgb, handle.AddrOfPinnedObject());
            var pixels = new byte[width * height * 4];
            var target = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                using var scaled = new Bitmap(width, height, width * 4, PixelFormat.Format32bppArgb, target.AddrOfPinnedObject());
                using var g = Graphics.FromImage(scaled);
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(source, new Rectangle(0, 0, width, height));
            }
            finally
            {
                target.Free();
            }
            return (pixels, width, height);
        }
        finally
        {
            handle.Free();
        }
    }
}
