using System.Runtime.InteropServices;
using WinRT;
using Windows.Graphics.Capture;

namespace HdrCapture;

/// <summary>
/// Creates a <see cref="GraphicsCaptureItem"/> for a monitor without showing the system picker,
/// via the <c>IGraphicsCaptureItemInterop</c> activation-factory interface.
/// </summary>
internal static class MonitorCapture
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid GraphicsCaptureItemInteropIid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private const string GraphicsCaptureItemClassId = "Windows.Graphics.Capture.GraphicsCaptureItem";

    public static GraphicsCaptureItem CreateForMonitor(nint monitor) => CreateForHandle(monitor, vtableSlot: 4);

    public static GraphicsCaptureItem CreateForWindow(nint hwnd) => CreateForHandle(hwnd, vtableSlot: 3);

    private static unsafe GraphicsCaptureItem CreateForHandle(nint handle, int vtableSlot)
    {
        nint hstring = 0;
        nint factory = 0;
        try
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(GraphicsCaptureItemClassId, GraphicsCaptureItemClassId.Length, out hstring));
            var interopIid = GraphicsCaptureItemInteropIid;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, ref interopIid, out factory));

            var itemIid = GraphicsCaptureItemIid;
            nint itemPtr;
            // IGraphicsCaptureItemInterop : IUnknown -> [3]=CreateForWindow, [4]=CreateForMonitor.
            var vtable = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>**)factory;
            Marshal.ThrowExceptionForHR((*vtable)[vtableSlot](factory, handle, &itemIid, &itemPtr));
            try
            {
                return MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
            }
            finally
            {
                Marshal.Release(itemPtr);
            }
        }
        finally
        {
            if (factory != 0) Marshal.Release(factory);
            if (hstring != 0) WindowsDeleteString(hstring);
        }
    }

    [DllImport("combase.dll")]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out nint hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(nint hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(nint activatableClassId, ref Guid iid, out nint factory);
}
