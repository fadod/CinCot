namespace CInCot.Capture;

internal enum CaptureMode { Region, FullScreen, ActiveWindow, Scroll }

/// <summary>A monitor in frame coordinates (physical pixels, origin at the virtual desktop's top-left).</summary>
internal readonly record struct MonitorInfo(Rectangle Bounds, float Scale);

/// <summary>
/// One frozen copy of the whole virtual desktop plus what the overlay needs to snap to:
/// visible windows in z-order and the monitor layout. All rectangles are in frame coordinates.
/// </summary>
internal sealed class CaptureFrame : IDisposable
{
    public CaptureFrame(DibBitmap pixels, Rectangle virtualBounds, IReadOnlyList<Rectangle> windows,
        IReadOnlyList<MonitorInfo> monitors, Rectangle? foregroundBounds)
    {
        Pixels = pixels;
        VirtualBounds = virtualBounds;
        Windows = windows;
        Monitors = monitors;
        ForegroundBounds = foregroundBounds;
    }

    public DibBitmap Pixels { get; }
    public Bitmap Image => Pixels.Bitmap;
    public Rectangle VirtualBounds { get; }
    public IReadOnlyList<Rectangle> Windows { get; }
    public IReadOnlyList<MonitorInfo> Monitors { get; }
    public Rectangle? ForegroundBounds { get; }
    public Rectangle Bounds => new(Point.Empty, Image.Size);

    /// <summary>The topmost visible window under a point, or null over the bare desktop.</summary>
    public Rectangle? WindowAt(Point p)
    {
        foreach (var w in Windows)
            if (w.Contains(p)) return w;
        return null;
    }

    public MonitorInfo MonitorAt(Point p)
    {
        foreach (var m in Monitors)
            if (m.Bounds.Contains(p)) return m;
        MonitorInfo nearest = Monitors[0];
        long best = long.MaxValue;
        foreach (var m in Monitors)
        {
            long dx = Math.Max(0, Math.Max(m.Bounds.Left - p.X, p.X - m.Bounds.Right));
            long dy = Math.Max(0, Math.Max(m.Bounds.Top - p.Y, p.Y - m.Bounds.Bottom));
            if (dx * dx + dy * dy < best) { best = dx * dx + dy * dy; nearest = m; }
        }
        return nearest;
    }

    public void Dispose() => Pixels.Dispose();
}
