using CInCot.App;
using CInCot.Core;

namespace CInCot;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\CInCot.SingleInstance", out bool firstInstance);
        using var captureSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CInCot.Capture");
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
        Application.Run(new TrayApp(AppSettings.Load(), captureSignal));
        GC.KeepAlive(mutex);
    }
}
