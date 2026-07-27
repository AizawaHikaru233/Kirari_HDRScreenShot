using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace HdrCapture;

/// <summary>
/// Generates a valid ICC v4 display profile describing the HDR PNG's Rec.2020 PQ encoding, so
/// color-managed SDR viewers that ignore cICP still tone-map the image instead of showing raw
/// PQ as sRGB. The tone curve decodes PQ and clips at the capture monitor's SDR white level,
/// reproducing the SDR-referenced look of the capture overlay (SDR content matches the desktop,
/// HDR highlights clip to white).
/// </summary>
internal static class IccProfileBuilder
{
    private const int CurvePoints = 4096;

    // Rec.2020 primaries and D65/D50 white points (CIE 1931 xy).
    private static readonly (double X, double Y)[] Rec2020Primaries = { (0.708, 0.292), (0.170, 0.797), (0.131, 0.046) };
    private static readonly (double X, double Y) D65 = (0.3127, 0.3290);
    private static readonly double[] D50Xyz = { 0.9642, 1.0, 0.8249 };

    public static byte[] Build(float sdrWhiteNits)
    {
        var rgbToXyzD65 = ComputeRgbToXyz();
        var bradford = ComputeBradfordD65ToD50();
        var adapted = Multiply(bradford, rgbToXyzD65);

        var description = MlucTag($"HDR Capture Rec2020 PQ (SDR {sdrWhiteNits:0} nit)");
        var copyright = MlucTag("No copyright, use freely");
        var whitePoint = XyzTag(D50Xyz[0], D50Xyz[1], D50Xyz[2]);
        var chad = Sf32Tag(bradford);
        var redColumn = XyzTag(adapted[0, 0], adapted[1, 0], adapted[2, 0]);
        var greenColumn = XyzTag(adapted[0, 1], adapted[1, 1], adapted[2, 1]);
        var blueColumn = XyzTag(adapted[0, 2], adapted[1, 2], adapted[2, 2]);
        var curve = CurveTag(sdrWhiteNits);

        // rTRC/gTRC/bTRC intentionally share one curve data block.
        var tags = new List<(string Signature, byte[] Data, int SharedWith)>
        {
            ("desc", description, -1),
            ("cprt", copyright, -1),
            ("wtpt", whitePoint, -1),
            ("chad", chad, -1),
            ("rXYZ", redColumn, -1),
            ("gXYZ", greenColumn, -1),
            ("bXYZ", blueColumn, -1),
            ("rTRC", curve, -1),
            ("gTRC", curve, 7),
            ("bTRC", curve, 7),
        };

        var tagTableSize = 4 + tags.Count * 12;
        var offsets = new int[tags.Count];
        var dataStart = 128 + tagTableSize;
        var cursor = dataStart;
        for (var i = 0; i < tags.Count; i++)
        {
            if (tags[i].SharedWith >= 0)
            {
                offsets[i] = offsets[tags[i].SharedWith];
                continue;
            }
            offsets[i] = cursor;
            cursor += (tags[i].Data.Length + 3) & ~3;
        }

        var profile = new byte[cursor];
        WriteHeader(profile);
        BinaryPrimitives.WriteUInt32BigEndian(profile, (uint)profile.Length);
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(128), (uint)tags.Count);
        for (var i = 0; i < tags.Count; i++)
        {
            var entry = 132 + i * 12;
            Encoding.ASCII.GetBytes(tags[i].Signature).CopyTo(profile, entry);
            BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(entry + 4), (uint)offsets[i]);
            BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(entry + 8), (uint)tags[i].Data.Length);
            if (tags[i].SharedWith < 0)
                tags[i].Data.CopyTo(profile, offsets[i]);
        }
        return profile;
    }

    /// <summary>Builds the complete iCCP chunk payload: profile name, deflate method, zlib data.</summary>
    public static byte[] BuildIccpPayload(float sdrWhiteNits)
    {
        using var payload = new MemoryStream();
        payload.Write(Encoding.Latin1.GetBytes("HDRCapture2020PQ"));
        payload.WriteByte(0); // name terminator
        payload.WriteByte(0); // compression method: deflate
        using (var zlib = new System.IO.Compression.ZLibStream(payload, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(Build(sdrWhiteNits));
        return payload.ToArray();
    }

    private static void WriteHeader(byte[] profile)
    {
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(8), 0x04300000); // v4.3
        Encoding.ASCII.GetBytes("mntr").CopyTo(profile, 12);
        Encoding.ASCII.GetBytes("RGB ").CopyTo(profile, 16);
        Encoding.ASCII.GetBytes("XYZ ").CopyTo(profile, 20);
        // Fixed creation date (2026-01-01) keeps builds deterministic.
        BinaryPrimitives.WriteUInt16BigEndian(profile.AsSpan(24), 2026);
        BinaryPrimitives.WriteUInt16BigEndian(profile.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16BigEndian(profile.AsSpan(28), 1);
        Encoding.ASCII.GetBytes("acsp").CopyTo(profile, 36);
        Encoding.ASCII.GetBytes("MSFT").CopyTo(profile, 40);
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(64), 1); // relative colorimetric
        // PCS illuminant: canonical D50 encoding.
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(68), 0x0000F6D6);
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(72), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(76), 0x0000D32D);
    }

    private static byte[] CurveTag(float sdrWhiteNits)
    {
        // PQ signal -> linear luminance relative to SDR white, clipped at 1.0.
        var data = new byte[12 + CurvePoints * 2];
        Encoding.ASCII.GetBytes("curv").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), CurvePoints);
        for (var i = 0; i < CurvePoints; i++)
        {
            var signal = i / (double)(CurvePoints - 1);
            var nits = PqToNits(signal);
            var relative = Math.Clamp(nits / sdrWhiteNits, 0.0, 1.0);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(12 + i * 2), (ushort)Math.Round(relative * 65535.0));
        }
        return data;
    }

    private static double PqToNits(double signal)
    {
        const double m1 = 2610.0 / 16384.0;
        const double m2 = 2523.0 / 32.0;
        const double c1 = 3424.0 / 4096.0;
        const double c2 = 2413.0 / 128.0;
        const double c3 = 2392.0 / 128.0;
        var powered = Math.Pow(signal, 1.0 / m2);
        var linear = Math.Pow(Math.Max(powered - c1, 0.0) / (c2 - c3 * powered), 1.0 / m1);
        return linear * 10_000.0;
    }

    private static byte[] XyzTag(double x, double y, double z)
    {
        var data = new byte[20];
        Encoding.ASCII.GetBytes("XYZ ").CopyTo(data, 0);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8), ToS15Fixed16(x));
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(12), ToS15Fixed16(y));
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(16), ToS15Fixed16(z));
        return data;
    }

    private static byte[] Sf32Tag(double[,] matrix)
    {
        var data = new byte[8 + 36];
        Encoding.ASCII.GetBytes("sf32").CopyTo(data, 0);
        for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++)
                BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8 + (row * 3 + column) * 4), ToS15Fixed16(matrix[row, column]));
        return data;
    }

    private static byte[] MlucTag(string text)
    {
        var utf16 = Encoding.BigEndianUnicode.GetBytes(text);
        var data = new byte[28 + utf16.Length];
        Encoding.ASCII.GetBytes("mluc").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 1);   // record count
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12), 12); // record size
        Encoding.ASCII.GetBytes("enUS").CopyTo(data, 16);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), (uint)utf16.Length);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(24), 28);
        utf16.CopyTo(data, 28);
        return data;
    }

    private static int ToS15Fixed16(double value) => checked((int)Math.Round(value * 65536.0));

    private static double[,] ComputeRgbToXyz()
    {
        // Columns from primaries, scaled so RGB(1,1,1) hits the D65 white point.
        var primaries = new double[3, 3];
        for (var i = 0; i < 3; i++)
        {
            var (x, y) = Rec2020Primaries[i];
            primaries[0, i] = x / y;
            primaries[1, i] = 1.0;
            primaries[2, i] = (1.0 - x - y) / y;
        }
        var white = new[] { D65.X / D65.Y, 1.0, (1.0 - D65.X - D65.Y) / D65.Y };
        var scale = Solve(primaries, white);
        var result = new double[3, 3];
        for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++)
                result[row, column] = primaries[row, column] * scale[column];
        return result;
    }

    private static double[,] ComputeBradfordD65ToD50()
    {
        var bradford = new double[,]
        {
            { 0.8951, 0.2664, -0.1614 },
            { -0.7502, 1.7135, 0.0367 },
            { 0.0389, -0.0685, 1.0296 },
        };
        var whiteD65 = new[] { D65.X / D65.Y, 1.0, (1.0 - D65.X - D65.Y) / D65.Y };
        var sourceCone = MultiplyVector(bradford, whiteD65);
        var targetCone = MultiplyVector(bradford, D50Xyz);
        var gain = new double[3, 3];
        for (var i = 0; i < 3; i++) gain[i, i] = targetCone[i] / sourceCone[i];
        return Multiply(Multiply(Invert(bradford), gain), bradford);
    }

    private static double[] MultiplyVector(double[,] matrix, double[] vector)
    {
        var result = new double[3];
        for (var row = 0; row < 3; row++)
            result[row] = matrix[row, 0] * vector[0] + matrix[row, 1] * vector[1] + matrix[row, 2] * vector[2];
        return result;
    }

    private static double[,] Multiply(double[,] a, double[,] b)
    {
        var result = new double[3, 3];
        for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++)
                result[row, column] = a[row, 0] * b[0, column] + a[row, 1] * b[1, column] + a[row, 2] * b[2, column];
        return result;
    }

    private static double[,] Invert(double[,] m)
    {
        var d = m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
              - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
              + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
        var inverse = new double[3, 3];
        inverse[0, 0] = (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) / d;
        inverse[0, 1] = (m[0, 2] * m[2, 1] - m[0, 1] * m[2, 2]) / d;
        inverse[0, 2] = (m[0, 1] * m[1, 2] - m[0, 2] * m[1, 1]) / d;
        inverse[1, 0] = (m[1, 2] * m[2, 0] - m[1, 0] * m[2, 2]) / d;
        inverse[1, 1] = (m[0, 0] * m[2, 2] - m[0, 2] * m[2, 0]) / d;
        inverse[1, 2] = (m[0, 2] * m[1, 0] - m[0, 0] * m[1, 2]) / d;
        inverse[2, 0] = (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]) / d;
        inverse[2, 1] = (m[0, 1] * m[2, 0] - m[0, 0] * m[2, 1]) / d;
        inverse[2, 2] = (m[0, 0] * m[1, 1] - m[0, 1] * m[1, 0]) / d;
        return inverse;
    }

    private static double[] Solve(double[,] matrix, double[] vector) => MultiplyVector(Invert(matrix), vector);
}
