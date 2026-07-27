using System.Runtime.InteropServices;
using Vortice.DXGI;

namespace HdrCapture;

/// <summary>CIE 1931 chromaticity coordinate.</summary>
internal readonly record struct DisplayChromaticity(float X, float Y);

/// <summary>Color volume, SDR white level and advanced-color state of the capture display.</summary>
internal readonly record struct DisplayMetadata(
    DisplayChromaticity Red,
    DisplayChromaticity Green,
    DisplayChromaticity Blue,
    DisplayChromaticity White,
    float MaxNits,
    float MinNits,
    float SdrWhiteNits,
    bool HdrActive);

/// <summary>Queries per-monitor display capabilities: DXGI color volume and the Windows SDR white level.</summary>
internal static class DisplayInfo
{
    public static DisplayMetadata? ForMonitor(nint monitor)
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint adapterIndex = 0; factory.EnumAdapters1(adapterIndex, out var adapter).Success; adapterIndex++)
            {
                using (adapter)
                {
                    for (uint outputIndex = 0; adapter.EnumOutputs(outputIndex, out var output).Success; outputIndex++)
                    {
                        using (output)
                        {
                            if (output.Description.Monitor != monitor) continue;
                            using var output6 = output.QueryInterfaceOrNull<IDXGIOutput6>();
                            if (output6 is null) return null;
                            var description = output6.Description1;
                            return new DisplayMetadata(
                                new DisplayChromaticity(description.RedPrimary[0], description.RedPrimary[1]),
                                new DisplayChromaticity(description.GreenPrimary[0], description.GreenPrimary[1]),
                                new DisplayChromaticity(description.BluePrimary[0], description.BluePrimary[1]),
                                new DisplayChromaticity(description.WhitePoint[0], description.WhitePoint[1]),
                                description.MaxLuminance,
                                description.MinLuminance,
                                GetSdrWhiteNits(monitor),
                                description.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020);
                        }
                    }
                }
            }
        }
        catch
        {
            // A DXGI enumeration failure only loses metadata quality; capture continues without it.
        }
        return null;
    }

    /// <summary>
    /// The monitor's SDR white level in nits (the Windows "SDR content brightness" setting).
    /// DWM composes SDR windows into scRGB scaled by this value over 80 nits, so the capture
    /// overlay must divide it back out for SDR content to match the live desktop.
    /// </summary>
    public static float GetSdrWhiteNits(nint monitor)
    {
        try
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!NativeMethods.GetMonitorInfo(monitor, ref info))
                return HdrPngExporter.SdrWhiteNits;

            if (NativeMethods.GetDisplayConfigBufferSizes(NativeMethods.QdcOnlyActivePaths, out var pathCount, out var modeCount) != 0)
                return HdrPngExporter.SdrWhiteNits;
            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            if (NativeMethods.QueryDisplayConfig(NativeMethods.QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, 0) != 0)
                return HdrPngExporter.SdrWhiteNits;

            for (var i = 0; i < pathCount; i++)
            {
                var sourceName = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = NativeMethods.DisplayConfigDeviceInfoGetSourceName,
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        adapterId = paths[i].sourceInfo.adapterId,
                        id = paths[i].sourceInfo.id,
                    },
                };
                if (NativeMethods.DisplayConfigGetDeviceInfo(ref sourceName) != 0) continue;
                if (!string.Equals(sourceName.viewGdiDeviceName, info.szDevice, StringComparison.OrdinalIgnoreCase)) continue;

                var whiteLevel = new DISPLAYCONFIG_SDR_WHITE_LEVEL
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = NativeMethods.DisplayConfigDeviceInfoGetSdrWhiteLevel,
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SDR_WHITE_LEVEL>(),
                        adapterId = paths[i].targetInfo.adapterId,
                        id = paths[i].targetInfo.id,
                    },
                };
                if (NativeMethods.DisplayConfigGetDeviceInfo(ref whiteLevel) != 0 || whiteLevel.SDRWhiteLevel == 0) break;
                return whiteLevel.SDRWhiteLevel / 1000f * 80f;
            }
        }
        catch
        {
            // Fall through to the SDR default.
        }
        return HdrPngExporter.SdrWhiteNits;
    }
}
