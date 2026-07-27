using System.Threading;
using Vortice.Direct3D11;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace HdrCapture;

/// <summary>
/// A persistent Windows Graphics Capture session on a single window, used by the scrolling
/// (long screenshot) mode. Window capture sees the target even beneath the frozen overlay and
/// never includes our own windows.
/// </summary>
internal sealed class ScrollCaptureSession : IDisposable
{
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;
    private readonly AutoResetEvent _frameArrived = new(false);
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;

    public SizeInt32 ItemSize { get; }

    public ScrollCaptureSession(nint hwnd)
    {
        var item = MonitorCapture.CreateForWindow(hwnd);
        var (device, context, direct3D) = GraphicsCaptureService.GetSharedDevice();
        _device = device;
        _context = context;
        ItemSize = item.Size;
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            direct3D, DirectXPixelFormat.R16G16B16A16Float, 2, item.Size);
        _framePool.FrameArrived += (_, _) => _frameArrived.Set();
        _session = _framePool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = false;
        try { _session.IsBorderRequired = false; } catch { /* older OS or policy */ }
        _session.StartCapture();
    }

    /// <summary>Returns the newest available frame, or null on timeout or window resize.</summary>
    public HdrFrame? TryGrabLatest(TimeSpan timeout)
    {
        var frame = DrainToLatest();
        if (frame is null)
        {
            if (!_frameArrived.WaitOne(timeout)) return null;
            frame = DrainToLatest();
            if (frame is null) return null;
        }
        using (frame)
        {
            if (frame.ContentSize.Width != ItemSize.Width || frame.ContentSize.Height != ItemSize.Height)
                return null; // The window was resized; stitching coordinates are no longer valid.
            return GraphicsCaptureService.ReadBack(frame.Surface, _device, _context, frame.ContentSize);
        }
    }

    private Direct3D11CaptureFrame? DrainToLatest()
    {
        Direct3D11CaptureFrame? newest = null;
        var next = _framePool.TryGetNextFrame();
        while (next is not null)
        {
            newest?.Dispose();
            newest = next;
            next = _framePool.TryGetNextFrame();
        }
        return newest;
    }

    public void Dispose()
    {
        _session.Dispose();
        _framePool.Dispose();
        _frameArrived.Dispose();
    }
}
