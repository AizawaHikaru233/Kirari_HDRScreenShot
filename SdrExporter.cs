using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HdrCapture;

/// <summary>
/// Plain SDR renderings of the HDR frame — the same tone map as the overlay preview and the
/// clipboard bitmap (divide by the monitor's SDR white scale, clip, sRGB-encode). PNG goes
/// through the in-house writer; JPEG uses the OS-provided WIC encoder.
/// </summary>
internal static class SdrExporter
{
    public static byte[] EncodePng(HdrFrame frame, float sdrWhiteScale) =>
        PngWriter.Encode(frame.Width, frame.Height, 8, ToSdrScanlines(frame, sdrWhiteScale), fast: false);

    public static byte[] EncodeJpg(HdrFrame frame, float sdrWhiteScale)
    {
        var source = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Rgb24, null,
            ToSdrScanlines(frame, sdrWhiteScale), frame.Width * 3);
        var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static byte[] ToSdrScanlines(HdrFrame frame, float sdrWhiteScale)
    {
        var invScale = 1f / (sdrWhiteScale > 0 ? sdrWhiteScale : 1f);
        var lut = new byte[65536];
        for (var i = 0; i < lut.Length; i++)
            lut[i] = ToSrgb((float)BitConverter.UInt16BitsToHalf((ushort)i) * invScale);

        var width = frame.Width;
        var pixels = new byte[frame.Width * frame.Height * 3];
        System.Threading.Tasks.Parallel.For(0, frame.Height, y =>
        {
            var bits = System.Runtime.InteropServices.MemoryMarshal.Cast<Half, ushort>(
                frame.Pixels.AsSpan(y * width * 4, width * 4));
            var row = y * width * 3;
            for (var x = 0; x < width; x++)
            {
                var s = x * 4;
                var d = row + x * 3;
                pixels[d] = lut[bits[s]];
                pixels[d + 1] = lut[bits[s + 1]];
                pixels[d + 2] = lut[bits[s + 2]];
            }
        });
        return pixels;
    }

    private static byte ToSrgb(float linear)
    {
        linear = float.IsFinite(linear) ? Math.Clamp(linear, 0f, 1f) : 0f;
        var srgb = linear <= 0.0031308f ? linear * 12.92f : 1.055f * MathF.Pow(linear, 1 / 2.4f) - 0.055f;
        return (byte)Math.Clamp(MathF.Round(srgb * 255), 0, 255);
    }
}
