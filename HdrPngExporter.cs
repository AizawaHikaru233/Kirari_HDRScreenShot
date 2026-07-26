using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace HdrCapture;

/// <summary>SpecialK-style HDR PNG: linear scRGB -> Rec.2020 -> ST.2084 PQ -> 16-bit PNG with HDR signaling.</summary>
internal static class HdrPngExporter
{
    /// <summary>scRGB 1.0 is the SDR reference white level.</summary>
    public const float SdrWhiteNits = 80f;
    // ST.2084 is normalized to 10,000 nits.
    private const float ScRgbToPqNormalization = SdrWhiteNits / 10_000f;
    // The PQ ceiling in scRGB units: nothing above this survives encoding.
    private const float PqPeakOverSdr = 10_000f / SdrWhiteNits;

    /// <summary>Writes the single-file HDR PNG and returns the content peak luminance (MaxCLL) in nits.</summary>
    public static float Export(HdrFrame frame, string outputPath)
    {
        var (data, maxCllNits) = Encode(frame);
        File.WriteAllBytes(outputPath, data);
        return maxCllNits;
    }

    // Half bit pattern -> 10-in-16-bit PQ code. Replaces two MathF.Pow calls per channel with
    // one table lookup; the Half round-trip's ~0.05% relative error is far below a PQ code step.
    private static readonly ushort[] PqCodeLut = BuildPqCodeLut();

    private static ushort[] BuildPqCodeLut()
    {
        var lut = new ushort[65536];
        for (var i = 0; i < lut.Length; i++)
        {
            var value = (float)BitConverter.UInt16BitsToHalf((ushort)i);
            lut[i] = float.IsFinite(value) && value > 0
                ? ToPqU16(Math.Clamp(value, 0f, PqPeakOverSdr) * ScRgbToPqNormalization)
                : (ushort)0;
        }
        return lut;
    }

    /// <summary>Encodes the single-file HDR PNG in memory (also used for the clipboard "PNG" format).</summary>
    public static (byte[] Data, float MaxCllNits) Encode(HdrFrame frame, bool fast = false)
    {
        // Raw 16-bit big-endian RGB scanlines for the in-house PNG writer.
        var scanlines = new byte[frame.Width * frame.Height * 6];
        var histogram = new int[4096];
        var totalPeak = 0.0;
        var gate = new object();
        var width = frame.Width;
        System.Threading.Tasks.Parallel.For(0, frame.Height,
            static () => (Hist: new int[4096], Sum: 0.0),
            (y, _, local) =>
            {
                var row = y * width;
                for (var x = 0; x < width; x++)
                {
                    var source = (row + x) * 4;
                    // Keep finite negative components: scRGB encodes out-of-709 wide-gamut
                    // colors with negative values, which the matrix maps into valid Rec.2020.
                    var red = SanitizeFinite((float)frame.Pixels[source]);
                    var green = SanitizeFinite((float)frame.Pixels[source + 1]);
                    var blue = SanitizeFinite((float)frame.Pixels[source + 2]);
                    var rec2020Red = 0.6274039f * red + 0.3292830f * green + 0.0433131f * blue;
                    var rec2020Green = 0.0690973f * red + 0.9195404f * green + 0.0113623f * blue;
                    var rec2020Blue = 0.0163914f * red + 0.0880133f * green + 0.8955953f * blue;
                    // Clamp first so cLLI/mDCV statistics describe the coded signal.
                    var encodedRed = Math.Clamp(rec2020Red, 0f, PqPeakOverSdr);
                    var encodedGreen = Math.Clamp(rec2020Green, 0f, PqPeakOverSdr);
                    var encodedBlue = Math.Clamp(rec2020Blue, 0f, PqPeakOverSdr);
                    var pixelPeak = MathF.Max(encodedRed, MathF.Max(encodedGreen, encodedBlue));
                    local.Hist[(int)MathF.Min(4095f, pixelPeak * (4095f / PqPeakOverSdr))]++;
                    local.Sum += pixelPeak;
                    var d = (row + x) * 6;
                    var codeR = PqCodeLut[BitConverter.HalfToUInt16Bits((Half)encodedRed)];
                    var codeG = PqCodeLut[BitConverter.HalfToUInt16Bits((Half)encodedGreen)];
                    var codeB = PqCodeLut[BitConverter.HalfToUInt16Bits((Half)encodedBlue)];
                    scanlines[d] = (byte)(codeR >> 8);
                    scanlines[d + 1] = (byte)codeR;
                    scanlines[d + 2] = (byte)(codeG >> 8);
                    scanlines[d + 3] = (byte)codeG;
                    scanlines[d + 4] = (byte)(codeB >> 8);
                    scanlines[d + 5] = (byte)codeB;
                }
                return local;
            },
            local =>
            {
                lock (gate)
                {
                    for (var i = 0; i < histogram.Length; i++)
                        histogram[i] += local.Hist[i];
                    totalPeak += local.Sum;
                }
            });

        // CTA-861.3 content light levels (per-pixel max component, scRGB 1.0 = 80 nits).
        // Chromium scales its SDR tone map by MaxCLL, so a lone specular pixel would dim the
        // whole image; report the 99.99th-percentile peak instead (ledoge/SpecialK approach).
        var pixelCount = frame.Width * frame.Height;
        var maxCllNits = totalPeak <= 0 ? 0f : PercentileNits(histogram, pixelCount);
        var maxFallNits = (float)(totalPeak / pixelCount) * SdrWhiteNits;

        var png = PngWriter.Encode(frame.Width, frame.Height, 16, scanlines, fast);
        return (HdrPngMetadata.AddHdrSignaling(png, maxCllNits, maxFallNits, frame.Display), maxCllNits);
    }

