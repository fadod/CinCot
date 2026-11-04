using System.Drawing.Imaging;
using Sikirincot.Core;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Sikirincot.Capture;

/// <summary>
/// DXGI Desktop Duplication, kept warm. One duplication per monitor stays open and its latest frame is
/// mirrored into a GPU texture only when a capture asks for it, so PrtSc costs one GPU copy and one
/// readback instead of a ~100 ms GDI screen blit. Any failure makes the caller fall back to GDI.
/// </summary>
internal sealed class DxgiCapture : IDisposable
{
    private sealed class Output : IDisposable
    {
        public required ID3D11Device Device { get; init; }
        public required IDXGIOutput1 Dxgi { get; init; }
        public required Rectangle Bounds { get; init; }
        public IDXGIOutputDuplication? Duplication { get; set; }
        public ID3D11Texture2D? Latest { get; set; }
        public bool Primed { get; set; }

        public void Dispose()
        {
            Duplication?.Dispose();
            Latest?.Dispose();
            Dxgi.Dispose();
        }
    }

    private readonly List<Output> _outputs = new();
    private readonly List<ID3D11Device> _devices = new();

    private DxgiCapture() { }

    public static DxgiCapture? TryCreate()
    {
        var capture = new DxgiCapture();
        try
        {
            capture.Initialize();
            if (capture._outputs.Count == Screen.AllScreens.Length) return capture;
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
        capture.Dispose();
        return null;
    }

    private void Initialize()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
            {
                ID3D11Device? device = null;
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    using (output)
                    {
                        var desc = output.Description;
                        // Rotated monitors would need a rotate pass; leave them to the GDI path.
                        if (!desc.AttachedToDesktop || desc.Rotation is not (ModeRotation.Identity or ModeRotation.Unspecified)) continue;
                        device ??= CreateDevice(adapter);
                        var r = desc.DesktopCoordinates;
                        var state = new Output
                        {
                            Device = device,
                            Dxgi = output.QueryInterface<IDXGIOutput1>(),
                            Bounds = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom),
                        };
                        _outputs.Add(state);
                        Refresh(state, 250);
                    }
                }
                if (device is not null) _devices.Add(device);
            }
        }
    }

    private static ID3D11Device CreateDevice(IDXGIAdapter1 adapter)
    {
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 }, out ID3D11Device? device).CheckError();
        return device!;
    }

    private static void Restart(Output output)
    {
        output.Duplication?.Dispose();
        output.Duplication = null;
        output.Primed = false;
        try
        {
            output.Duplication = output.Dxgi.DuplicateOutput(output.Device);
        }
        catch (SharpGenException ex)
        {
            Log.Trace("DuplicateOutput failed: " + ex.Message);
        }
    }

    /// <summary>Mirrors the newest desktop frame into <see cref="Output.Latest"/>; false if there is no valid image.</summary>
    private static bool Refresh(Output output, uint timeoutMs)
    {
        if (output.Duplication is null) Restart(output);
        if (output.Duplication is null) return false;

        var result = output.Duplication.AcquireNextFrame(timeoutMs, out var info, out var resource);
        if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code) return output.Primed; // unchanged since the last acquire
        if (result.Failure)
        {
            // Access lost: mode change, secure desktop, exclusive full-screen app. Retry once.
            Log.Trace($"DXGI acquire failed ({result.Code:X8}); restarting duplication");
            Restart(output);
            if (output.Duplication is null) return false;
            result = output.Duplication.AcquireNextFrame(timeoutMs, out info, out resource);
            if (result.Failure) return false;
        }

        try
        {
            // LastPresentTime 0 = only the mouse pointer changed. The surface then holds no new desktop image
            // and can be blank, so keep the last real frame instead of copying it.
            if (info.LastPresentTime == 0)
            {
                Log.Trace($"DXGI pointer-only frame ignored (primed {output.Primed})");
                return output.Primed;
            }

            using var texture = resource.QueryInterface<ID3D11Texture2D>();
            var desc = texture.Description;
            if (desc.Format != Format.B8G8R8A8_UNorm) return false;
            if (output.Latest is null || output.Latest.Description.Width != desc.Width || output.Latest.Description.Height != desc.Height)
            {
                output.Latest?.Dispose();
                output.Latest = output.Device.CreateTexture2D(new Texture2DDescription
                {
                    Width = desc.Width, Height = desc.Height, MipLevels = 1, ArraySize = 1, Format = desc.Format,
                    SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
                });
            }
            output.Device.ImmediateContext.CopyResource(output.Latest, texture);
            output.Primed = true;
            return true;
        }
        finally
        {
            resource.Dispose();
            output.Duplication.ReleaseFrame();
        }
    }

    /// <summary>Copies every monitor into <paramref name="target"/> (virtual-desktop sized). False means use GDI.</summary>
    public unsafe bool TryCopyTo(Bitmap target, Rectangle virtualBounds)
    {
        if (_outputs.Count != Screen.AllScreens.Length) return false;
        foreach (var output in _outputs)
            if (!Refresh(output, 0) || output.Latest is null) return false;

        var data = target.LockBits(new Rectangle(Point.Empty, target.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            foreach (var output in _outputs)
            {
                // The CPU-readable copy only exists during a capture, so it costs no memory while idle.
                var desc = output.Latest!.Description;
                using var staging = output.Device.CreateTexture2D(desc with
                {
                    Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read, BindFlags = BindFlags.None, MiscFlags = ResourceOptionFlags.None,
                });
                var context = output.Device.ImmediateContext;
                context.CopyResource(staging, output.Latest);
                var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    var area = Rectangle.Intersect(output.Bounds, virtualBounds);
                    int width = Math.Min(area.Width, (int)desc.Width);
                    int height = Math.Min(area.Height, (int)desc.Height);
                    int sx = area.X - output.Bounds.X, sy = area.Y - output.Bounds.Y;
                    int dx = area.X - virtualBounds.X, dy = area.Y - virtualBounds.Y;
                    IntPtr src = mapped.DataPointer, dst = data.Scan0;
                    long srcPitch = mapped.RowPitch, dstPitch = data.Stride, bytes = width * 4L;
                    Parallel.For(0, height, y =>
                    {
                        byte* s = (byte*)src + (sy + y) * srcPitch + sx * 4L;
                        byte* d = (byte*)dst + (dy + y) * dstPitch + dx * 4L;
                        Buffer.MemoryCopy(s, d, bytes, bytes);
                    });

                    // Safety net: a monitor that comes back completely black is almost certainly a bad frame
                    // (display waking, driver hiccup), not the real screen. Let the caller use GDI instead,
                    // and make the next capture take a fresh frame.
                    if (IsBlank((byte*)dst, dstPitch, dx, dy, width, height))
                    {
                        Log.Trace($"DXGI frame for monitor at {output.Bounds.Location} was blank; using GDI");
                        output.Primed = false;
                        return false;
                    }
                }
                finally
                {
                    context.Unmap(staging, 0);
                }
            }
        }
        finally
        {
            target.UnlockBits(data);
        }
        return true;
    }

    /// <summary>True when a 24×24 grid of samples across the area is pure black.</summary>
    internal static unsafe bool IsBlank(byte* pixels, long stride, int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return true;
        const int grid = 24;
        for (int j = 0; j < grid; j++)
        {
            byte* row = pixels + (y + (long)(height - 1) * j / (grid - 1)) * stride;
            for (int i = 0; i < grid; i++)
            {
                uint p = *(uint*)(row + (x + (long)(width - 1) * i / (grid - 1)) * 4);
                if ((p & 0x00FFFFFF) != 0) return false;
            }
        }
        return true;
    }

    public void Dispose()
    {
        foreach (var output in _outputs) output.Dispose();
        _outputs.Clear();
        foreach (var device in _devices) device.Dispose();
        _devices.Clear();
    }
}
