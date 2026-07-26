using System.Runtime.InteropServices;
using System.Windows;

namespace HdrCapture;

/// <summary>A candidate window for auto-detection: its region in capture-frame pixels, its handle, and its raw screen bounds.</summary>
internal readonly record struct DetectedWindow(Int32Rect FrameRect, nint Hwnd, RECT ScreenBounds);

/// <summary>
/// Enumerates visible top-level windows on the captured monitor, topmost first, mapping each
/// physical-pixel screen rectangle into capture-frame pixel coordinates for hover highlighting.
/// </summary>
internal static class WindowEnumerator
{
    public static List<DetectedWindow> Enumerate(RECT monitor, int frameWidth, int frameHeight, nint overlayHwnd)
    {
        var result = new List<DetectedWindow>();
        var monitorWidth = monitor.Width;
        var monitorHeight = monitor.Height;
        if (monitorWidth <= 0 || monitorHeight <= 0 || frameWidth <= 0 || frameHeight <= 0)
            return result;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (hwnd == overlayHwnd) return true;
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            if (NativeMethods.IsIconic(hwnd)) return true;
            if (IsCloaked(hwnd)) return true;
            if ((NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64() & NativeMethods.WsExToolWindow) != 0)
                return true;
            if (!TryGetBounds(hwnd, out var bounds)) return true;

            // Clip to the captured monitor so highlights never extend past the frozen frame.
            var left = Math.Max(bounds.Left, monitor.Left);
            var top = Math.Max(bounds.Top, monitor.Top);
            var right = Math.Min(bounds.Right, monitor.Right);
            var bottom = Math.Min(bounds.Bottom, monitor.Bottom);
            if (right - left < 8 || bottom - top < 8) return true;

            var frameX = (int)Math.Round((left - monitor.Left) * (double)frameWidth / monitorWidth);
            var frameY = (int)Math.Round((top - monitor.Top) * (double)frameHeight / monitorHeight);
            var frameW = (int)Math.Round((right - left) * (double)frameWidth / monitorWidth);
            var frameH = (int)Math.Round((bottom - top) * (double)frameHeight / monitorHeight);
            frameX = Math.Clamp(frameX, 0, frameWidth);
            frameY = Math.Clamp(frameY, 0, frameHeight);
            frameW = Math.Clamp(frameW, 0, frameWidth - frameX);
            frameH = Math.Clamp(frameH, 0, frameHeight - frameY);
            if (frameW >= 4 && frameH >= 4)
                result.Add(new DetectedWindow(new Int32Rect(frameX, frameY, frameW, frameH), hwnd, bounds));
            return true;
        }, 0);

        return result;
    }

    private static bool TryGetBounds(nint hwnd, out RECT rect)
    {
        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DwmwaExtendedFrameBounds, out rect, Marshal.SizeOf<RECT>()) == 0
            && rect.Right > rect.Left && rect.Bottom > rect.Top)
            return true;
        return NativeMethods.GetWindowRect(hwnd, out rect) && rect.Right > rect.Left && rect.Bottom > rect.Top;
    }

    private static bool IsCloaked(nint hwnd) =>
        NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
}
