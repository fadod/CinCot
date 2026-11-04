using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using Sikirincot.Core;
using Sikirincot.Native;

namespace Sikirincot.Capture;

internal static class ScreenCapture
{
    /// <summary>
    /// Freezes the whole virtual desktop. Windows and the foreground window are read first, before any
    /// Sikirincot window can appear, so snapping and Alt+PrtSc see what the user saw.
    /// </summary>
    public static CaptureFrame Capture(DxgiCapture? dxgi)
    {
        var virtualBounds = SystemInformation.VirtualScreen;
        var foreground = Win32.GetForegroundWindow();
        var windows = VisibleWindows(virtualBounds);
        Rectangle? foregroundBounds = null;
        if (foreground != IntPtr.Zero && BoundsOf(foreground) is { } fb)
        {
            var clipped = Rectangle.Intersect(fb, virtualBounds);
            if (clipped.Width > 1 && clipped.Height > 1)
            {
                clipped.Offset(-virtualBounds.X, -virtualBounds.Y);
                foregroundBounds = clipped;
            }
        }

        var pixels = new DibBitmap(virtualBounds.Width, virtualBounds.Height);
        bool viaDxgi = false;
        try
        {
            viaDxgi = dxgi?.TryCopyTo(pixels.Bitmap, virtualBounds) == true;
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
        if (!viaDxgi)
        {
            var screen = Win32.GetDC(IntPtr.Zero);
            try
            {
                Win32.BitBlt(pixels.Dc, 0, 0, virtualBounds.Width, virtualBounds.Height, screen, virtualBounds.X, virtualBounds.Y, Win32.SRCCOPY);
            }
            finally
            {
                Win32.ReleaseDC(IntPtr.Zero, screen);
            }
        }
        Log.Trace(viaDxgi ? "captured via DXGI" : "captured via GDI");

        var monitors = Screen.AllScreens.Select(s =>
        {
            var b = s.Bounds;
            float scale = Win32.ScaleAt(new Point(b.X + b.Width / 2, b.Y + b.Height / 2));
            b.Offset(-virtualBounds.X, -virtualBounds.Y);
            return new MonitorInfo(b, scale);
        }).ToList();

        return new CaptureFrame(pixels, virtualBounds, windows, monitors, foregroundBounds);
    }

    private static List<Rectangle> VisibleWindows(Rectangle virtualBounds)
    {
        var result = new List<Rectangle>();
        uint ownPid = (uint)Environment.ProcessId;
        var className = new StringBuilder(64);
        Win32.EnumWindows((hwnd, _) =>
        {
            if (!Win32.IsWindowVisible(hwnd) || Win32.IsIconic(hwnd)) return true;
            Win32.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == ownPid) return true;
            if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if ((Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE) & Win32.WS_EX_TRANSPARENT) != 0) return true;
            className.Clear();
            Win32.GetClassName(hwnd, className, className.Capacity);
            var cls = className.ToString();
            if (cls is "Progman" or "WorkerW") return true;
            if (BoundsOf(hwnd) is not { } bounds) return true;
            var r = Rectangle.Intersect(bounds, virtualBounds);
            if (r.Width < 24 || r.Height < 24) return true;
            r.Offset(-virtualBounds.X, -virtualBounds.Y);
            result.Add(r);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>Visible frame bounds (without the invisible resize borders Windows 10/11 adds).</summary>
    private static Rectangle? BoundsOf(IntPtr hwnd)
    {
        if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out Win32.RECT rect, Marshal.SizeOf<Win32.RECT>()) == 0)
            return rect.ToRectangle();
        return Win32.GetWindowRect(hwnd, out rect) ? rect.ToRectangle() : null;
    }
}
