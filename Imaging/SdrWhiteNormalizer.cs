namespace HdrCapture;

/// <summary>
/// Converts a composited scRGB frame captured with one HDR SDR-white setting
/// to a fixed output SDR-white reference. Both values are in nits; scRGB 1.0
/// is 80 nits. The source display peak remains an anchor, so HDR highlights
/// are not globally amplified when the SDR reference is raised.
/// </summary>
internal static class SdrWhiteNormalizer
{
    public static float NormalizeScRgb(float value, float sourceSdrWhiteNits, float referenceSdrWhiteNits)
    {
        if (!float.IsFinite(value)) return 0f;
        if (!float.IsFinite(sourceSdrWhiteNits) || sourceSdrWhiteNits <= 0)
            sourceSdrWhiteNits = HdrPngExporter.SdrWhiteNits;
        if (!float.IsFinite(referenceSdrWhiteNits) || referenceSdrWhiteNits <= 0)
            referenceSdrWhiteNits = HdrPngExporter.SdrWhiteNits;
        return value * referenceSdrWhiteNits / sourceSdrWhiteNits;
    }

    public static HdrFrame NormalizeFrame(HdrFrame frame, float sourceSdrWhiteNits, float referenceSdrWhiteNits, float sourcePeakNits)
    {
        sourceSdrWhiteNits = SanitizeNits(sourceSdrWhiteNits, HdrPngExporter.SdrWhiteNits);
        referenceSdrWhiteNits = SanitizeNits(referenceSdrWhiteNits, HdrPngExporter.SdrWhiteNits);
        sourcePeakNits = Math.Max(sourceSdrWhiteNits, SanitizeNits(sourcePeakNits, sourceSdrWhiteNits));

        if (MathF.Abs(sourceSdrWhiteNits - referenceSdrWhiteNits) < 0.001f)
            return frame;

        var normalized = new Half[frame.Pixels.Length];
        for (var pixel = 0; pixel < frame.Width * frame.Height; pixel++)
        {
            var offset = pixel * 4;
            var red = SanitizeComponent((float)frame.Pixels[offset]);
            var green = SanitizeComponent((float)frame.Pixels[offset + 1]);
            var blue = SanitizeComponent((float)frame.Pixels[offset + 2]);
            // Use a shared scale derived from scene luminance to preserve chromaticity,
            // including scRGB's negative components used for wide-gamut colors.
            var luminance = MathF.Max(0f, 0.2126f * red + 0.7152f * green + 0.0722f * blue);
            var scale = luminance <= 0f ? referenceSdrWhiteNits / sourceSdrWhiteNits
                : MapLuminanceNits(luminance * HdrPngExporter.SdrWhiteNits, sourceSdrWhiteNits, referenceSdrWhiteNits, sourcePeakNits) /
                  (luminance * HdrPngExporter.SdrWhiteNits);

            normalized[offset] = ToHalf(red * scale);
            normalized[offset + 1] = ToHalf(green * scale);
            normalized[offset + 2] = ToHalf(blue * scale);
            normalized[offset + 3] = frame.Pixels[offset + 3];
        }

        DisplayMetadata? display = frame.Display is { } sourceDisplay
            ? sourceDisplay with { SdrWhiteNits = referenceSdrWhiteNits }
            : null;
        return frame with { Pixels = normalized, Display = display };
    }

    internal static float MapLuminanceNits(float luminanceNits, float sourceSdrWhiteNits, float referenceSdrWhiteNits, float sourcePeakNits)
    {
        sourceSdrWhiteNits = SanitizeNits(sourceSdrWhiteNits, HdrPngExporter.SdrWhiteNits);
        referenceSdrWhiteNits = SanitizeNits(referenceSdrWhiteNits, HdrPngExporter.SdrWhiteNits);
        sourcePeakNits = Math.Max(sourceSdrWhiteNits, SanitizeNits(sourcePeakNits, sourceSdrWhiteNits));
        if (!float.IsFinite(luminanceNits) || luminanceNits <= 0) return 0;

        if (sourcePeakNits <= sourceSdrWhiteNits + 0.001f || referenceSdrWhiteNits >= sourcePeakNits)
            return luminanceNits * referenceSdrWhiteNits / sourceSdrWhiteNits;
        if (luminanceNits <= sourceSdrWhiteNits)
            return luminanceNits * referenceSdrWhiteNits / sourceSdrWhiteNits;

        // The knee maps source SDR white to the chosen reference and leaves the display peak
        // fixed. It intentionally continues with the same slope above peak for out-of-range
        // content; the PNG encoder still enforces the 10,000-nit PQ ceiling.
        var slope = (sourcePeakNits - referenceSdrWhiteNits) / (sourcePeakNits - sourceSdrWhiteNits);
        return referenceSdrWhiteNits + (luminanceNits - sourceSdrWhiteNits) * slope;
    }

    private static float SanitizeNits(float value, float fallback) =>
        float.IsFinite(value) && value > 0 ? value : fallback;

    private static float SanitizeComponent(float value) => float.IsFinite(value) ? value : 0;

    private static Half ToHalf(float value) =>
        (Half)Math.Clamp(float.IsFinite(value) ? value : 0f, -65504f, 65504f);
}
