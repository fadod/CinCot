using Microsoft.Win32;

namespace Sikirincot.Core;

/// <summary>
/// One-time carry-over from the app's previous name, CInCot: settings, the defaults that still carry the
/// old name, and the Start with Windows entry. Choices the user made themselves are kept as they are.
/// </summary>
internal static class LegacyMigration
{
    private const string OldName = "CInCot";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Pictures => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

    /// <summary>Returns the migrated settings on the first run after the rename, otherwise null.</summary>
    public static AppSettings? Run()
    {
        MoveStartupEntry();
        try
        {
            if (File.Exists(Path.Combine(AppSettings.DataFolder, "settings.json"))) return null;
            var old = AppSettings.LoadFrom(Path.Combine(LocalAppData, OldName, "settings.json"));
            if (old is null) return null;

            var fresh = new AppSettings();
            if (string.Equals(old.SaveFolder, Path.Combine(Pictures, OldName), StringComparison.OrdinalIgnoreCase))
                old.SaveFolder = fresh.SaveFolder;
            if (old.FileNamePattern == OldName + "_{0:yyyy-MM-dd_HH-mm-ss}")
                old.FileNamePattern = fresh.FileNamePattern;
            old.FirstRun = false;
            old.Save();
            return old;
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            return null;
        }
    }

    /// <summary>Points an existing CInCot startup entry at this exe under the new name.</summary>
    private static void MoveStartupEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(OldName) is not string || Environment.ProcessPath is not { } exe) return;
            key.DeleteValue(OldName, throwOnMissingValue: false);
            key.SetValue("Sikirincot", $"\"{exe}\"");
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    }
}
