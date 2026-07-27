using System.Runtime.InteropServices;

namespace HdrCapture;

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MONITORINFO
{
    public int cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct MONITORINFOEX
{
    public int cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string szDevice;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LUID
{
    public uint LowPart;
    public int HighPart;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_PATH_SOURCE_INFO
{
    public LUID adapterId;
    public uint id;
    public uint modeInfoIdx;
    public uint statusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_RATIONAL
{
    public uint Numerator;
    public uint Denominator;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_PATH_TARGET_INFO
{
    public LUID adapterId;
    public uint id;
    public uint modeInfoIdx;
    public uint outputTechnology;
    public uint rotation;
    public uint scaling;
    public DISPLAYCONFIG_RATIONAL refreshRate;
    public uint scanLineOrdering;
    public int targetAvailable;
    public uint statusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_PATH_INFO
{
    public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
    public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
    public uint flags;
}

// Header plus the largest union member (DISPLAYCONFIG_TARGET_MODE, 48 bytes).
[StructLayout(LayoutKind.Sequential, Size = 64)]
internal struct DISPLAYCONFIG_MODE_INFO
{
    public uint infoType;
    public uint id;
    public LUID adapterId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_DEVICE_INFO_HEADER
{
    public uint type;
    public uint size;
    public LUID adapterId;
    public uint id;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string viewGdiDeviceName;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_SDR_WHITE_LEVEL
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    /// <summary>SDR white in multiples of 80 nits scaled by 1000 (1000 = 80 nits).</summary>
    public uint SDRWhiteLevel;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MARGINS
{
    public int Left;
    public int Right;
    public int Top;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WNDCLASSEX
{
    public uint cbSize;
    public uint style;
    public nint lpfnWndProc;
    public int cbClsExtra;
    public int cbWndExtra;
    public nint hInstance;
    public nint hIcon;
    public nint hCursor;
    public nint hbrBackground;
    [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
    [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    public nint hIconSm;
}

/// <summary>Win32/DWM entry points used for monitor selection, window detection and overlay placement.</summary>
internal static class NativeMethods
{
    public const uint MonitorDefaultToNearest = 0x00000002;
    public const int GwlExStyle = -20;
    public const long WsExToolWindow = 0x00000080;
    public const int DwmwaCloaked = 14;
    public const int DwmwaExtendedFrameBounds = 9;

    public const uint SwpNoZorder = 0x0004;
    public const uint SwpShowWindow = 0x0040;

    public const uint QdcOnlyActivePaths = 0x00000002;
    public const uint DisplayConfigDeviceInfoGetSourceName = 1;
    public const uint DisplayConfigDeviceInfoGetSdrWhiteLevel = 11;

    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoActivate = 0x0010;

    public const uint WsPopup = 0x80000000;
    public const uint WsExToolWindowStyle = 0x00000080;
    public const uint WsExNoActivate = 0x08000000;
    public const uint WsExTopmost = 0x00000008;
    public const int SwShowNoActivate = 4;

    public delegate nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam);

    public delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    public static extern nint MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(nint hwnd, out RECT rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out RECT value, int size);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    [DllImport("dwmapi.dll")]
    public static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref MARGINS margins);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(nint monitor, ref MONITORINFOEX info);

    [DllImport("user32.dll")]
    public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

    [DllImport("user32.dll")]
    public static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] DISPLAYCONFIG_PATH_INFO[] paths, ref uint numModes, [Out] DISPLAYCONFIG_MODE_INFO[] modes, nint currentTopologyId);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME request);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SDR_WHITE_LEVEL request);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WNDCLASSEX windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowExW(uint exStyle, nint classAtom, string? windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    public static extern nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern nint GetModuleHandleW(string? moduleName);

    public const uint WmMouseWheel = 0x020A;

    [DllImport("user32.dll")]
    public static extern bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    public static extern bool ScreenToClient(nint hwnd, ref POINT point);

    [DllImport("user32.dll")]
    public static extern nint RealChildWindowFromPoint(nint hwnd, POINT point);

    /// <summary>
    /// Posts wheel scroll notches to the deepest child window at the given screen point.
    /// WM_MOUSEWHEEL propagates parent-ward only, so sending to the top-level frame is ignored
    /// by multi-HWND apps (Explorer lists, Edit controls); Post avoids blocking on hung targets.
    /// </summary>
    public static void SendMouseWheel(nint topLevel, int screenX, int screenY, int notches)
    {
        var target = DeepChildAtPoint(topLevel, screenX, screenY);
        var wParam = (nint)(notches * -120 << 16);
        var lParam = (nint)((screenY << 16) | (screenX & 0xFFFF));
        PostMessageW(target, WmMouseWheel, wParam, lParam);
    }

    private static nint DeepChildAtPoint(nint topLevel, int screenX, int screenY)
    {
        var hwnd = topLevel;
        for (var depth = 0; depth < 16; depth++)
        {
            var point = new POINT { X = screenX, Y = screenY };
            if (!ScreenToClient(hwnd, ref point)) break;
            var child = RealChildWindowFromPoint(hwnd, point);
            if (child == 0 || child == hwnd) break;
            hwnd = child;
        }
        return hwnd;
    }

    public static (nint Monitor, RECT Bounds) MonitorUnderCursor()
    {
        GetCursorPos(out var point);
        var monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        return (monitor, info.rcMonitor);
    }
}
