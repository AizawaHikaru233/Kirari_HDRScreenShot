using System.Runtime.InteropServices;
using WinRT;
using Windows.Graphics.Capture;

namespace HdrCapture;

internal static class CapturePickerHelper
{
    private static readonly Guid InitializeWithWindowIid = new("3E68D4BD-7135-4D10-8018-9FB6D9F33FA1");

    public static async Task<GraphicsCaptureItem?> PickAsync(nint hwnd)
    {
        var picker = new GraphicsCapturePicker();
        InitializeWithWindow(picker, hwnd);
        return await picker.PickSingleItemAsync();
    }

    private static unsafe void InitializeWithWindow(GraphicsCapturePicker picker, nint hwnd)
    {
        var initializeWithWindow = MarshalInspectable<object>.CreateMarshaler2(picker, InitializeWithWindowIid);
        try
        {
            var thisPtr = initializeWithWindow.GetAbi();
            var vtable = (delegate* unmanaged[Stdcall]<nint, nint, int>**)thisPtr;
            var result = (*vtable)[3](thisPtr, hwnd);
            Marshal.ThrowExceptionForHR(result);
        }
        finally
        {
            initializeWithWindow.Dispose();
        }
    }
}
