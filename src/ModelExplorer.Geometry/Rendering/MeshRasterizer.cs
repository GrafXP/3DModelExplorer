using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace ModelExplorer.Geometry.Rendering;

/// <summary>
/// Renders a mesh to a small RGBA image on the CPU.
/// </summary>
/// <remarks>
/// A thumbnail is 256 pixels. At that size the GPU buys nothing — the cost is
/// dominated by parsing the file, not by filling 65k pixels — while a second
/// DirectX device would contend with the interactive viewport for the GPU, need
/// its own STA thread and window, and fall over on a machine with no hardware
/// device at all. Doing it in software removes that whole class of problem, runs
/// on as many worker threads as we like, and is deterministic enough to test.
///
/// The camera framing mirrors the viewer's, so a tile looks like what clicking it
/// produces.
/// </remarks>
public static class MeshRasterizer
{
    /// <summary>
    /// Rendered at this multiple of the requested size and box-filtered down.
    /// Edges of a faceted model are the whole silhouette at thumbnail scale, and
    /// unfiltered they crawl badly.
    /// </summary>
    public const int Supersample = 2;

    /// <summary>Matches the viewer: front-left-above, so depth reads immediately.</summary>
    private static readonly Vector3 ViewDirection = Vector3.Normalize(new Vector3(0.55f, 0.75f, -0.42f));

    private const float FieldOfViewRadians = 45f * MathF.PI / 180f;

    /// <summary>Share of the half-frame the model is zoomed to fill.</summary>
    private const float FrameFill = 0.92f;

    // The viewer's lighting, so tiles and the viewport agree.
    private static readonly Vector3 KeyLight = Vector3.Normalize(new Vector3(-0.6f, -0.8f, -0.9f));
    private static readonly Vector3 FillLight = Vector3.Normalize(new Vector3(0.8f, 0.4f, 0.35f));
    private static readonly Vector3 KeyColor = new(1f, 1f, 1f);
    private static readonly Vector3 FillColor = new(0.345f, 0.360f, 0.392f);
    private static readonly Vector3 Ambient = new(0.188f, 0.200f, 0.219f);
    private static readonly Vector3 Diffuse = new(0.62f, 0.67f, 0.74f);
    private static readonly Vector3 Specular = new(0.22f, 0.23f, 0.25f);
    private const float Shininess = 24f;

    /// <summary>
    /// Renders <paramref name="mesh"/> into a straight (non-premultiplied) RGBA
    /// buffer of <paramref name="size"/> square. Background pixels are fully
    /// transparent so the tile's own background shows through.
    /// </summary>
    public static RasterImage Render(MeshData mesh, int size, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);

        var scale = size * Supersample;
        var pixels = scale * scale;

        // Pooled, not allocated. At 256 px the three buffers are about 5 MB, all
        // of it over the large-object threshold, and a grid scroll renders them
        // back to back on several threads at once. Allocating each time would
        // churn the large object heap — which is never compacted — and the
        // process would hold on to the fragments long after the scroll stopped.
        var colour = ArrayPool<Vector3>.Shared.Rent(pixels);
        var coverage = ArrayPool<float>.Shared.Rent(pixels);
        var depth = ArrayPool<float>.Shared.Rent(pixels);

