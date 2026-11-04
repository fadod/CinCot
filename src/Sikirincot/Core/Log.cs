namespace Sikirincot.Core;

/// <summary>Minimal error log in %LOCALAPPDATA%\Sikirincot\logs. Never contains image content.</summary>
internal static class Log
{
    private static readonly object Gate = new();

    /// <summary>Set SIKIRINCOT_TRACE=1 to log hot-path timings.</summary>
    public static bool TraceEnabled { get; } = Environment.GetEnvironmentVariable("SIKIRINCOT_TRACE") == "1";

    public static void Error(Exception ex) => Write(ex.ToString());

    public static void Trace(string message)
    {
        if (TraceEnabled) Write("trace " + message);
    }

    private static void Write(string line)
    {
        try
        {
            var folder = Path.Combine(AppSettings.DataFolder, "logs");
            Directory.CreateDirectory(folder);
            lock (Gate)
                File.AppendAllText(Path.Combine(folder, "sikirincot.log"), $"{DateTime.Now:O} {line}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
