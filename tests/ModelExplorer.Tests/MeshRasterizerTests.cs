using System.Numerics;
using ModelExplorer.Geometry;
using ModelExplorer.Geometry.Rendering;

namespace ModelExplorer.Tests;

public sealed class MeshRasterizerTests
{
    [Fact]
    public void An_empty_mesh_renders_a_fully_transparent_image()
    {
        var image = MeshRasterizer.Render(MeshData.Empty, 64);

        Assert.Equal(64, image.Size);
        Assert.Equal(64 * 64 * 4, image.Pixels.Length);
        Assert.True(image.IsEmpty);
    }

    [Fact]
    public void A_cube_covers_a_plausible_share_of_the_frame()
    {
        var image = MeshRasterizer.Render(Cube(10f), 128);

        Assert.False(image.IsEmpty);

        // The camera zooms until the projected bounding box fills the frame, so a
        // cube's hexagonal silhouette should take up most of the tile. The bound
        // is what stops the framing quietly regressing to the bounding-sphere fit
        // it replaced, which left a cube at roughly a quarter of the frame.
        var covered = Coverage(image);
        Assert.InRange(covered, 0.40, 0.70);
    }

    [Fact]
    public void The_model_is_centred_in_the_frame()
    {
        var image = MeshRasterizer.Render(Cube(10f), 128);
        var (x, y) = Centroid(image);

        Assert.InRange(x, 0.44, 0.56);
        Assert.InRange(y, 0.44, 0.56);
    }

    /// <summary>
    /// Framing comes from the bounding sphere, so a model's size and where it
    /// sits in space must not change the picture.
    /// </summary>
    [Fact]
    public void Framing_is_independent_of_scale_and_position()
    {
        var small = MeshRasterizer.Render(Cube(1f), 96);
        var large = MeshRasterizer.Render(Cube(500f, new Vector3(-3000f, 812f, 47f)), 96);

        Assert.Equal(small.Pixels, large.Pixels);
    }

    [Fact]
    public void Rendering_is_deterministic()
    {
        var mesh = Cube(10f);

        Assert.Equal(
            MeshRasterizer.Render(mesh, 64).Pixels,
            MeshRasterizer.Render(mesh, 64).Pixels);
    }

    /// <summary>
    /// STL carries no reliable winding. Facets wound the other way have to render
    /// the same rather than vanishing, or half of a real-world model disappears.
    /// </summary>
    [Fact]
    public void Reversed_winding_renders_the_same_silhouette()
    {
        var forward = MeshRasterizer.Render(Cube(10f), 96);
        var reversed = MeshRasterizer.Render(Reverse(Cube(10f)), 96);

        Assert.Equal(Silhouette(forward), Silhouette(reversed));
    }

    /// <summary>
    /// The near face has to win. Without a depth test the result depends on
    /// triangle order, and a cube would show its inside.
    /// </summary>
    [Fact]
    public void The_nearer_surface_wins_regardless_of_draw_order()
    {
        var mesh = Cube(10f);
        var reordered = new MeshData
        {
            Positions = mesh.Positions,
            Normals = mesh.Normals,
            Indices = [.. mesh.Indices.Chunk(3).Reverse().SelectMany(triangle => triangle)],
            Bounds = mesh.Bounds,
        };

        Assert.Equal(
            MeshRasterizer.Render(mesh, 96).Pixels,
            MeshRasterizer.Render(reordered, 96).Pixels);
    }

    /// <summary>
    /// Supersampling exists to give the silhouette soft edges; if every pixel is
    /// either fully opaque or fully clear it is not doing anything.
    /// </summary>
    [Fact]
    public void Silhouette_edges_are_antialiased()
    {
        var image = MeshRasterizer.Render(Cube(10f), 128);

        var partial = 0;
        for (var i = 3; i < image.Pixels.Length; i += 4)
        {
            if (image.Pixels[i] is > 0 and < 255)
            {
                partial++;
            }
        }

        Assert.True(partial > 0, "expected partially covered pixels along the silhouette");
    }

