using System.Runtime;
using System.Runtime.InteropServices;

namespace HdrCapture;

/// <summary>
/// Returns memory to the OS after a burst of work. Capture sessions allocate hundreds of MB of
/// large-object-heap buffers (HDR frames, previews, annotation rasters) that the GC retains for
/// reuse; for a background tray app, compacting and trimming the working set after each session
/// keeps idle memory low.
/// </summary>
internal static class MemoryTrimmer
{
    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(nint process, nint minimumSize, nint maximumSize);

    public static void Trim()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
    }
}
