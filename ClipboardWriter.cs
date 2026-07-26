using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HdrCapture;

/// <summary>
/// Puts the capture on the clipboard in two formats: a tone-mapped SDR bitmap (CF_DIB — the
/// only thing bitmap-oriented consumers understand), and the full single-file HDR PNG bytes
/// under the "PNG" format — Chromium-family paste targets (Discord, Chrome, Telegram web)
/// prefer that entry and receive the HDR file intact, cICP/ICC signaling included.
/// </summary>
internal static class ClipboardWriter
{
    public static void CopySdr(HdrFrame frame, float sdrWhiteScale, byte[]? hdrPng = null)
    {
        var invScale = 1f / (sdrWhiteScale > 0 ? sdrWhiteScale : 1f);
        var pixels = new byte[frame.Width * frame.Height * 4];
        for (var pixel = 0; pixel < frame.Width * frame.Height; pixel++)
        {
            var source = pixel * 4;
            pixels[source] = ToSrgb((float)frame.Pixels[source + 2] * invScale);
            pixels[source + 1] = ToSrgb((float)frame.Pixels[source + 1] * invScale);
            pixels[source + 2] = ToSrgb((float)frame.Pixels[source] * invScale);
            pixels[source + 3] = 255;
        }

        var bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), pixels, frame.Width * 4, 0);
        bitmap.Freeze();

        MemoryStream png;
        if (hdrPng is not null)
        {
            png = new MemoryStream(hdrPng);
        }
        else
        {
            png = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(png);
            png.Position = 0;
        }

        var data = new DataObject();
        data.SetImage(bitmap);
        data.SetData("PNG", png, autoConvert: false);

        // The clipboard can be transiently locked by other processes; retry briefly.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return;
            }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 3)
            {
                Thread.Sleep(80);
            }
        }
    }

    private static byte ToSrgb(float linear)
    {
        linear = float.IsFinite(linear) ? Math.Clamp(linear, 0f, 1f) : 0f;
        var srgb = linear <= 0.0031308f ? linear * 12.92f : 1.055f * MathF.Pow(linear, 1 / 2.4f) - 0.055f;
        return (byte)Math.Clamp(MathF.Round(srgb * 255), 0, 255);
    }
}
