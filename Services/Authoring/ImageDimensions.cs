//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
// Ported from Bcl.Dms (namespace changed; logic unchanged unless marked "DMS_V2").
using System.Buffers.Binary;

namespace DMS.Web.Services.Authoring;

/// <summary>
/// Reads the pixel dimensions of an embedded image from its own header bytes.
///
/// <para><b>Why not System.Drawing or ImageSharp</b></para>
/// System.Drawing.Common is Windows-only from .NET 7 onwards, and this server
/// has to run on Windows and Linux alike; an imaging package would be a new
/// third-party dependency to qualify for a validated system, for the sake of two
/// integers. Every format the sanitiser admits states its own size in the first
/// few dozen bytes, so those bytes are read directly.
///
/// <para>
/// The consequence of getting this wrong is not a crash but a document that
/// looks wrong: an image written into the .docx at the wrong aspect ratio is
/// distorted in the controlled PDF, and nobody notices until it is printed. That
/// is why each reader below validates the structure it is walking rather than
/// indexing at a fixed offset and hoping.
/// </para>
/// </summary>
internal static class ImageDimensions
{
    /// <summary>
    /// Returns the image's size in pixels, or (0, 0) when the header cannot be
    /// read. The caller substitutes a default rather than failing the save: a
    /// picture at a guessed size is recoverable, a lost afternoon is not.
    /// </summary>
    public static (int Width, int Height) Measure(byte[] bytes, string extension)
    {
        try
        {
            return extension.ToLowerInvariant() switch
            {
                "png" => Png(bytes),
                "jpeg" or "jpg" => Jpeg(bytes),
                "gif" => Gif(bytes),
                "webp" => Webp(bytes),
                _ => (0, 0)
            };
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            // A truncated file is a data problem, not an exception to propagate
            // out of a document save.
            return (0, 0);
        }
    }

    /// <summary>
    /// PNG: the IHDR chunk is always first and always carries width then height
    /// as big-endian 32-bit integers at offsets 16 and 20.
    /// </summary>
    private static (int, int) Png(byte[] b)
    {
        if (b.Length < 24) return (0, 0);
        if (b[12] != 'I' || b[13] != 'H' || b[14] != 'D' || b[15] != 'R') return (0, 0);

        var width = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(20, 4));

        return Sane(width, height);
    }

    /// <summary>
    /// GIF: little-endian 16-bit logical screen width and height at offsets 6
    /// and 8, in both the 87a and 89a versions.
    /// </summary>
    private static (int, int) Gif(byte[] b)
    {
        if (b.Length < 10) return (0, 0);

        var width = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(6, 2));
        var height = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(8, 2));

        return Sane(width, height);
    }

    /// <summary>
    /// JPEG has no fixed header: the size lives in whichever start-of-frame
    /// marker the encoder used, so the segment chain has to be walked. SOF0
    /// through SOF15 all carry height then width at the same place, except the
    /// four markers in that range that are not frame headers at all (DHT, JPG,
    /// DAC, and the restart markers) - skipping those is the part a naive
    /// implementation gets wrong on progressive JPEGs.
    /// </summary>
    private static (int, int) Jpeg(byte[] b)
    {
        if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8) return (0, 0);

        var i = 2;

        while (i + 3 < b.Length)
        {
            if (b[i] != 0xFF) { i++; continue; }

            var marker = b[i + 1];

            // Padding and standalone markers carry no length field.
            if (marker == 0xFF) { i++; continue; }
            if (marker is 0x01 or >= 0xD0 and <= 0xD9) { i += 2; continue; }

            if (i + 3 >= b.Length) break;

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i + 2, 2));
            if (segmentLength < 2) break;

            var isStartOfFrame = marker is >= 0xC0 and <= 0xCF
                                 && marker is not (0xC4 or 0xC8 or 0xCC);

            if (isStartOfFrame)
            {
                if (i + 9 >= b.Length) break;

                var height = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i + 5, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i + 7, 2));

                return Sane(width, height);
            }

            // Start of scan: the compressed data follows and there is no further
            // header to find.
            if (marker == 0xDA) break;

            i += 2 + segmentLength;
        }

        return (0, 0);
    }

    /// <summary>
    /// WebP comes in three container flavours - lossy (VP8), lossless (VP8L) and
    /// extended (VP8X) - and each states its size differently.
    /// </summary>
    private static (int, int) Webp(byte[] b)
    {
        if (b.Length < 30) return (0, 0);
        if (b[8] != 'W' || b[9] != 'E' || b[10] != 'B' || b[11] != 'P') return (0, 0);

        var fourCc = System.Text.Encoding.ASCII.GetString(b, 12, 4);

        switch (fourCc)
        {
            case "VP8 ":
            {
                // The frame header starts after a 3-byte tag and the 3-byte
                // start code 0x9D012A.
                if (b.Length < 30) return (0, 0);
                if (b[23] != 0x9D || b[24] != 0x01 || b[25] != 0x2A) return (0, 0);

                var width = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(26, 2)) & 0x3FFF;
                var height = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(28, 2)) & 0x3FFF;

                return Sane(width, height);
            }

            case "VP8L":
            {
                // 14 bits of width-1 then 14 bits of height-1, packed
                // little-endian after the 0x2F signature byte.
                if (b.Length < 25 || b[20] != 0x2F) return (0, 0);

                var bits = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(21, 4));
                var width = (int)(bits & 0x3FFF) + 1;
                var height = (int)((bits >> 14) & 0x3FFF) + 1;

                return Sane(width, height);
            }

            case "VP8X":
            {
                // Canvas size as two 24-bit little-endian values, each minus one.
                if (b.Length < 30) return (0, 0);

                var width = (b[24] | (b[25] << 8) | (b[26] << 16)) + 1;
                var height = (b[27] | (b[28] << 8) | (b[29] << 16)) + 1;

                return Sane(width, height);
            }

            default:
                return (0, 0);
        }
    }

    /// <summary>
    /// A dimension outside this range is a corrupt header rather than a picture,
    /// and passing it on would produce an OOXML extent that makes Word offer to
    /// repair the document.
    /// </summary>
    private static (int, int) Sane(int width, int height)
        => width is > 0 and <= 30000 && height is > 0 and <= 30000 ? (width, height) : (0, 0);
}
