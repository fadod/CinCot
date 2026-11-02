using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CInCot.Core;

public enum ImageFormatKind { Png, Jpg }

/// <summary>User settings, stored as JSON in %LOCALAPPDATA%\CInCot\settings.json.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CInCot");
    private static string FilePath => Path.Combine(DataFolder, "settings.json");

    [Category("Save"), DisplayName("Save folder"), Description("Where Ctrl+S puts screenshots. Environment variables are expanded.")]
    public string SaveFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "CInCot");

    [Category("Save"), DisplayName("File name pattern"), Description(".NET format string; {0} is the capture time, e.g. CInCot_{0:yyyy-MM-dd_HH-mm-ss}.")]
    public string FileNamePattern { get; set; } = "CInCot_{0:yyyy-MM-dd_HH-mm-ss}";

    [Category("Save"), DisplayName("Image format")]
    public ImageFormatKind ImageFormat { get; set; } = ImageFormatKind.Png;

    [Category("Save"), DisplayName("JPEG quality"), Description("30-100.")]
    public int JpegQuality { get; set; } = 90;

    [Category("Save"), DisplayName("Also copy when saving")]
    public bool CopyWhenSaving { get; set; } = true;

    [Category("Capture"), DisplayName("Dim outside selection (%)"), Description("0-90.")]
    public int DimPercent { get; set; } = 45;

    [Category("Capture"), DisplayName("Show magnifier")]
    public bool ShowMagnifier { get; set; } = true;

    [Category("Capture"), DisplayName("Snap to windows"), Description("Highlight the window under the cursor; click to select it.")]
    public bool SnapToWindows { get; set; } = true;

    [Category("Editing"), DisplayName("Tool after selecting"), Description("Active tool when the toolbar appears.")]
    public ToolKind DefaultTool { get; set; } = ToolKind.Pixelate;

    [Category("Editing"), DisplayName("Pixelate block size (px)"), Description("4-64. Larger is safer.")]
    public int PixelateBlockSize { get; set; } = 12;

    [Category("Editing"), DisplayName("Blur strength"), Description("4-40. Larger is safer.")]
    public int BlurStrength { get; set; } = 14;

    [Category("Text recognition"), DisplayName("Recognise text"), Description("Run on-device OCR on every capture so text can be selected, copied and redacted.")]
    public bool TextRecognition { get; set; } = true;

    [Category("Text recognition"), DisplayName("Upscale for small text"), Description("1-3. Enlarging the image before OCR finds small screen text more reliably but takes longer.")]
    public float OcrUpscale { get; set; } = 2f;

    [Category("General"), DisplayName("Show notifications")]
    public bool ShowNotifications { get; set; } = true;

    [Browsable(false)]
    public bool FirstRun { get; set; } = true;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings()).Normalized();
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataFolder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;

    public void CopyFrom(AppSettings other)
    {
        foreach (var property in typeof(AppSettings).GetProperties())
            if (property.CanWrite) property.SetValue(this, property.GetValue(other));
        Normalized();
    }

    private AppSettings Normalized()
    {
        JpegQuality = Math.Clamp(JpegQuality, 30, 100);
        DimPercent = Math.Clamp(DimPercent, 0, 90);
        PixelateBlockSize = Math.Clamp(PixelateBlockSize, 4, 64);
        BlurStrength = Math.Clamp(BlurStrength, 4, 40);
        OcrUpscale = Math.Clamp(OcrUpscale, 1f, 3f);
        if (string.IsNullOrWhiteSpace(FileNamePattern)) FileNamePattern = "CInCot_{0:yyyy-MM-dd_HH-mm-ss}";
        return this;
    }
}