    private static float Pq(float linear)
    {
        linear = Math.Clamp(linear, 0, 1);
        const float m1 = 2610f / 16384f;
        const float m2 = 2523f / 32f;
        const float c1 = 3424f / 4096f;
        const float c2 = 2413f / 128f;
        const float c3 = 2392f / 128f;
        var powered = MathF.Pow(linear, m1);
        return MathF.Pow((c1 + c2 * powered) / (1f + c3 * powered), m2);
    }

    private static ushort ToPqU16(float linear)
    {
        // ReShade declares sBIT=10 and stores that normalized 10-bit PQ signal in a 16-bit PNG channel.
        var code10 = Math.Clamp(MathF.Round(Pq(linear) * 1023f), 0, 1023);
        return (ushort)Math.Clamp(MathF.Round(code10 / 1023f * ushort.MaxValue), 0, ushort.MaxValue);
    }
    private static float SanitizeFinite(float value) => float.IsFinite(value) ? value : 0;

    private static float PercentileNits(int[] histogram, int pixelCount)
    {
        var threshold = Math.Max(1, (int)(pixelCount * 0.0001));
        var seen = 0;
        for (var bin = histogram.Length - 1; bin >= 0; bin--)
        {
            seen += histogram[bin];
            if (seen >= threshold)
                return (bin + 1) / (float)histogram.Length * PqPeakOverSdr * SdrWhiteNits;
        }
        return 0f;
    }
}

internal static class HdrPngMetadata
{
    private static readonly byte[] Signature = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
    private static readonly byte[] SignificantBits = new byte[] { 10, 10, 10 };
    private static readonly byte[] Cicp = new byte[] { 9, 16, 0, 1 }; // BT.2020, PQ, RGB, full range
    private static readonly byte[] Rec2020Chromaticities = new byte[] { 0, 0, 122, 38, 0, 0, 128, 132, 0, 1, 20, 144, 0, 0, 114, 16, 0, 0, 66, 104, 0, 1, 55, 84, 0, 0, 51, 44, 0, 0, 17, 248 };

    // The generated Rec.2020 PQ ICC profile is anchored at the capture monitor's SDR white
    // level, so iCCP-honoring SDR viewers reproduce the overlay's SDR-referenced clipped look.

    // Rec.2020 primaries in mDCV R,G,B order followed by the D65 white point, in 0.00002 units.
    private static readonly ushort[] MdcvRec2020Chromaticities = { 35400, 14600, 8500, 39850, 6550, 2300, 15635, 16450 };

