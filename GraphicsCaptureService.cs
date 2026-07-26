using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinRT;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace HdrCapture;

internal sealed record HdrFrame
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required Half[] Pixels { get; init; }
    /// <summary>Color volume of the source display, when known; used for mDCV metadata.</summary>
    public DisplayMetadata? Display { get; init; }
}

internal static class GraphicsCaptureService
{
    private static readonly Guid Direct3DDxgiInterfaceAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    // Cached across captures: creating a D3D device costs tens of milliseconds and was a
    // noticeable part of hotkey-to-overlay latency. Captures are serialized by the caller.
    private static ID3D11Device? _device;
    private static ID3D11DeviceContext? _context;
    private static IDirect3DDevice? _direct3DDevice;

    [DllImport("d3d11.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    internal static (ID3D11Device Device, ID3D11DeviceContext Context, IDirect3DDevice Direct3D) GetSharedDevice()
    {
        if (_device is not null && _device.DeviceRemovedReason.Success)
            return (_device, _context!, _direct3DDevice!);

        _direct3DDevice?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        D3D11.D3D11CreateDevice(
            null!,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            null!,
            out _device,
            out _context).CheckError();
        _direct3DDevice = CreateDirect3DDevice(_device);
        return (_device, _context!, _direct3DDevice);
    }

    public static HdrFrame CaptureOneFrame(GraphicsCaptureItem item, bool captureCursor, bool hideBorder = false)
    {
        var (device, context, direct3DDevice) = GetSharedDevice();

        using (var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            direct3DDevice,
            DirectXPixelFormat.R16G16B16A16Float,
            2,
            item.Size))
        using (var session = framePool.CreateCaptureSession(item))
        using (var ready = new AutoResetEvent(false))
        {
            session.IsCursorCaptureEnabled = captureCursor;
            if (hideBorder)
            {
                // Available on Windows 11; suppress the yellow capture border for the frozen overlay.
                try { session.IsBorderRequired = false; } catch { /* older OS or policy: ignore */ }
            }
            framePool.FrameArrived += (_, _) => ready.Set();
            session.StartCapture();
            if (!ready.WaitOne(TimeSpan.FromSeconds(3)))
                throw new TimeoutException("No Windows Graphics Capture frame arrived within three seconds.");

            using var frame = framePool.TryGetNextFrame();
            if (frame is null)
                throw new InvalidOperationException("The capture event arrived without an available frame.");
            return ReadBack(frame.Surface, device, context, frame.ContentSize);
        }
    }

    private static IDirect3DDevice CreateDirect3DDevice(ID3D11Device device)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var pointer);
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    internal static HdrFrame ReadBack(IDirect3DSurface surface, ID3D11Device device, ID3D11DeviceContext context, SizeInt32 size)
    {
        using var source = GetD3DTexture(surface);
        var desc = source.Description;
        if (desc.Format != Format.R16G16B16A16_Float)
            throw new InvalidOperationException($"The capture frame is not R16G16B16A16_FLOAT: {desc.Format}.");

        var stagingDesc = new Texture2DDescription
        {
            Width = checked((uint)size.Width),
            Height = checked((uint)size.Height),
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R16G16B16A16_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        };
        using var staging = device.CreateTexture2D(stagingDesc);
        context.CopyResource(staging, source);
        context.Flush();
        var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var pixels = new Half[size.Width * size.Height * 4];
            unsafe
            {
                var destination = MemoryMarshal.AsBytes(pixels.AsSpan());
                for (var y = 0; y < size.Height; y++)
                {
                    var sourceRow = new ReadOnlySpan<byte>((void*)((byte*)mapped.DataPointer + y * mapped.RowPitch), size.Width * 8);
                    sourceRow.CopyTo(destination.Slice(y * size.Width * 8, size.Width * 8));
                }
            }
            return new HdrFrame { Width = size.Width, Height = size.Height, Pixels = pixels };
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }

    private static unsafe ID3D11Texture2D GetD3DTexture(IDirect3DSurface surface)
    {
        var access = MarshalInspectable<object>.CreateMarshaler2(surface, Direct3DDxgiInterfaceAccessIid);
        try
        {
            var thisPtr = access.GetAbi();
            var textureIid = typeof(ID3D11Texture2D).GUID;
            nint texture = 0;
            var vtable = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>**)thisPtr;
            var result = (*vtable)[3](thisPtr, &textureIid, &texture);
            Marshal.ThrowExceptionForHR(result);
            if (texture == 0)
                throw new InvalidOperationException("IDirect3DDxgiInterfaceAccess returned no texture.");
            return new ID3D11Texture2D(texture);
        }
        finally
        {
            access.Dispose();
        }
    }
}
