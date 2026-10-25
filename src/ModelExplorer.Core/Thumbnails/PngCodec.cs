using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;

namespace ModelExplorer.Core.Thumbnails;

/// <summary>An 8-bit RGBA image, straight alpha, rows from the top.</summary>
public sealed record RgbaImage(int Width, int Height, byte[] Pixels);

/// <summary>
/// Reads and writes the PNG files of the thumbnail cache.
/// </summary>
/// <remarks>
/// The cache holds only small, non-interlaced 8-bit images, so a codec of its own
/// costs less than an imaging library: no dependency, nothing to marshal, and it
/// runs on the thumbnail workers without a COM apartment. It reads every 8-bit
/// colour type with every filter, so thumbnails written by earlier versions of the
/// app stay valid.
/// </remarks>
public static class PngCodec
{
    private static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    public static byte[] Encode(RgbaImage image)
    {
        var stride = image.Width * 4;
        using var output = new MemoryStream();
        output.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], image.Height);
        header[8] = 8; // bit depth
        header[9] = 6; // RGBA
        WriteChunk(output, "IHDR"u8, header);

        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                // The Sub filter: each byte minus the one four to its left. Shaded
                // surfaces change slowly along a row, so this roughly halves the size.
                var row = new byte[stride + 1];
                row[0] = 1;
                for (var y = 0; y < image.Height; y++)
                {
                    var source = image.Pixels.AsSpan(y * stride, stride);
                    for (var x = 0; x < stride; x++)
                    {
                        row[x + 1] = (byte)(source[x] - (x >= 4 ? source[x - 4] : 0));
                    }

                    zlib.Write(row);
                }
            }

            WriteChunk(output, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        }

        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    /// <exception cref="InvalidDataException">Not a PNG this codec reads.</exception>
    public static RgbaImage Decode(ReadOnlySpan<byte> png)
    {
        if (png.Length < 8 || !png[..8].SequenceEqual(Signature))
        {
            throw new InvalidDataException("Not a PNG file.");
        }

        int width = 0, height = 0, colorType = -1;
        byte[]? palette = null, paletteAlpha = null;
        using var data = new MemoryStream();
        var position = 8;
        while (position + 8 <= png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png[position..]);
            var type = png.Slice(position + 4, 4);
            if (length < 0 || position + 12 + length > png.Length)
            {
                throw new InvalidDataException("Truncated PNG chunk.");
            }

            var body = png.Slice(position + 8, length);
            if (type.SequenceEqual("IHDR"u8))
            {
                width = BinaryPrimitives.ReadInt32BigEndian(body);
                height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                colorType = body[9];
                if (body[8] != 8 || body[12] != 0)
                {
                    throw new InvalidDataException("Only 8-bit, non-interlaced PNG is supported.");
                }
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                palette = body.ToArray();
            }
            else if (type.SequenceEqual("tRNS"u8))
            {
                paletteAlpha = body.ToArray();
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                data.Write(body);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }

            position += 12 + length;
        }

        var channels = colorType switch
        {
            0 => 1, // grey
            2 => 3, // RGB
            3 => 1, // palette
            4 => 2, // grey + alpha
            6 => 4, // RGBA
            _ => throw new InvalidDataException($"Unsupported PNG colour type {colorType}."),
        };
        if (width <= 0 || height <= 0 || (long)width * height > 1 << 26)
        {
            throw new InvalidDataException("Invalid PNG dimensions.");
        }

        var stride = width * channels;
        var raw = new byte[(stride + 1) * height];
        data.Position = 0;
        try
        {
            using var zlib = new ZLibStream(data, CompressionMode.Decompress);
            zlib.ReadExactly(raw);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("The PNG's image data is truncated.", ex);
        }

        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            Unfilter(raw[y * (stride + 1)], raw.AsSpan((y * (stride + 1)) + 1, stride), y == 0 ? default : pixels.AsSpan((y - 1) * stride, stride),
                pixels.AsSpan(y * stride, stride), channels);
        }

        return new RgbaImage(width, height, ToRgba(pixels, width, height, colorType, palette, paletteAlpha));
    }

    private static void Unfilter(byte filter, ReadOnlySpan<byte> row, ReadOnlySpan<byte> previous, Span<byte> output, int bpp)
    {
        for (var x = 0; x < row.Length; x++)
        {
            int left = x >= bpp ? output[x - bpp] : 0;
            int up = previous.IsEmpty ? 0 : previous[x];
            int upLeft = x >= bpp && !previous.IsEmpty ? previous[x - bpp] : 0;
            output[x] = filter switch
            {
                0 => row[x],
                1 => (byte)(row[x] + left),
                2 => (byte)(row[x] + up),
                3 => (byte)(row[x] + ((left + up) >> 1)),
                4 => (byte)(row[x] + Paeth(left, up, upLeft)),
                _ => throw new InvalidDataException($"Unknown PNG filter {filter}."),
            };
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static byte[] ToRgba(byte[] pixels, int width, int height, int colorType, byte[]? palette, byte[]? paletteAlpha)
    {
        if (colorType == 6)
        {
            return pixels;
        }

        var rgba = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            byte r, g, b, a = 255;
            switch (colorType)
            {
                case 0:
                    r = g = b = pixels[i];
                    break;
                case 2:
                    r = pixels[i * 3];
                    g = pixels[(i * 3) + 1];
                    b = pixels[(i * 3) + 2];
                    break;
                case 3:
                    var entry = pixels[i];
                    if (palette is null || (entry * 3) + 2 >= palette.Length)
                    {
                        throw new InvalidDataException("PNG palette index out of range.");
                    }

                    r = palette[entry * 3];
                    g = palette[(entry * 3) + 1];
                    b = palette[(entry * 3) + 2];
                    a = paletteAlpha is not null && entry < paletteAlpha.Length ? paletteAlpha[entry] : (byte)255;
                    break;
                default:
                    r = g = b = pixels[i * 2];
                    a = pixels[(i * 2) + 1];
                    break;
            }

            rgba[i * 4] = r;
            rgba[(i * 4) + 1] = g;
            rgba[(i * 4) + 2] = b;
            rgba[(i * 4) + 3] = a;
        }

        return rgba;
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> body)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, body.Length);
        output.Write(word);
        output.Write(type);
        output.Write(body);

        var crc = new Crc32();
        crc.Append(type);
        crc.Append(body);
        BinaryPrimitives.WriteUInt32BigEndian(word, crc.GetCurrentHashAsUInt32());
        output.Write(word);
    }
}
