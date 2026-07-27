using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace HdrCapture;

/// <summary>
/// A borderless native window that presents the captured scRGB frame through an FP16 flip-model
/// swapchain. Because the frame is DWM's own composition output, presenting it back in the
/// scRGB color space reproduces the frozen desktop pixel-for-pixel — SDR content at its normal
/// brightness and HDR highlights at their real luminance. The WPF overlay (dim, selection,
/// annotations, toolbar) sits in a transparent window directly above this one.
/// </summary>
internal sealed class HdrBackdropWindow : IDisposable
{
    private const string WindowClassName = "KirariBackdrop";

    // The window procedure delegate must outlive every window of this class.
    private static readonly NativeMethods.WindowProc WndProc = static (hwnd, message, wParam, lParam) =>
        NativeMethods.DefWindowProcW(hwnd, message, wParam, lParam);
    private static ushort _classAtom;

    /// <summary>Linear keep factor of the dim; equivalent to the SDR path's alpha-190 black.</summary>
    private const float DimKeepFactor = 65f / 255f;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGISwapChain1 _swapChain;
    private ID3D11Texture2D? _frameTexture;
    private ID3D11Texture2D? _dimmedTexture;
    private System.Windows.Int32Rect? _lastHole = new System.Windows.Int32Rect(-1, -1, -1, -1);
    private nint _hwnd;

    public nint Hwnd => _hwnd;

    /// <summary>Deferred until the overlay is about to appear, so both show in the same beat.</summary>
    public void Show() => NativeMethods.ShowWindow(_hwnd, NativeMethods.SwShowNoActivate);

    public static HdrBackdropWindow? TryCreate(HdrFrame frame, RECT monitor)
    {
        try
        {
            return new HdrBackdropWindow(frame, monitor);
        }
        catch
        {
            // Any failure (device, swapchain, color space) falls back to the SDR preview path.
            return null;
        }
    }

    private HdrBackdropWindow(HdrFrame frame, RECT monitor)
    {
        EnsureWindowClass();
        _hwnd = NativeMethods.CreateWindowExW(
            NativeMethods.WsExToolWindowStyle | NativeMethods.WsExNoActivate | NativeMethods.WsExTopmost,
            _classAtom, null, NativeMethods.WsPopup,
            monitor.Left, monitor.Top, monitor.Width, monitor.Height,
            0, 0, NativeMethods.GetModuleHandleW(null), 0);
        if (_hwnd == 0)
            throw new InvalidOperationException("Backdrop window creation failed.");

        try
        {
            D3D11.D3D11CreateDevice(null!, DriverType.Hardware, DeviceCreationFlags.BgraSupport, null!,
                out _device, out _context).CheckError();
            using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            using var factory = adapter.GetParent<IDXGIFactory2>();

            _swapChain = factory.CreateSwapChainForHwnd(_device, _hwnd, new SwapChainDescription1
            {
                Width = (uint)monitor.Width,
                Height = (uint)monitor.Height,
                Format = Format.R16G16B16A16_Float,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = AlphaMode.Ignore,
            });

            using (var swapChain3 = _swapChain.QueryInterface<IDXGISwapChain3>())
                swapChain3.SetColorSpace1(ColorSpaceType.RgbFullG10NoneP709);

            CreateTextures(frame, monitor);
            // The dim lives in this window: the first presented frame is already the dimmed
            // frozen desktop, so no matter when the WPF overlay's first frame lands, the
            // screen shows the final look from the very first composition — no flicker.
            // Presenting BEFORE the window is shown means it appears with content latched.
            PresentDim(null, throwOnFailure: true);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void CreateTextures(HdrFrame frame, RECT monitor)
    {
        // The overlay's coordinate mapping assumes the frame covers the monitor exactly, and
        // Scaling.Stretch cannot stretch a partial copy (buffer size equals window size), so a
        // mismatch (e.g. a resolution change mid-capture) falls back to the SDR preview.
        if (frame.Width != monitor.Width || frame.Height != monitor.Height)
            throw new InvalidOperationException("Capture frame does not match the monitor size.");

        var dimmed = new Half[frame.Pixels.Length];
        var width = frame.Width;
        System.Threading.Tasks.Parallel.For(0, frame.Height, y =>
        {
            var row = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var p = row + x * 4;
                dimmed[p] = (Half)((float)frame.Pixels[p] * DimKeepFactor);
                dimmed[p + 1] = (Half)((float)frame.Pixels[p + 1] * DimKeepFactor);
                dimmed[p + 2] = (Half)((float)frame.Pixels[p + 2] * DimKeepFactor);
                dimmed[p + 3] = frame.Pixels[p + 3];
            }
        });

        _frameTexture = CreateTexture(frame.Pixels, frame.Width, frame.Height);
        _dimmedTexture = CreateTexture(dimmed, frame.Width, frame.Height);
    }

    private ID3D11Texture2D CreateTexture(Half[] pixels, int width, int height)
    {
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            var description = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R16G16B16A16_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            };
            var initialData = new SubresourceData(handle.AddrOfPinnedObject(), (uint)(width * 8));
            return _device.CreateTexture2D(description, new[] { initialData });
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// Presents the dimmed frame with an undimmed hole (the selection or hovered window).
    /// Called by the overlay whenever the highlight changes; cheap GPU region copies.
    /// </summary>
    public void PresentDim(System.Windows.Int32Rect? holeFramePixels, bool throwOnFailure = false)
    {
        if (Nullable.Equals(holeFramePixels, _lastHole)) return;
        try
        {
            using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            _context.CopyResource(backBuffer, _dimmedTexture!);
            if (holeFramePixels is { Width: > 0, Height: > 0 } hole)
            {
                var box = new Box(hole.X, hole.Y, 0, hole.X + hole.Width, hole.Y + hole.Height, 1);
                _context.CopySubresourceRegion(backBuffer, 0, (uint)hole.X, (uint)hole.Y, 0, _frameTexture!, 0, box);
            }
            _swapChain.Present(0, PresentFlags.None).CheckError();
            _lastHole = holeFramePixels;
        }
        catch when (!throwOnFailure)
        {
            // Mid-session present failure only degrades the highlight; keep the session alive.
        }
    }

    private static void EnsureWindowClass()
    {
        if (_classAtom != 0) return;
        var windowClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProc),
            hInstance = NativeMethods.GetModuleHandleW(null),
            lpszClassName = WindowClassName,
        };
        _classAtom = NativeMethods.RegisterClassExW(ref windowClass);
        if (_classAtom == 0)
            throw new InvalidOperationException("Backdrop window class registration failed.");
    }

    public void Dispose()
    {
        _frameTexture?.Dispose();
        _dimmedTexture?.Dispose();
        _swapChain?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        if (_hwnd != 0)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = 0;
        }
    }
}
