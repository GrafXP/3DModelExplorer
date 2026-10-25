using System.Numerics;
using ModelExplorer.Geometry;

namespace ModelExplorer.Core.Viewing;

/// <summary>
/// A Z-up turntable camera: it looks at <see cref="Target"/> from a direction
/// given by <see cref="Yaw"/> and <see cref="Pitch"/>, from
/// <see cref="Distance"/> away.
/// </summary>
/// <remarks>
/// Z up, matching how slicers and printers present a model. Turntable rather
/// than trackball, because a print has a floor: the horizon never rolls, and a
/// drag that goes right and back again leaves the view where it started.
///
/// A value type, so a pane can hold a copy, and linking panes means sharing one.
/// </remarks>
public struct OrbitCamera
{
    /// <summary>Vertical field of view. Narrow enough that a part does not look distorted.</summary>
    public const float FieldOfView = 40f * MathF.PI / 180f;

    /// <summary>Just short of straight up or down, where the horizon would be undefined.</summary>
    public const float MaxPitch = 89f * MathF.PI / 180f;

    private const float RadiansPerPixel = 0.4f * MathF.PI / 180f;

    /// <summary>
    /// The direction the default view looks in: front-left-above. A three-quarter
    /// view makes depth legible at once, instead of showing a flat silhouette.
    /// </summary>
    public static readonly Vector3 DefaultDirection = Vector3.Normalize(new Vector3(0.55f, 0.75f, -0.42f));

    /// <summary>The point looked at. Without a pivot it is also what the view turns about.</summary>
    public Vector3 Target;

    /// <summary>Direction of <see cref="Forward"/> about Z, from +X towards +Y, in radians.</summary>
    public float Yaw;

    /// <summary>Elevation of <see cref="Forward"/>, in radians; negative looks down.</summary>
    public float Pitch;

    public float Distance;

    /// <summary>
    /// A point picked on a surface to turn about instead of the target. Without it,
    /// orbiting a large print always swings around its middle, which is useless when
    /// inspecting one corner of it.
    /// </summary>
    public Vector3? Pivot;

    /// <summary>From the camera towards the target.</summary>
    public readonly Vector3 Forward
    {
        get
        {
            var (sinYaw, cosYaw) = MathF.SinCos(Yaw);
            var (sinPitch, cosPitch) = MathF.SinCos(Pitch);
            return new Vector3(cosPitch * cosYaw, cosPitch * sinYaw, sinPitch);
        }
    }

    /// <summary>Right on the screen. Always level.</summary>
    public readonly Vector3 Right
    {
        get
        {
            var (sinYaw, cosYaw) = MathF.SinCos(Yaw);
            return new Vector3(sinYaw, -cosYaw, 0);
        }
    }

    /// <summary>Up on the screen.</summary>
    public readonly Vector3 Up => Vector3.Cross(Right, Forward);

    public readonly Vector3 Position => Target - (Forward * Distance);

    /// <summary>Whether this camera has been placed at all.</summary>
    public readonly bool IsSet => Distance > 0;

    /// <summary>A camera looking along <paramref name="direction"/> at <paramref name="target"/>.</summary>
    public static OrbitCamera Looking(Vector3 direction, Vector3 target, float distance)
    {
        var camera = new OrbitCamera { Target = target, Distance = distance };
        camera.SetDirection(direction);
        return camera;
    }

    /// <summary>
    /// The default view of <paramref name="bounds"/> in a view
    /// <paramref name="aspect"/> times as wide as it is high.
    /// </summary>
    /// <remarks>
    /// Fits the bounding sphere into the tighter of the two screen dimensions, so it
    /// holds at any orientation and in the narrow panes of a large grid.
    /// </remarks>
    public static OrbitCamera Framing(BoundingBox bounds, float aspect) =>
        Looking(DefaultDirection, bounds.IsEmpty ? Vector3.Zero : bounds.Center, FramingDistance(bounds, aspect));

    /// <summary>How far away a camera has to be to show all of <paramref name="bounds"/>.</summary>
    public static float FramingDistance(BoundingBox bounds, float aspect)
    {
        var radius = bounds.IsEmpty ? 1f : bounds.Size.Length() * 0.5f;
        if (radius <= float.Epsilon)
        {
            radius = 1f;
        }

        var halfVertical = FieldOfView / 2;
        var halfHorizontal = MathF.Atan(MathF.Tan(halfVertical) * MathF.Max(aspect, 0.01f));
        var half = MathF.Min(halfVertical, halfHorizontal);

        // Room around the sphere, so the model does not touch the pane's edges.
        return radius / MathF.Sin(half) * 1.35f;
    }

    /// <summary>Looks along <paramref name="direction"/>, keeping the target and distance.</summary>
    public void SetDirection(Vector3 direction)
    {
        if (direction.LengthSquared() <= float.Epsilon)
        {
            return;
        }

        direction = Vector3.Normalize(direction);
        Yaw = MathF.Atan2(direction.Y, direction.X);
        Pitch = Math.Clamp(MathF.Asin(Math.Clamp(direction.Z, -1f, 1f)), -MaxPitch, MaxPitch);
    }

