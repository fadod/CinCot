using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using CInCot.Core;

namespace CInCot.Export;

internal static class Exporter
{
    /// <summary>Burns the annotations into a new bitmap the size of the selection.</summary>
    public static Bitmap Flatten(Bitmap source, AnnotationDocument doc)
    {
        var selection = doc.Selection;
        var result = new Bitmap(selection.Width, selection.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(result);
        g.TranslateTransform(-selection.X, -selection.Y);
        g.DrawImage(source, selection, selection, GraphicsUnit.Pixel);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        doc.Render(g);
        return result;
    }

    /// <summary>
    /// Puts the image on the clipboard as a DIB and as PNG. Captures containing redactions are kept out
    /// of Windows clipboard history and cloud clipboard sync.
    /// </summary>
    public static void CopyToClipboard(Bitmap image, bool keepOutOfHistory)
    {
        var data = new DataObject();
        data.SetData(DataFormats.Bitmap, true, image);
        var png = new MemoryStream();
        image.Save(png, ImageFormat.Png);
        data.SetData("PNG", false, png);
        if (keepOutOfHistory)
        {
            data.SetData("CanIncludeInClipboardHistory", false, new MemoryStream(BitConverter.GetBytes(0)));
            data.SetData("CanUploadToCloudClipboard", false, new MemoryStream(BitConverter.GetBytes(0)));
        }
        Clipboard.SetDataObject(data, true, 10, 50);
    }

    /// <summary>Saves to the configured folder and returns the full path.</summary>
    public static string Save(Bitmap image, AppSettings settings)
    {
        var folder = Environment.ExpandEnvironmentVariables(settings.SaveFolder);
        Directory.CreateDirectory(folder);

        string name;
        try
        {
            name = string.Format(CultureInfo.InvariantCulture, settings.FileNamePattern, DateTime.Now);
        }
        catch (FormatException)
        {
            name = $"CInCot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
        }
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');

        string extension = settings.ImageFormat == ImageFormatKind.Jpg ? ".jpg" : ".png";
        string path = Path.Combine(folder, name + extension);
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(folder, $"{name}_{i}{extension}");

        if (settings.ImageFormat == ImageFormatKind.Jpg)
        {
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)settings.JpegQuality);
            image.Save(path, codec, parameters);
        }
        else
        {
            image.Save(path, ImageFormat.Png);
        }
        return path;
    }
}
