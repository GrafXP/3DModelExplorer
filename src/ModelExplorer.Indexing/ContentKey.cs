using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;

namespace ModelExplorer.Indexing;

/// <summary>
/// A cheap content fingerprint: xxHash64 over the first 64 KB, salted with the
/// file's length.
/// </summary>
/// <remarks>
/// Deliberately not a hash of the whole file. Scanning never computes this —
/// change detection is (size, mtime) — so it is only paid when a thumbnail is
/// actually generated, and reading a fixed 64 KB keeps that cost flat whether
/// the model is 10 KB or 400 MB.
///
/// Mixing the length in is what makes a prefix hash usable as an identity: two
/// STLs exported from the same source with different geometry share their
/// 80-byte header and triangle count but not their length. Collisions remain
/// possible in principle, and the consequence is bounded — a wrong thumbnail,
/// never wrong data.
/// </remarks>
public static class ContentKey
{
    /// <summary>Bytes read from the head of the file.</summary>
    public const int SampleBytes = 64 * 1024;

    /// <summary>16 lower-case hex characters, safe to use as a file name.</summary>
    public static string Compute(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 0,
            FileOptions.SequentialScan);

        return Compute(stream, stream.Length);
    }

    public static string Compute(Stream stream, long length)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(SampleBytes);
        try
        {
            var read = ReadUpTo(stream, buffer.AsSpan(0, SampleBytes));

            var hash = new XxHash64();
            hash.Append(buffer.AsSpan(0, read));

            // Appended rather than folded in afterwards so the length is part of
            // the same digest, not a second value the caller has to carry.
            Span<byte> tail = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(tail, length);
            hash.Append(tail);

            return hash.GetCurrentHashAsUInt64().ToString("x16");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Fills as much of the buffer as the stream has. A single Read can return
    /// short for reasons that are not end-of-file — notably on network shares,
    /// which is exactly where these files often live.
    /// </summary>
    private static int ReadUpTo(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