    /// <summary>
    /// Turns the view by a mouse drag of (<paramref name="dx"/>, <paramref name="dy"/>)
    /// pixels: the model follows the mouse as if it had been grabbed.
    /// </summary>
    public void Orbit(float dx, float dy)
    {
        var deltaYaw = -dx * RadiansPerPixel;
        var newPitch = Math.Clamp(Pitch - (dy * RadiansPerPixel), -MaxPitch, MaxPitch);
        var deltaPitch = newPitch - Pitch;

        if (Pivot is { } pivot)
        {
            // Camera and target turn rigidly about the pivot: first about the
            // screen's right axis, then about world Z, both through the pivot.
            var offset = Target - pivot;
            offset = Vector3.Transform(offset, Quaternion.CreateFromAxisAngle(Right, deltaPitch));
            offset = Vector3.Transform(offset, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, deltaYaw));
            Target = pivot + offset;
        }

        Yaw += deltaYaw;
        Pitch = newPitch;
    }

    /// <summary>
    /// Moves the view so the scene follows a drag of (<paramref name="dx"/>,
    /// <paramref name="dy"/>) pixels in a view <paramref name="viewHeight"/> pixels high.
    /// </summary>
    public void Pan(float dx, float dy, float viewHeight)
    {
        var unitsPerPixel = 2 * MathF.Tan(FieldOfView / 2) * Distance / MathF.Max(viewHeight, 1);
        Target += ((Up * dy) - (Right * dx)) * unitsPerPixel;
    }

    /// <summary>
    /// Comes closer by <paramref name="factor"/> (below 1) or goes further away
    /// (above 1), keeping the point under the cursor where it is. The cursor is
    /// given from -1 to 1 across the view, <paramref name="ndcY"/> up.
    /// </summary>
    public void Zoom(float factor, float ndcX, float ndcY, float aspect)
    {
        if (!float.IsFinite(factor) || factor <= 0)
        {
            return;
        }

        var t = MathF.Tan(FieldOfView / 2) * Distance;
        var underCursor = Target + (Right * (ndcX * t * aspect)) + (Up * (ndcY * t));
        Target = underCursor + ((Target - underCursor) * factor);
        Distance = MathF.Max(Distance * factor, 1e-5f);
    }

    /// <summary>World to view space, right-handed.</summary>
    public readonly Matrix4x4 View => Matrix4x4.CreateLookAt(Position, Target, Vector3.UnitZ);

    /// <summary>
    /// View to clip space for a view <paramref name="aspect"/> times as wide as it is
    /// high. Depth is reversed — 1 at <paramref name="near"/>, 0 at infinity — which
    /// with a float depth buffer keeps it precise however far the scene reaches, so
    /// there is no far plane to fit and no z-fighting to trade it against.
    /// </summary>
    public static Matrix4x4 Projection(float aspect, float near)
    {
        var f = 1 / MathF.Tan(FieldOfView / 2);
        return new Matrix4x4(
            f / MathF.Max(aspect, 1e-4f), 0, 0, 0,
            0, f, 0, 0,
            0, 0, 0, -1,
            0, 0, near, 0);
    }

    /// <summary>
    /// A near plane that never cuts into a model of this size around this centre:
    /// a fraction of the distance to its near side, collapsing to a sliver of the
    /// model's own size once the camera is inside its bounding sphere.
    /// </summary>
    public readonly float NearPlane(Vector3 sceneCenter, float sceneRadius)
    {
        var distance = Vector3.Distance(Position, sceneCenter);
        return MathF.Max(MathF.Max((distance - sceneRadius) * 0.5f, sceneRadius * 1e-4f), 1e-5f);
    }

    /// <summary>The ray from the camera through a point of the view given from -1 to 1, y up.</summary>
    public readonly (Vector3 Origin, Vector3 Direction) Ray(float ndcX, float ndcY, float aspect)
    {
        var t = MathF.Tan(FieldOfView / 2);
        var direction = Forward + (Right * (ndcX * t * aspect)) + (Up * (ndcY * t));
        return (Position, Vector3.Normalize(direction));
    }

    /// <summary>Where a world point appears in a view of this size, in its pixels; null behind the camera.</summary>
    public readonly Vector2? Project(Vector3 world, float width, float height)
    {
        var view = Vector3.Transform(world, View);
        if (view.Z >= -1e-6f)
        {
            return null;
        }

        var f = 1 / MathF.Tan(FieldOfView / 2);
        var aspect = width / MathF.Max(height, 1);
        var ndcX = view.X * f / aspect / -view.Z;
        var ndcY = view.Y * f / -view.Z;
        return new Vector2((ndcX + 1) * 0.5f * width, (1 - ndcY) * 0.5f * height);
    }

    /// <summary>The same view seen from a camera moved by <paramref name="offset"/>.</summary>
    public readonly OrbitCamera Translated(Vector3 offset)
    {
        var copy = this;
        copy.Target += offset;
        if (copy.Pivot is { } pivot)
        {
            copy.Pivot = pivot + offset;
        }

        return copy;
    }
}
