using System.IO;

namespace HdrCapture;

internal readonly record struct ExportResult(string MainPath, float MaxGainStops);

/// <summary>Routes an HDR frame to the configured on-disk format.</summary>
internal static class CaptureExporter
{
    public static ExportResult Export(HdrFrame frame, string outputPath, string format, bool saveSdrCopy = false)
    {
        var sdrWhiteScale = (frame.Display?.SdrWhiteNits ?? HdrPngExporter.SdrWhiteNits) / HdrPngExporter.SdrWhiteNits;
        if (format.Equals("sdrpng", StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllBytes(outputPath, SdrExporter.EncodePng(frame, sdrWhiteScale));
            return new ExportResult(outputPath, 0f);
        }
        if (format.Equals("sdrjpg", StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllBytes(outputPath, SdrExporter.EncodeJpg(frame, sdrWhiteScale));
            return new ExportResult(outputPath, 0f);
        }

        // Single-file HDR PNG (ReShade/SpecialK-style signaling); optional SDR PNG companion.
        var maxNits = HdrPngExporter.Export(frame, outputPath);
        if (saveSdrCopy)
        {
            // "name_HDR.png" pairs with a suffix-free "name.png" companion; other names fall
            // back to an explicit "_SDR" suffix.
            var baseName = Path.GetFileNameWithoutExtension(outputPath);
            var sdrName = baseName.EndsWith("_HDR", StringComparison.OrdinalIgnoreCase)
                ? baseName[..^4] + ".png"
                : baseName + "_SDR.png";
            var sdrPath = Path.Combine(Path.GetDirectoryName(outputPath) ?? string.Empty, sdrName);
            File.WriteAllBytes(sdrPath, SdrExporter.EncodePng(frame, sdrWhiteScale));
        }
        var stopsOverSdr = MathF.Max(0f, MathF.Log2(MathF.Max(maxNits, HdrPngExporter.SdrWhiteNits) / HdrPngExporter.SdrWhiteNits));
        return new ExportResult(outputPath, stopsOverSdr);
    }
}
