using System.Buffers.Binary;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;

namespace ModelExplorer.Geometry.Stl;

/// <summary>
/// Reads binary and ASCII STL. Binary is the hot path and is memory-mapped and
/// parsed in parallel; ASCII is comparatively rare and handled with a simple
/// streaming tokenizer.
/// </summary>
public static class StlReader
{
    private const int HeaderBytes = 80;
    private const int BinaryPrologue = HeaderBytes + 4; // header + uint32 triangle count
    private const int TriangleBytes = 50;               // normal + 3 vertices + attribute word

    /// <summary>Below this, thread coordination costs more than it saves.</summary>
    private const int ParallelThreshold = 64 * 1024;

    public static MeshData Read(string path, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("STL file not found.", path);
        }

        if (info.Length == 0)
        {
            throw new GeometryFormatException($"'{Path.GetFileName(path)}' is empty.");
        }

        int triangleCount;
        using (var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                   FileOptions.SequentialScan))
        {
            if (!TryReadBinaryTriangleCount(handle, info.Length, out triangleCount))
            {
                return ReadAscii(path, cancellationToken);
            }
        }

        return triangleCount == 0
            ? MeshData.Empty
            : ReadBinary(path, triangleCount, cancellationToken);
    }

    /// <summary>
    /// Decides binary vs ASCII. The declared triangle count matching the file
    /// length exactly is the only trustworthy signal: binary STLs are routinely
    /// written with a header beginning "solid", so the leading keyword alone
    /// misclassifies them.
    /// </summary>
    private static bool TryReadBinaryTriangleCount(SafeFileHandle handle, long length, out int triangleCount)
    {
        triangleCount = 0;
        if (length < BinaryPrologue)
        {
            return false;
        }

        Span<byte> prologue = stackalloc byte[BinaryPrologue];
        if (!TryReadExactly(handle, prologue, 0))
        {
            return false;
        }

        uint declared = BinaryPrimitives.ReadUInt32LittleEndian(prologue[HeaderBytes..]);

        // Three vertices per triangle must still index with an int.
        if (declared > int.MaxValue / 3)
        {
            return false;
        }

        long expected = BinaryPrologue + ((long)declared * TriangleBytes);
        if (length == expected)
        {
            triangleCount = (int)declared;
            return true;
        }

        // Some writers append padding. Accept a file that is merely longer than
        // declared, but only when it does not look like ASCII — otherwise a large
        // ASCII file whose bytes happen to parse as a plausible count is misread.
        if (expected < length && declared > 0 && !StartsWithSolid(prologue))
        {
            triangleCount = (int)declared;
            return true;
        }

        return false;
    }

    private static bool StartsWithSolid(ReadOnlySpan<byte> prologue)
    {
        var i = 0;
        while (i < prologue.Length && prologue[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            i++;
        }

        return prologue[i..].StartsWith("solid"u8);
    }

    private static bool TryReadExactly(SafeFileHandle handle, Span<byte> buffer, long offset)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = RandomAccess.Read(handle, buffer[total..], offset + total);
            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }

    /// <summary>
    /// Maps the file and parses straight out of the mapped pages. This avoids
    /// copying the whole file into managed memory first — on a 100 MB STL that
    /// is a 100 MB allocation and a full extra pass over the data saved.
    /// </summary>
    private static unsafe MeshData ReadBinary(string path, int triangleCount, CancellationToken cancellationToken)
    {
        using var mmf = MemoryMappedFile.CreateFromFile(
            path, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
        using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

        byte* basePointer = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref basePointer);
        try
        {
            var payload = basePointer + view.PointerOffset + BinaryPrologue;
            return BuildFromBinary((nint)payload, triangleCount, cancellationToken);
        }
        finally
        {
            view.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    private static MeshData BuildFromBinary(nint payload, int triangleCount, CancellationToken cancellationToken)
    {
        var vertexCount = triangleCount * 3;
        var positions = GC.AllocateUninitializedArray<Vector3>(vertexCount);
        var normals = GC.AllocateUninitializedArray<Vector3>(vertexCount);
        var indices = GC.AllocateUninitializedArray<int>(vertexCount);

        BoundingBox bounds;

        if (triangleCount < ParallelThreshold)
        {
            bounds = ProcessRange(payload, 0, triangleCount, positions, normals, indices,
                BoundingBox.Empty, cancellationToken);
        }
        else
        {
            // Ranges are disjoint, so the three output arrays need no synchronisation.
            // Only the merged bounding box does.
            bounds = BoundingBox.Empty;
            var gate = new object();
            var chunkSize = Math.Max(8192, triangleCount / (Environment.ProcessorCount * 8));
            var chunks = (triangleCount + chunkSize - 1) / chunkSize;

            // Handing the token to ParallelOptions matters for more than early exit:
            // it makes Parallel.For rethrow a bare OperationCanceledException instead
            // of bundling it into an AggregateException, so callers can catch the
            // obvious type.
            var options = new ParallelOptions { CancellationToken = cancellationToken };

            Parallel.For(
                0,
                chunks,
                options,
                () => BoundingBox.Empty,
                (chunk, _, local) =>
                {
                    var start = chunk * chunkSize;
                    var end = Math.Min(start + chunkSize, triangleCount);
                    return ProcessRange(payload, start, end, positions, normals, indices, local, cancellationToken);
                },
                local =>
                {
                    lock (gate)
                    {
                        bounds = bounds.Union(local);
                    }
                });
        }

        return new MeshData
        {
            Positions = positions,
            Normals = normals,
            Indices = indices,
            Bounds = bounds,
        };
    }

    private static unsafe BoundingBox ProcessRange(
        nint payload,
        int start,
        int end,
        Vector3[] positions,
        Vector3[] normals,
        int[] indices,
        BoundingBox bounds,
        CancellationToken cancellationToken)
    {
        var basePointer = (byte*)payload;

        for (var t = start; t < end; t++)
        {
            // Checking every triangle would dominate the loop; every 64K is
            // still sub-millisecond responsiveness.
            if ((t & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var record = basePointer + ((long)t * TriangleBytes);

            var stored = ReadVector3(record);
            var v0 = ReadVector3(record + 12);
            var v1 = ReadVector3(record + 24);
            var v2 = ReadVector3(record + 36);

            // Recomputed rather than trusted: the stored normal is zero or wrong
            // in a large share of real-world STLs, which shows up as black facets.
            var normal = Vector3.Cross(v1 - v0, v2 - v0);
            var lengthSquared = normal.LengthSquared();
            if (lengthSquared > 1e-20f)
            {
                normal *= 1f / MathF.Sqrt(lengthSquared);
            }
            else
            {
                // Degenerate triangle — fall back to the stored normal.
                var storedLengthSquared = stored.LengthSquared();
                normal = storedLengthSquared > 1e-20f
                    ? stored * (1f / MathF.Sqrt(storedLengthSquared))
                    : Vector3.UnitZ;
            }

            var i = t * 3;
            positions[i] = v0;
            positions[i + 1] = v1;
            positions[i + 2] = v2;
            normals[i] = normal;
            normals[i + 1] = normal;
            normals[i + 2] = normal;
            indices[i] = i;
            indices[i + 1] = i + 1;
            indices[i + 2] = i + 2;

            bounds = bounds.Union(v0).Union(v1).Union(v2);
        }

        return bounds;
    }

    /// <remarks>
    /// STL floats are little-endian and unaligned within the 50-byte record.
    /// Windows runs little-endian on every architecture .NET supports, so no
    /// byte swap is needed here.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe Vector3 ReadVector3(byte* p) => new(
        Unsafe.ReadUnaligned<float>(p),
        Unsafe.ReadUnaligned<float>(p + 4),
        Unsafe.ReadUnaligned<float>(p + 8));

    private static MeshData ReadAscii(string path, CancellationToken cancellationToken)
    {
        var vertices = new List<Vector3>(4096);

        using (var reader = new StreamReader(path))
        {
            var lineNumber = 0;
            while (reader.ReadLine() is { } line)
            {
                if ((++lineNumber & 0xFFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var span = line.AsSpan().Trim();
                if (!span.StartsWith("vertex", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!TryParseTriple(span["vertex".Length..], out var vertex))
                {
                    throw new GeometryFormatException(
                        $"Malformed vertex on line {lineNumber} of '{Path.GetFileName(path)}'.");
                }

                vertices.Add(vertex);
            }
        }

        if (vertices.Count == 0)
        {
            throw new GeometryFormatException(
                $"'{Path.GetFileName(path)}' contains no vertices — not a readable STL.");
        }

        if (vertices.Count % 3 != 0)
        {
            throw new GeometryFormatException(
                $"'{Path.GetFileName(path)}' has {vertices.Count} vertices, which is not a whole number of triangles.");
        }

        return BuildFromVertices(vertices);
    }

    private static MeshData BuildFromVertices(List<Vector3> vertices)
    {
        var vertexCount = vertices.Count;
        var positions = GC.AllocateUninitializedArray<Vector3>(vertexCount);
        var normals = GC.AllocateUninitializedArray<Vector3>(vertexCount);
        var indices = GC.AllocateUninitializedArray<int>(vertexCount);
        var bounds = BoundingBox.Empty;

        for (var t = 0; t < vertexCount / 3; t++)
        {
            var i = t * 3;
            var v0 = vertices[i];
            var v1 = vertices[i + 1];
            var v2 = vertices[i + 2];

            var normal = Vector3.Cross(v1 - v0, v2 - v0);
            var lengthSquared = normal.LengthSquared();
            normal = lengthSquared > 1e-20f
                ? normal * (1f / MathF.Sqrt(lengthSquared))
                : Vector3.UnitZ;

            positions[i] = v0;
            positions[i + 1] = v1;
            positions[i + 2] = v2;
            normals[i] = normal;
            normals[i + 1] = normal;
            normals[i + 2] = normal;
            indices[i] = i;
            indices[i + 1] = i + 1;
            indices[i + 2] = i + 2;

            bounds = bounds.Union(v0).Union(v1).Union(v2);
        }

        return new MeshData
        {
            Positions = positions,
            Normals = normals,
            Indices = indices,
            Bounds = bounds,
        };
    }

    private static bool TryParseTriple(ReadOnlySpan<char> span, out Vector3 value)
    {
        value = default;
        Span<float> components = stackalloc float[3];

        for (var i = 0; i < 3; i++)
        {
            span = span.TrimStart();
            if (span.IsEmpty)
            {
                return false;
            }

            var separator = span.IndexOfAny(' ', '\t');
            var token = separator < 0 ? span : span[..separator];

            if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out components[i]))
            {
                return false;
            }

            span = separator < 0 ? [] : span[(separator + 1)..];
        }

        value = new Vector3(components[0], components[1], components[2]);
        return true;
    }
}
