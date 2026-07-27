using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace HdrCapture;

/// <summary>Shared PNG chunk plumbing (length + type + payload + CRC32).</summary>
internal static class PngChunks
{
    public static void WriteChunk(Stream output, string type, byte[] payload)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        Span<byte> value = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(value, checked((uint)payload.Length));
        output.Write(value);
        output.Write(typeBytes);
        output.Write(payload);
        BinaryPrimitives.WriteUInt32BigEndian(value, Crc32(typeBytes, payload));
        output.Write(value);
    }

    public static uint Crc32(byte[] type, byte[] payload)
    {
        var crc = 0xFFFF_FFFFu;
        foreach (var value in type) crc = Update(crc, value);
        foreach (var value in payload) crc = Update(crc, value);
        return ~crc;
    }

    private static uint Update(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        return crc;
    }
}

/// <summary>
/// Minimal self-contained truecolor PNG encoder (8- or 16-bit RGB) — no external imaging
/// dependency. Normal mode uses the Up filter + Optimal deflate; fast mode (clipboard) uses
/// no filtering + Fastest deflate.
/// </summary>
internal static class PngWriter
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    /// <param name="scanlines">Raw pixel bytes, row-major, no filter bytes; 16-bit samples big-endian.</param>
    public static byte[] Encode(int width, int height, int bitDepth, byte[] scanlines, bool fast)
    {
        if (bitDepth is not (8 or 16)) throw new ArgumentOutOfRangeException(nameof(bitDepth));
        var bytesPerRow = width * 3 * (bitDepth / 8);
        if (scanlines.Length != bytesPerRow * height)
            throw new ArgumentException("Scanline buffer does not match the image dimensions.");

        using var output = new MemoryStream(scanlines.Length / 2);
        output.Write(Signature);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = (byte)bitDepth;
        ihdr[9] = 2; // truecolor RGB
        PngChunks.WriteChunk(output, "IHDR", ihdr);

        using (var idat = new MemoryStream(scanlines.Length / 2))
        {
            using (var zlib = new ZLibStream(idat, fast ? CompressionLevel.Fastest : CompressionLevel.Optimal, leaveOpen: true))
            {
                if (fast)
                {
                    // Filter 0 (None) on every row.
                    for (var y = 0; y < height; y++)
                    {
                        zlib.WriteByte(0);
                        zlib.Write(scanlines, y * bytesPerRow, bytesPerRow);
                    }
                }
                else
                {
                    // Filter 2 (Up): stored = raw - above; strong on screenshots and gradients.
                    var filtered = new byte[bytesPerRow];
                    for (var y = 0; y < height; y++)
                    {
                        var row = y * bytesPerRow;
                        var previous = row - bytesPerRow;
                        if (y == 0)
                        {
                            zlib.WriteByte(0);
                            zlib.Write(scanlines, row, bytesPerRow);
                            continue;
                        }
                        for (var i = 0; i < bytesPerRow; i++)
                            filtered[i] = (byte)(scanlines[row + i] - scanlines[previous + i]);
                        zlib.WriteByte(2);
                        zlib.Write(filtered, 0, bytesPerRow);
                    }
                }
            }
            PngChunks.WriteChunk(output, "IDAT", idat.ToArray());
        }
        PngChunks.WriteChunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }
}
