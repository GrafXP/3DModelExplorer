using System.Numerics;

namespace ModelExplorer.Geometry;

/// <summary>
/// Axis-aligned bounding box. Used to frame the camera on load and, later, to
/// show model dimensions in the index.
/// </summary>
public readonly record struct BoundingBox(Vector3 Min, Vector3 Max)
{
    /// <summary>
    /// Inverted box that absorbs any point on the first union. Starting from
    /// this rather than the origin avoids wrongly including (0,0,0) in the
    /// bounds of a model that doesn't sit at the origin.
    /// </summary>
    public static BoundingBox Empty { get; } = new(
        new Vector3(float.PositiveInfinity),
        new Vector3(float.NegativeInfinity));

    public bool IsEmpty => Min.X > Max.X;

    public Vector3 Size => IsEmpty ? Vector3.Zero : Max - Min;

    public Vector3 Center => IsEmpty ? Vector3.Zero : (Min + Max) * 0.5f;

    public float MaxExtent
    {
        get
        {
            var s = Size;
            return MathF.Max(s.X, MathF.Max(s.Y, s.Z));
        }
    }

    public BoundingBox Union(in BoundingBox other) =>
        new(Vector3.Min(Min, other.Min), Vector3.Max(Max, other.Max));

    public BoundingBox Union(in Vector3 point) =>
        new(Vector3.Min(Min, point), Vector3.Max(Max, point));
}
