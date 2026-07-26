using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace HdrCapture;

internal static class ContainerSelfTest
{
    public static void Run()
    {
        var pixels = new Half[]
        {
            (Half)0.1f, (Half)0.2f, (Half)0.3f, (Half)1f,
            (Half)1.5f, (Half)0.2f, (Half)0.1f, (Half)1f,
            (Half)5f, (Half)3f, (Half)1f, (Half)1f,
            (Half)0f, (Half)0f, (Half)0f, (Half)1f,
        };
        var frame = new HdrFrame { Width = 2, Height = 2, Pixels = pixels };
        var directory = Path.Combine(Path.GetTempPath(), "HdrCapture-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var hdrPng = Path.Combine(directory, "sample-hdr.png");
            CaptureExporter.Export(frame, hdrPng, "hdrpng");
            VerifyHdrPng(File.ReadAllBytes(hdrPng));
            if (File.Exists(Path.Combine(directory, Path.GetFileNameWithoutExtension(hdrPng) + "_SDR.png")))
                throw new InvalidOperationException("HDR PNG mode wrote an unrequested companion file.");

            // Plain SDR formats and the optional SDR companion of the HDR PNG.
            var sdrPng = Path.Combine(directory, "sample-sdr.png");
            var sdrJpg = Path.Combine(directory, "sample-sdr.jpg");
            CaptureExporter.Export(frame, sdrPng, "sdrpng");
            CaptureExporter.Export(frame, sdrJpg, "sdrjpg");
            var sdrPngBytes = File.ReadAllBytes(sdrPng);
            var sdrJpgBytes = File.ReadAllBytes(sdrJpg);
            if (sdrPngBytes.Length < 8 || sdrPngBytes[0] != 137 || sdrPngBytes[1] != 80)
                throw new InvalidOperationException("SDR PNG export is not a PNG.");
            if (sdrJpgBytes.Length < 4 || sdrJpgBytes[0] != 0xFF || sdrJpgBytes[1] != 0xD8)
                throw new InvalidOperationException("SDR JPG export is not a JPEG.");

            var hdrWithCopy = Path.Combine(directory, "sample-copy.png");
            CaptureExporter.Export(frame, hdrWithCopy, "hdrpng", saveSdrCopy: true);
            if (!File.Exists(Path.Combine(directory, "sample-copy_SDR.png")))
                throw new InvalidOperationException("HDR PNG did not write the requested SDR companion.");
            // "name_HDR.png" pairs with a suffix-free companion.
            CaptureExporter.Export(frame, Path.Combine(directory, "sample2_HDR.png"), "hdrpng", saveSdrCopy: true);
            if (!File.Exists(Path.Combine(directory, "sample2.png")))
                throw new InvalidOperationException("HDR-suffixed export did not write the paired SDR file.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyHdrPng(byte[] bytes)
    {
        if (bytes.Length < 33 || bytes[24] != 16 || bytes[25] != 2)
            throw new InvalidOperationException("HDR PNG is not 16-bit RGB.");

        var offset = 8;
        var foundCicp = false;
        var foundIcc = false;
        var foundSignificantBits = false;
        var foundChromaticities = false;
        var foundLightLevel = false;
        var foundMasteringDisplay = false;
        while (offset < bytes.Length)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4)));
            var type = Encoding.ASCII.GetString(bytes, offset + 4, 4);
            if (type == "cICP")
                foundCicp = length == 4 && bytes[offset + 8] == 9 && bytes[offset + 9] == 16 && bytes[offset + 10] == 0 && bytes[offset + 11] == 1;
            if (type == "iCCP")
                foundIcc = VerifyIccProfile(bytes, offset + 8, length);
            if (type == "sBIT")
                foundSignificantBits = length == 3 && bytes[offset + 8] == 10 && bytes[offset + 9] == 10 && bytes[offset + 10] == 10;
            if (type == "cHRM")
                foundChromaticities = length == 32;
            if (type == "cLLI")
            {
                // MaxCLL leads MaxFALL; a peak can never be below its frame average.
                var maxCll = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 8, 4));
                var maxFall = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 12, 4));
                foundLightLevel = length == 8 && maxCll >= maxFall && maxFall > 0;
            }
            if (type == "mDCV")
                // Red primary x = 0.708 / 0.00002 = 35400 leads the payload; max luminance must be non-zero.
                foundMasteringDisplay = length == 24 &&
                    BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 8, 2)) == 35400 &&
                    BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 8 + 16, 4)) > 0;
            offset = checked(offset + length + 12);
        }
        if (!foundCicp || !foundIcc || !foundSignificantBits || !foundChromaticities)
            throw new InvalidOperationException("HDR PNG lacks Rec.2020 PQ signaling.");
        if (!foundLightLevel || !foundMasteringDisplay)
            throw new InvalidOperationException("HDR PNG lacks cLLI/mDCV light-level metadata.");
    }

    private static bool VerifyIccProfile(byte[] bytes, int payloadStart, int payloadLength)
    {
        // The zlib stream must inflate and carry a plausible ICC header ('acsp', matching size).
        try
        {
            var nameEnd = Array.IndexOf(bytes, (byte)0, payloadStart, payloadLength);
            if (nameEnd < 0) return false;
            var zlibStart = nameEnd + 2;
            using var raw = new MemoryStream(bytes, zlibStart, payloadLength - (zlibStart - payloadStart));
            using var zlib = new System.IO.Compression.ZLibStream(raw, System.IO.Compression.CompressionMode.Decompress);
            using var inflated = new MemoryStream();
            zlib.CopyTo(inflated);
            var icc = inflated.ToArray();
            return icc.Length > 128 &&
                Encoding.ASCII.GetString(icc, 36, 4) == "acsp" &&
                BinaryPrimitives.ReadUInt32BigEndian(icc) == icc.Length;
        }
        catch
        {
            return false;
        }
    }

}