        try
        {
            // Rented arrays carry the previous caller's contents. Colour needs no
            // clearing because it is only ever read where coverage is non-zero.
            Array.Clear(coverage, 0, pixels);
            Array.Fill(depth, float.PositiveInfinity, 0, pixels);

            if (mesh.TriangleCount > 0 && !mesh.Bounds.IsEmpty)
            {
                var camera = Camera.Frame(mesh.Bounds, FieldOfViewRadians, FrameFill);
                Draw(mesh, camera, scale, colour, coverage, depth, cancellationToken);
            }

            return Downsample(colour, coverage, size, scale);
        }
        finally
        {
            ArrayPool<Vector3>.Shared.Return(colour);
            ArrayPool<float>.Shared.Return(coverage);
            ArrayPool<float>.Shared.Return(depth);
        }
    }

    private static void Draw(
        MeshData mesh,
        Camera camera,
        int scale,
        Vector3[] colour,
        float[] coverage,
        float[] depth,
        CancellationToken cancellationToken)
    {
        var positions = mesh.Positions;
        var indices = mesh.Indices;
        var half = scale * 0.5f;

        for (var t = 0; t + 2 < indices.Length; t += 3)
        {
            if ((t & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var w0 = positions[indices[t]];
            var w1 = positions[indices[t + 1]];
            var w2 = positions[indices[t + 2]];

            var v0 = camera.ToView(w0);
            var v1 = camera.ToView(w1);
            var v2 = camera.ToView(w2);

            // Anything touching or behind the eye plane is dropped whole rather
            // than clipped: at thumbnail scale the framing guarantees the model
            // is well in front of the camera, so this only ever fires on stray
            // geometry far outside the bounds it was framed from.
            if (v0.Z <= camera.Near || v1.Z <= camera.Near || v2.Z <= camera.Near)
            {
                continue;
            }

            var s0 = camera.ToScreen(v0, half);
            var s1 = camera.ToScreen(v1, half);
            var s2 = camera.ToScreen(v2, half);

            var area = ((s1.X - s0.X) * (s2.Y - s0.Y)) - ((s1.Y - s0.Y) * (s2.X - s0.X));
            if (area == 0)
            {
                continue;
            }

            // Wound the other way? Swap two vertices rather than culling. STL has
            // no reliable winding, and a model with inconsistent facets would
            // otherwise render full of holes.
            if (area < 0)
            {
                (s1, s2) = (s2, s1);
                (v1, v2) = (v2, v1);
                area = -area;
            }

            var minX = Math.Max(0, (int)MathF.Floor(Min3(s0.X, s1.X, s2.X)));
            var maxX = Math.Min(scale - 1, (int)MathF.Ceiling(Max3(s0.X, s1.X, s2.X)));
            var minY = Math.Max(0, (int)MathF.Floor(Min3(s0.Y, s1.Y, s2.Y)));
            var maxY = Math.Min(scale - 1, (int)MathF.Ceiling(Max3(s0.Y, s1.Y, s2.Y)));

            if (minX > maxX || minY > maxY)
            {
                continue;
            }

            // Flat shaded from the geometric normal. The mesh is unwelded — every
            // triangle already carries one normal across its three vertices — so
            // interpolating would only reproduce this value.
            var normal = Vector3.Cross(w1 - w0, w2 - w0);
            var lengthSquared = normal.LengthSquared();
            if (lengthSquared <= 1e-20f)
            {
                continue;
            }

            normal *= 1f / MathF.Sqrt(lengthSquared);
            var shade = Shade(normal, camera.Eye, (w0 + w1 + w2) / 3f);

            var inverseArea = 1f / area;

            for (var y = minY; y <= maxY; y++)
            {
                var py = y + 0.5f;
                var row = y * scale;

                for (var x = minX; x <= maxX; x++)
                {
                    var px = x + 0.5f;

                    var e0 = ((s1.X - s0.X) * (py - s0.Y)) - ((s1.Y - s0.Y) * (px - s0.X));
                    var e1 = ((s2.X - s1.X) * (py - s1.Y)) - ((s2.Y - s1.Y) * (px - s1.X));
                    var e2 = ((s0.X - s2.X) * (py - s2.Y)) - ((s0.Y - s2.Y) * (px - s2.X));

                    if (e0 < 0 || e1 < 0 || e2 < 0)
                    {
                        continue;
                    }

                    // Barycentric weights, used only for depth. Interpolating the
                    // reciprocal is what makes it correct under perspective.
                    var b0 = e1 * inverseArea;
                    var b1 = e2 * inverseArea;
                    var b2 = e0 * inverseArea;
                    var z = 1f / ((b0 / v0.Z) + (b1 / v1.Z) + (b2 / v2.Z));

                    var i = row + x;
                    if (z >= depth[i])
                    {
                        continue;
                    }

                    depth[i] = z;
                    colour[i] = shade;
                    coverage[i] = 1f;
                }
            }
        }
    }

    /// <summary>
    /// Two directional lights plus ambient, two-sided. Normals are flipped toward
    /// the eye instead of back-facing triangles being culled, so a model with
    /// inverted facets is merely lit oddly rather than full of holes.
    /// </summary>
    private static Vector3 Shade(Vector3 normal, Vector3 eye, Vector3 point)
    {
        var toEye = Vector3.Normalize(eye - point);
        if (Vector3.Dot(normal, toEye) < 0)
        {
            normal = -normal;
        }

        var colour = Ambient * Diffuse;
        colour += Contribution(normal, toEye, -KeyLight, KeyColor);
        colour += Contribution(normal, toEye, -FillLight, FillColor);

        return Vector3.Clamp(colour, Vector3.Zero, Vector3.One);
    }

    private static Vector3 Contribution(Vector3 normal, Vector3 toEye, Vector3 toLight, Vector3 lightColour)
    {
        var lambert = Vector3.Dot(normal, toLight);
        if (lambert <= 0)
        {
            return Vector3.Zero;
        }

        var result = Diffuse * lightColour * lambert;

        // Blinn-Phong: the half vector avoids computing a reflection per pixel.
        var half = Vector3.Normalize(toLight + toEye);
        var highlight = MathF.Max(Vector3.Dot(normal, half), 0);
        if (highlight > 0)
        {
            result += Specular * lightColour * MathF.Pow(highlight, Shininess);
        }

        return result;
    }

    /// <summary>
    /// Box-filters the supersampled buffer down and converts to 8-bit sRGB-ish
    /// bytes. Coverage becomes alpha, so silhouette edges fade out rather than
    /// stepping, and colour is divided by coverage so partially covered pixels
    /// keep their full brightness instead of darkening toward the background.
    /// </summary>
    private static RasterImage Downsample(Vector3[] colour, float[] coverage, int size, int scale)
    {
        var pixels = new byte[size * size * 4];
        var samples = Supersample * Supersample;

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var sum = Vector3.Zero;
                var covered = 0f;

                for (var sy = 0; sy < Supersample; sy++)
                {
                    var row = ((y * Supersample) + sy) * scale;
                    for (var sx = 0; sx < Supersample; sx++)
                    {
                        var i = row + (x * Supersample) + sx;
                        sum += colour[i];
                        covered += coverage[i];
                    }
                }

                var offset = ((y * size) + x) * 4;
                if (covered <= 0)
                {
                    continue;
                }

                var rgb = sum / covered;
                pixels[offset] = ToByte(rgb.X);
                pixels[offset + 1] = ToByte(rgb.Y);
                pixels[offset + 2] = ToByte(rgb.Z);
                pixels[offset + 3] = ToByte(covered / samples);
            }
        }

        return new RasterImage(size, pixels);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ToByte(float value) => (byte)Math.Clamp((int)((value * 255f) + 0.5f), 0, 255);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Min3(float a, float b, float c) => MathF.Min(a, MathF.Min(b, c));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Max3(float a, float b, float c) => MathF.Max(a, MathF.Max(b, c));

    /// <summary>
    /// A perspective camera framed on the model's bounding sphere, so the fit
    /// holds at any orientation.
    /// </summary>
    private readonly struct Camera
    {
        private readonly Vector3 _right;
        private readonly Vector3 _up;
        private readonly Vector3 _forward;
        private readonly float _focal;

        private Camera(Vector3 eye, Vector3 right, Vector3 up, Vector3 forward, float focal, float near)
        {
            Eye = eye;
            _right = right;
            _up = up;
            _forward = forward;
            _focal = focal;
            Near = near;
        }

        public Vector3 Eye { get; }

        public float Near { get; }

        public static Camera Frame(BoundingBox bounds, float fieldOfView, float fill)
        {
            var centre = bounds.Center;
            var radius = MathF.Max(bounds.Size.Length() * 0.5f, 1e-4f);

            // Stand off the bounding sphere, so the camera is clear of the model
            // whatever its shape and the near plane is never an issue.
            var distance = radius / MathF.Sin(fieldOfView * 0.5f) * 1.05f;

            var forward = ViewDirection;
            var eye = centre - (forward * distance);

            // Z-up, matching how slicers present a model. The world up is only
            // degenerate if the view direction is vertical, which it is not.
            var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitZ));
            var up = Vector3.Cross(right, forward);

            // Half the image spans tan(fov/2) at unit depth.
            var focal = 1f / MathF.Tan(fieldOfView * 0.5f);
            var near = MathF.Max(distance - radius, radius * 1e-3f) * 0.5f;
            var camera = new Camera(eye, right, up, forward, focal, near);

            // Then zoom in until the box just fits. Fitting the sphere alone is
            // correct but wasteful: it is the enclosing ball of the diagonal, so
            // a plate or a bracket — most of a print library — would sit in the
            // middle of a large empty tile. Fitting the projected corners instead
            // gives every thumbnail a consistent apparent size.
            var extent = 0f;
            foreach (var corner in Corners(bounds))
            {
                var view = camera.ToView(corner);
                if (view.Z <= near)
                {
                    continue;
                }

                var projected = focal / view.Z;
                extent = MathF.Max(extent, MathF.Abs(view.X) * projected);
                extent = MathF.Max(extent, MathF.Abs(view.Y) * projected);
            }

            return extent > 1e-6f
                ? new Camera(eye, right, up, forward, focal * fill / extent, near)
                : camera;
        }

        private static Vector3[] Corners(BoundingBox bounds)
        {
            var (min, max) = (bounds.Min, bounds.Max);
            return
            [
                new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z),
                new(min.X, max.Y, min.Z), new(max.X, max.Y, min.Z),
                new(min.X, min.Y, max.Z), new(max.X, min.Y, max.Z),
                new(min.X, max.Y, max.Z), new(max.X, max.Y, max.Z),
            ];
        }

        /// <summary>View space: X right, Y up, Z forward (depth), all positive in front.</summary>
        public Vector3 ToView(Vector3 world)
        {
            var d = world - Eye;
            return new Vector3(Vector3.Dot(d, _right), Vector3.Dot(d, _up), Vector3.Dot(d, _forward));
        }

        /// <summary>Pixel coordinates, Y down.</summary>
        public Vector2 ToScreen(Vector3 view, float half)
        {
            var projected = _focal / view.Z;
            return new Vector2(
                half + (view.X * projected * half),
                half - (view.Y * projected * half));
        }
    }
}

/// <summary>A straight (non-premultiplied) RGBA8 image, row-major from the top.</summary>
public sealed class RasterImage(int size, byte[] pixels)
{
    public int Size { get; } = size;

    public byte[] Pixels { get; } = pixels;

    /// <summary>Whether anything was drawn at all.</summary>
    public bool IsEmpty
    {
        get
        {
            for (var i = 3; i < Pixels.Length; i += 4)
            {
                if (Pixels[i] != 0)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
