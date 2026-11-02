using System.Diagnostics;
using Microsoft.Win32;
using CInCot.Capture;
using CInCot.Core;
using CInCot.Hotkeys;
using CInCot.Overlay;

namespace CInCot.App;

/// <summary>
/// The resident tray process. It keeps the overlay window and hook warm so PrtSc never pays a cold start.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly AppSettings _settings;
    private readonly OverlayForm _overlay;
    private readonly NotifyIcon _tray;
    private readonly KeyboardHook _hook;
    private readonly RegisteredWaitHandle _signalWait;
    private readonly SynchronizationContext _ui;
    private ToolStripMenuItem? _pauseItem;
    private string? _lastSavedPath;
    private bool _capturing;
    private SettingsForm? _settingsForm;
    private DxgiCapture? _dxgi;

    public TrayApp(AppSettings settings, EventWaitHandle captureSignal)
    {
        _settings = settings;

        // Creating the overlay installs the WinForms synchronization context and pre-creates its window.
        _overlay = new OverlayForm(settings);
        _ = _overlay.Handle;
        _overlay.Completed += OnCaptureCompleted;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _dxgi = DxgiCapture.TryCreate();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        _tray = new NotifyIcon
        {
            Icon = AppIcon.Create(),
            Text = "CInCot — press PrtSc to capture",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) StartCapture(CaptureMode.Region, fromMenu: true);
        };
        _tray.BalloonTipClicked += (_, _) => RevealLastSaved();

        _hook = new KeyboardHook(_ui);
        _hook.Triggered += mode => StartCapture(mode, fromMenu: false);
        try
        {
            _hook.Start();
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            Notify("CInCot could not take over the PrtSc key. Use the tray icon to capture.", ToolTipIcon.Warning);
        }

        // A second launch of CInCot.exe signals this instance to start a region capture.
        _signalWait = ThreadPool.RegisterWaitForSingleObject(captureSignal,
            (_, _) => _ui.Post(_ => StartCapture(CaptureMode.Region, fromMenu: false), null), null, Timeout.Infinite, false);

        if (_settings.FirstRun)
        {
            _settings.FirstRun = false;
            TrySave();
            Notify("CInCot is running. Press PrtSc to capture; Ctrl+PrtSc for full screen, Alt+PrtSc for the active window.", ToolTipIcon.Info, force: true);
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Capture region\tPrtSc", null, (_, _) => StartCapture(CaptureMode.Region, true));
        menu.Items.Add("Capture full screen\tCtrl+PrtSc", null, (_, _) => StartCapture(CaptureMode.FullScreen, true));
        menu.Items.Add("Capture active window\tAlt+PrtSc", null, (_, _) => StartCapture(CaptureMode.ActiveWindow, true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open screenshots folder", null, (_, _) => OpenSaveFolder());
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());

        _pauseItem = new ToolStripMenuItem("Pause PrtSc hotkeys") { CheckOnClick = true };
        _pauseItem.CheckedChanged += (_, _) => _hook.Enabled = !_pauseItem.Checked;
        menu.Items.Add(_pauseItem);

        var startup = new ToolStripMenuItem("Start with Windows") { Checked = IsStartupEnabled(), CheckOnClick = true };
        startup.CheckedChanged += (_, _) => SetStartup(startup.Checked);
        menu.Items.Add(startup);

        menu.Items.Add("Windows Print Screen setting…", null, (_, _) => Shell("ms-settings:easeofaccess-keyboard"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        return menu;
    }

    private async void StartCapture(CaptureMode mode, bool fromMenu)
    {
        if (_capturing || _overlay.IsActive) return;
        _capturing = true;
        try
        {
            // Let the tray menu fade out so it is not in the screenshot.
            if (fromMenu) await Task.Delay(250);
            if (mode == CaptureMode.Scroll)
            {
                Notify("Scroll capture is planned for a later version; starting a region capture instead.", ToolTipIcon.Info);
                mode = CaptureMode.Region;
            }
            var watch = Stopwatch.StartNew();
            var frame = ScreenCapture.Capture(_dxgi);
            long captured = watch.ElapsedMilliseconds;
            _overlay.Begin(frame, mode);
            _overlay.Update();
            Log.Trace($"{mode}: capture {captured} ms, overlay painted {watch.ElapsedMilliseconds} ms, {frame.Image.Width}x{frame.Image.Height}");
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            Notify("Capture failed: " + ex.Message, ToolTipIcon.Error, force: true);
        }
        finally
        {
            _capturing = false;
        }
    }

    private void OnCaptureCompleted(object? sender, OverlayResult result)
    {
        switch (result.Outcome)
        {
            case OverlayOutcome.Copied:
                Notify($"Copied {result.Size.Width} × {result.Size.Height} to the clipboard.", ToolTipIcon.None);
                break;
            case OverlayOutcome.Saved:
                _lastSavedPath = result.Path;
                Notify($"Saved {Path.GetFileName(result.Path)}" + (_settings.CopyWhenSaving ? " and copied it." : ".") + " Click to show it.", ToolTipIcon.None);
                break;
            case OverlayOutcome.Failed:
                Notify("Export failed: " + result.Error, ToolTipIcon.Error, force: true);
                break;
        }
        // The frozen frame can be hundreds of MB on multi-4K setups; give it back right away.
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Monitors added, removed, rescaled or rotated: rebuild the duplication sessions.
        _ui.Post(_ =>
        {
            _dxgi?.Dispose();
            _dxgi = DxgiCapture.TryCreate();
        }, null);
    }

    private void Notify(string text, ToolTipIcon icon, bool force = false)
    {
        if (!force && !_settings.ShowNotifications) return;
        _tray.ShowBalloonTip(3000, "CInCot", text, icon);
    }

    private void ShowSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }
        _settingsForm = new SettingsForm(_settings);
        _settingsForm.FormClosed += (_, _) => _settingsForm = null;
        _settingsForm.Show();
    }

    private void OpenSaveFolder()
    {
        var folder = Environment.ExpandEnvironmentVariables(_settings.SaveFolder);
        Directory.CreateDirectory(folder);
        Shell(folder);
    }

    private void RevealLastSaved()
    {
        if (_lastSavedPath is not null && File.Exists(_lastSavedPath))
            Process.Start("explorer.exe", $"/select,\"{_lastSavedPath}\"");
    }

    private static void Shell(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    }

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("CInCot") is string;
    }

    private static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled && Environment.ProcessPath is { } exe) key.SetValue("CInCot", $"\"{exe}\"");
        else key.DeleteValue("CInCot", throwOnMissingValue: false);
    }

    private void TrySave()
    {
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    }

    protected override void ExitThreadCore()
    {
        _signalWait.Unregister(null);
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _hook.Dispose();
        _dxgi?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _settingsForm?.Close();
        _overlay.Dispose();
        base.ExitThreadCore();
    }
}
