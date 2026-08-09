using System.Numerics;

namespace ModelExplorer.Geometry;

/// <summary>
/// A renderer-agnostic triangle mesh.
/// </summary>
/// <remarks>
/// Geometry is stored unwelded: three distinct vertices per triangle, with the
/// face normal replicated across them. That is deliberate. STL has no vertex
/// sharing to begin with, printable models are faceted rather than smooth, and
/// welding would cost more time than it saves on load. It does mean vertex
/// count is always <c>3 × TriangleCount</c>.
/// </remarks>
public sealed class MeshData
{
    public required Vector3[] Positions { get; init; }

    public required Vector3[] Normals { get; init; }

    public required int[] Indices { get; init; }

    public required BoundingBox Bounds { get; init; }

    public int TriangleCount => Indices.Length / 3;

    public int VertexCount => Positions.Length;

    /// <summary>Approximate managed footprint, for diagnostics and cache sizing.</summary>
    public long ApproximateBytes =>
        ((long)Positions.Length * 12) + ((long)Normals.Length * 12) + ((long)Indices.Length * 4);

    public static MeshData Empty { get; } = new()
    {
        Positions = [],
        Normals = [],
        Indices = [],
        Bounds = BoundingBox.Empty,
    };
}
