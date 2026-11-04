using System.ComponentModel;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Sikirincot.Native;

namespace Sikirincot.Capture;

/// <summary>
/// A 32-bit top-down DIB section that is both a GDI+ <see cref="System.Drawing.Bitmap"/> and a GDI memory
/// DC over the same pixels: effects write through GDI+/pointers, and painting uses plain BitBlt, which is
/// several times faster than GDI+ DrawImage for full-desktop frames.
/// </summary>
internal sealed class DibBitmap : IDisposable
{
    private readonly IntPtr _hbitmap;
    private readonly IntPtr _previous;

    public DibBitmap(int width, int height)
    {
        var header = new Win32.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
        };
        _hbitmap = Win32.CreateDIBSection(IntPtr.Zero, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (_hbitmap == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateDIBSection failed.");
        Dc = Win32.CreateCompatibleDC(IntPtr.Zero);
        _previous = Win32.SelectObject(Dc, _hbitmap);
        Bitmap = new Bitmap(width, height, width * 4, PixelFormat.Format32bppRgb, bits);
    }

    public Bitmap Bitmap { get; }
    public IntPtr Dc { get; }
    public Size Size => Bitmap.Size;

    /// <summary>Copies <paramref name="area"/> of this bitmap to the same position on <paramref name="hdc"/>.</summary>
    public void BlitTo(IntPtr hdc, Rectangle area) =>
        Win32.BitBlt(hdc, area.X, area.Y, area.Width, area.Height, Dc, area.X, area.Y, Win32.SRCCOPY);

    public void Dispose()
    {
        Bitmap.Dispose();
        Win32.SelectObject(Dc, _previous);
        Win32.DeleteDC(Dc);
        Win32.DeleteObject(_hbitmap);
    }
}
