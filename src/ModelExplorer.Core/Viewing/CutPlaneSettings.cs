using System.Numerics;
using ModelExplorer.Geometry;

namespace ModelExplorer.Core.Viewing;

/// <summary>
/// One freely oriented cutting plane, shared by every pane.
/// </summary>
/// <remarks>
/// Placed relative to each model's own bounding-box centre rather than at a world
/// position. Models put their geometry wherever the exporter left it, so a world
/// plane would miss most of a set; relative to the centre, the same plane takes
/// the same section through every variant being compared.
/// </remarks>
public readonly record struct CutPlaneSettings(Quaternion Orientation, Vector3 Origin, bool KeepOtherSide)
{
    /// <summary>Horizontal through the middle, keeping the upper half.</summary>
    public static CutPlaneSettings Default { get; } = new(Quaternion.Identity, Vector3.Zero, false);

    /// <summary>The plane's normal: local +Z turned by <see cref="Orientation"/>.</summary>
    public Vector3 Normal => Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, Orientation));

    /// <summary>Signed distance of the plane from the model centre, along the normal.</summary>
    public float Offset => Vector3.Dot(Origin, Normal);

    /// <summary>
    /// The plane for a model with these bounds, facing the half that is kept:
    /// <see cref="MeshPlaneCutter"/> keeps the side where it evaluates non-negative.
    /// </summary>
    public Plane For(BoundingBox bounds)
    {
        var normal = Normal;
        var point = bounds.Center + Origin;
        var plane = new Plane(normal, -Vector3.Dot(normal, point));
        return KeepOtherSide ? new Plane(-plane.Normal, -plane.D) : plane;
    }

    /// <summary>
    /// Local-to-world transform of the plane for a model with these bounds: the
    /// plane is authored in local XY with +Z as its normal.
    /// </summary>
    public Matrix4x4 Transform(BoundingBox bounds) =>
        Matrix4x4.CreateFromQuaternion(Orientation) * Matrix4x4.CreateTranslation(bounds.Center + Origin);

    /// <summary>The settings a plane transform stands for, after a manipulator has moved it.</summary>
    public CutPlaneSettings WithTransform(Matrix4x4 transform, BoundingBox bounds)
    {
        if (!Matrix4x4.Decompose(transform, out _, out var rotation, out var translation))
        {
            return this;
        }

        return this with { Orientation = Quaternion.Normalize(rotation), Origin = translation - bounds.Center };
    }

    /// <summary>The same orientation, at <paramref name="offset"/> from the centre along the normal.</summary>
    public CutPlaneSettings WithOffset(float offset) =>
        this with { Origin = Origin + (Normal * (offset - Offset)) };

    /// <summary>Faces <paramref name="normal"/> through the centre.</summary>
    public static CutPlaneSettings Facing(Vector3 normal)
    {
        normal = Vector3.Normalize(normal);
        var dot = Vector3.Dot(Vector3.UnitZ, normal);
        Quaternion orientation;
        if (dot > 0.9999f)
        {
            orientation = Quaternion.Identity;
        }
        else if (dot < -0.9999f)
        {
            orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
        }
        else
        {
            var axis = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, normal));
            orientation = Quaternion.CreateFromAxisAngle(axis, MathF.Acos(dot));
        }

        return new CutPlaneSettings(orientation, Vector3.Zero, false);
    }
}
