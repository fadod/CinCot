using Sikirincot.App;
using Sikirincot.Core;

namespace Sikirincot;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\Sikirincot.SingleInstance", out bool firstInstance);
        using var captureSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Sikirincot.Capture");
        if (!firstInstance)
        {
            // Already running in the tray: launching the exe again means "capture now".
            captureSignal.Set();
            return;
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) Log.Error(ex); };

        ApplicationConfiguration.Initialize();
        // A copy still running under the old name would grab PrtSc too; this replaces it.
        foreach (var old in System.Diagnostics.Process.GetProcessesByName("CInCot"))
        {
            try { old.Kill(); } catch (Exception ex) { Log.Error(ex); }
        }
        var settings = LegacyMigration.Run() ?? AppSettings.Load();
        Application.Run(new TrayApp(settings, captureSignal));
        GC.KeepAlive(mutex);
    }
}
