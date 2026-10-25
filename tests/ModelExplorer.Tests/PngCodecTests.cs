using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using ModelExplorer.Core.Thumbnails;

namespace ModelExplorer.Tests;

public class PngCodecTests
{
    private static RgbaImage Gradient(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                pixels[i] = (byte)(x * 7);
                pixels[i + 1] = (byte)(y * 11);
                pixels[i + 2] = (byte)((x * y) + 3);
                pixels[i + 3] = (byte)((x + y) % 3 == 0 ? 0 : 255);
            }
        }

        return new RgbaImage(width, height, pixels);
    }

    [Fact]
    public void WhatIsWrittenReadsBackExactly()
    {
        var image = Gradient(37, 19);

        var decoded = PngCodec.Decode(PngCodec.Encode(image));

        Assert.Equal(image.Width, decoded.Width);
        Assert.Equal(image.Height, decoded.Height);
        Assert.Equal(image.Pixels, decoded.Pixels);
    }

    /// <summary>
    /// Thumbnails written by earlier versions of the app came from another encoder,
    /// which may use any filter and colour type. Each is read back correctly.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void EveryFilterIsRead(byte filter)
    {
        var image = Gradient(9, 6);
        var rgb = new byte[image.Width * image.Height * 3];
        for (var i = 0; i < image.Width * image.Height; i++)
        {
            Array.Copy(image.Pixels, i * 4, rgb, i * 3, 3);
        }

        var decoded = PngCodec.Decode(EncodeRgb(rgb, image.Width, image.Height, filter));

        for (var i = 0; i < image.Width * image.Height; i++)
        {
            Assert.Equal(rgb[i * 3], decoded.Pixels[i * 4]);
            Assert.Equal(rgb[(i * 3) + 1], decoded.Pixels[(i * 4) + 1]);
            Assert.Equal(rgb[(i * 3) + 2], decoded.Pixels[(i * 4) + 2]);
            Assert.Equal(255, decoded.Pixels[(i * 4) + 3]);
        }
    }

    [Fact]
    public void SomethingElseIsRejectedAsInvalidData()
    {
        Assert.Throws<InvalidDataException>(() => PngCodec.Decode("not a png at all"u8));
        var truncated = PngCodec.Encode(Gradient(8, 8))[..40];
        Assert.Throws<InvalidDataException>(() => PngCodec.Decode(truncated));
    }

    [Fact]
    public void ShrinkingKeepsTheAspectAndDoesNotDarkenEdges()
    {
        // Opaque red up to column 130, transparent black after it.
        var pixels = new byte[256 * 128 * 4];
        for (var y = 0; y < 128; y++)
        {
            for (var x = 0; x < 130; x++)
            {
                var i = ((y * 256) + x) * 4;
                pixels[i] = 255;
                pixels[i + 3] = 255;
            }
        }

        var small = ImageScaling.Fit(new RgbaImage(256, 128, pixels), 100);

        Assert.Equal(100, small.Width);
        Assert.Equal(50, small.Height);

        // Output column 50 covers source columns 128 to 130.56: two of its 2.56
        // columns are red. It comes out partly transparent but still pure red,
        // not darkened by the black it was averaged with.
        var straddling = ((10 * 100) + 50) * 4;
        Assert.Equal(255, small.Pixels[straddling]);
        Assert.Equal(0, small.Pixels[straddling + 1]);
        Assert.InRange(small.Pixels[straddling + 3], 190, 210);
        Assert.Same(small, ImageScaling.Fit(small, 128));
    }

    /// <summary>An RGB PNG whose every row uses <paramref name="filter"/>, the way other encoders write them.</summary>
    private static byte[] EncodeRgb(byte[] rgb, int width, int height, byte filter)
    {
        const int Bpp = 3;
        var stride = width * Bpp;
        var raw = new byte[(stride + 1) * height];
        for (var y = 0; y < height; y++)
        {
            raw[y * (stride + 1)] = filter;
            for (var x = 0; x < stride; x++)
            {
                int value = rgb[(y * stride) + x];
                int left = x >= Bpp ? rgb[(y * stride) + x - Bpp] : 0;
                int up = y > 0 ? rgb[((y - 1) * stride) + x] : 0;
                int upLeft = x >= Bpp && y > 0 ? rgb[((y - 1) * stride) + x - Bpp] : 0;
                var predicted = filter switch
                {
                    1 => left,
                    2 => up,
                    3 => (left + up) >> 1,
                    4 => Paeth(left, up, upLeft),
                    _ => 0,
                };
                raw[(y * (stride + 1)) + 1 + x] = (byte)(value - predicted);
            }
        }

        using var output = new MemoryStream();
        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 2;
        Chunk(output, "IHDR"u8, header);
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            Chunk(output, "IDAT"u8, compressed.ToArray());
        }

        Chunk(output, "IEND"u8, []);
        return output.ToArray();

        static int Paeth(int a, int b, int c)
        {
            var p = a + b - c;
            int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }

        static void Chunk(Stream stream, ReadOnlySpan<byte> type, byte[] body)
        {
            var word = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(word, body.Length);
            stream.Write(word);
            stream.Write(type);
            stream.Write(body);
            var crc = new Crc32();
            crc.Append(type);
            crc.Append(body);
            BinaryPrimitives.WriteUInt32BigEndian(word, crc.GetCurrentHashAsUInt32());
            stream.Write(word);
        }
    }
}