    /// <summary>
    /// Three faces of a cube are visible from a three-quarter view and each meets
    /// the light differently. One flat tone means the shading is not running.
    /// </summary>
    [Fact]
    public void Lit_faces_are_shaded_differently()
    {
        var image = MeshRasterizer.Render(Cube(10f), 128);

        var tones = new HashSet<int>();
        for (var i = 0; i < image.Pixels.Length; i += 4)
        {
            if (image.Pixels[i + 3] == 255)
            {
                tones.Add((image.Pixels[i] << 16) | (image.Pixels[i + 1] << 8) | image.Pixels[i + 2]);
            }
        }

        Assert.True(tones.Count >= 3, $"expected at least three tones, saw {tones.Count}");
    }

    [Fact]
    public void A_degenerate_triangle_does_not_throw()
    {
        var point = new Vector3(1, 2, 3);
        var mesh = new MeshData
        {
            Positions = [point, point, point],
            Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            Indices = [0, 1, 2],
            Bounds = BoundingBox.Empty.Union(point),
        };

        Assert.True(MeshRasterizer.Render(mesh, 32).IsEmpty);
    }

    [Fact]
    public void Rendering_is_cancellable()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => MeshRasterizer.Render(Cube(10f), 64, cancellation.Token));
    }

    private static double Coverage(RasterImage image)
    {
        var covered = 0;
        for (var i = 3; i < image.Pixels.Length; i += 4)
        {
            if (image.Pixels[i] > 127)
            {
                covered++;
            }
        }

        return (double)covered / (image.Size * image.Size);
    }

    private static (double X, double Y) Centroid(RasterImage image)
    {
        double sumX = 0, sumY = 0;
        var count = 0;

        for (var y = 0; y < image.Size; y++)
        {
            for (var x = 0; x < image.Size; x++)
            {
                if (image.Pixels[(((y * image.Size) + x) * 4) + 3] > 127)
                {
                    sumX += x;
                    sumY += y;
                    count++;
                }
            }
        }

        Assert.True(count > 0);
        return (sumX / count / image.Size, sumY / count / image.Size);
    }

    private static bool[] Silhouette(RasterImage image)
    {
        var mask = new bool[image.Size * image.Size];
        for (var i = 0; i < mask.Length; i++)
        {
            mask[i] = image.Pixels[(i * 4) + 3] > 127;
        }

        return mask;
    }

    private static MeshData Reverse(MeshData mesh) => new()
    {
        Positions = mesh.Positions,
        Normals = [.. mesh.Normals.Select(normal => -normal)],
        Indices = [.. mesh.Indices.Chunk(3).SelectMany(t => new[] { t[0], t[2], t[1] })],
        Bounds = mesh.Bounds,
    };

    /// <summary>An axis-aligned cube, unwelded the way the parsers produce it.</summary>
    private static MeshData Cube(float side, Vector3 centre = default)
    {
        var h = side * 0.5f;
        Vector3[] corners =
        [
            centre + new Vector3(-h, -h, -h),
            centre + new Vector3(+h, -h, -h),
            centre + new Vector3(+h, +h, -h),
            centre + new Vector3(-h, +h, -h),
            centre + new Vector3(-h, -h, +h),
            centre + new Vector3(+h, -h, +h),
            centre + new Vector3(+h, +h, +h),
            centre + new Vector3(-h, +h, +h),
        ];

        int[][] faces =
        [
            [0, 3, 2, 1], // bottom
            [4, 5, 6, 7], // top
            [0, 1, 5, 4],
            [1, 2, 6, 5],
            [2, 3, 7, 6],
            [3, 0, 4, 7],
        ];

        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var indices = new List<int>();
        var bounds = BoundingBox.Empty;

        foreach (var face in faces)
        {
            foreach (var (a, b, c) in new[] { (face[0], face[1], face[2]), (face[0], face[2], face[3]) })
            {
                var v0 = corners[a];
                var v1 = corners[b];
                var v2 = corners[c];
                var normal = Vector3.Normalize(Vector3.Cross(v1 - v0, v2 - v0));

                foreach (var vertex in new[] { v0, v1, v2 })
                {
                    indices.Add(positions.Count);
                    positions.Add(vertex);
                    normals.Add(normal);
                    bounds = bounds.Union(vertex);
                }
            }
        }

        return new MeshData
        {
            Positions = [.. positions],
            Normals = [.. normals],
            Indices = [.. indices],
            Bounds = bounds,
        };
    }
}