    public static byte[] AddHdrSignaling(byte[] png, float maxCllNits, float maxFallNits, DisplayMetadata? display)
    {
        if (!png.AsSpan().StartsWith(Signature)) throw new InvalidOperationException("PNG encoding failed.");
        var iccp = IccProfileBuilder.BuildIccpPayload(display?.SdrWhiteNits ?? HdrPngExporter.SdrWhiteNits);
        using var output = new MemoryStream(png.Length + iccp.Length + 128);
        output.Write(Signature);
        var offset = Signature.Length;
        while (offset < png.Length)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4)));
            var type = Encoding.ASCII.GetString(png, offset + 4, 4);
            if (type == "IHDR")
            {
                output.Write(png, offset, checked(length + 12));
                WriteChunk(output, "sBIT", SignificantBits);
                WriteChunk(output, "cICP", Cicp);
                // A cLLI value of zero means "unknown" per the PNG spec, so an all-black frame
                // (which genuinely has zero light) omits the chunk instead of writing 0/0.
                if (maxCllNits > 0)
                    WriteChunk(output, "cLLI", CreateLightLevelPayload(maxCllNits, maxFallNits));
                WriteChunk(output, "mDCV", CreateMasteringDisplayPayload(display, maxCllNits));
                WriteChunk(output, "iCCP", iccp);
                WriteChunk(output, "cHRM", Rec2020Chromaticities);
            }
            else if (type is not "sBIT" and not "cICP" and not "iCCP" and not "sRGB" and not "gAMA" and not "cHRM" and not "cLLI" and not "mDCV")
            {
                output.Write(png, offset, checked(length + 12));
            }
            offset += checked(length + 12);
        }
        return output.ToArray();
    }

    private static byte[] CreateLightLevelPayload(float maxCllNits, float maxFallNits)
    {
        // cLLI: MaxCLL then MaxFALL, PNG four-byte unsigned integers in units of 0.0001 cd/m².
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(payload, ToLuminanceUnits(maxCllNits));
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), ToLuminanceUnits(maxFallNits));
        return payload;
    }

    private static byte[] CreateMasteringDisplayPayload(DisplayMetadata? display, float contentMaxNits)
    {
        // mDCV: R,G,B primaries then white point (0.00002 chromaticity units), then max/min
        // mastering luminance (0.0001 cd/m² units). The capture source display is the mastering
        // display, so use its real DXGI color volume when a coherent descriptor is available;
        // otherwise describe the Rec.2020 container with content-derived peak luminance.
        var payload = new byte[24];
        if (display is { MaxNits: > 0, Red.X: > 0 } d)
        {
            WriteChromaticity(payload, 0, d.Red);
            WriteChromaticity(payload, 4, d.Green);
            WriteChromaticity(payload, 8, d.Blue);
            WriteChromaticity(payload, 12, d.White);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(16), ToLuminanceUnits(d.MaxNits));
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(20), ToLuminanceUnits(d.MinNits));
        }
        else
        {
            for (var i = 0; i < MdcvRec2020Chromaticities.Length; i++)
                BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(i * 2), MdcvRec2020Chromaticities[i]);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(16), ToLuminanceUnits(MathF.Max(contentMaxNits, HdrPngExporter.SdrWhiteNits)));
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(20), 0);
        }
        return payload;
    }

    private static void WriteChromaticity(byte[] payload, int offset, DisplayChromaticity value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(offset), (ushort)Math.Clamp(Math.Round(value.X / 0.00002), 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(offset + 2), (ushort)Math.Clamp(Math.Round(value.Y / 0.00002), 0, ushort.MaxValue));
    }

    private static uint ToLuminanceUnits(float nits) =>
        (uint)Math.Clamp(Math.Round(nits * 10000.0), 0, uint.MaxValue);

    private static void WriteChunk(Stream output, string type, byte[] payload) =>
        PngChunks.WriteChunk(output, type, payload);
}

/// <summary>
/// Rewrites the HDR signaling chunks of an existing HDR Capture PNG in place of the old set
/// (fresh ICC profile included), writing <c>*_fixed.png</c>. Used to repair files exported by
/// earlier builds; light levels are carried over from the file's cLLI chunk when present.
/// </summary>
internal static class PngResigner
{
    public static void Resign(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var maxCllNits = 0f;
        var maxFallNits = 0f;
        var offset = 8;
        while (offset < bytes.Length)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4)));
            if (Encoding.ASCII.GetString(bytes, offset + 4, 4) == "cLLI")
            {
                maxCllNits = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 8, 4)) / 10_000f;
                maxFallNits = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 12, 4)) / 10_000f;
                break;
            }
            offset += checked(length + 12);
        }

        var primary = NativeMethods.MonitorFromPoint(new POINT(), NativeMethods.MonitorDefaultToNearest);
        var display = DisplayInfo.ForMonitor(primary);
        var output = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty,
            Path.GetFileNameWithoutExtension(path) + "_fixed.png");
        File.WriteAllBytes(output, HdrPngMetadata.AddHdrSignaling(bytes, maxCllNits, maxFallNits, display));
    }
}
