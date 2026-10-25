using HelixToolkit;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using ModelExplorer.Geometry;

namespace ModelExplorer.App;

public static class ComparisonHeatmap
{
    public static Color4 Color(float distance, float tolerance, float maximum)
    {
        var green = new Color4(0x36 / 255f, 0xc9 / 255f, 0x8f / 255f, 1);
        var yellow = new Color4(0xf5 / 255f, 0xcd / 255f, 0x4c / 255f, 1);
        var red = new Color4(0xf0 / 255f, 0x51 / 255f, 0x4f / 255f, 1);
        if (distance <= tolerance) return green;
        var t = Math.Clamp((distance - tolerance) / MathF.Max(maximum - tolerance, 1e-12f), 0, 1);
        return t < 0.5f ? Color4.Lerp(green, yellow, t * 2) : Color4.Lerp(yellow, red, t * 2 - 1);
    }

    public static MeshGeometry3D Build(MeshData mesh, MeshDistanceResult result, float tolerance,
        float maximum, CancellationToken token = default)
    {
        // Unweld the output so shared vertices cannot overwrite another face's
        // deviation. Source meshes and their rendering buffers remain untouched.
        var geometry = new MeshGeometry3D
        {
            Positions = new Vector3Collection(mesh.Indices.Length),
            Normals = new Vector3Collection(mesh.Indices.Length),
            Indices = new IntCollection(mesh.Indices.Length),
            Colors = new Color4Collection(mesh.Indices.Length),
        };
        for (var triangle = 0; triangle < mesh.TriangleCount; triangle++)
        {
            if ((triangle & 1023) == 0) token.ThrowIfCancellationRequested();
            var color = Color(result.TriangleDistances[triangle], tolerance, maximum);
            for (var corner = 0; corner < 3; corner++)
            {
                var i = triangle * 3 + corner;
                var vertex = mesh.Indices[i];
                geometry.Positions.Add(mesh.Positions[vertex]);
                geometry.Normals.Add(mesh.Normals[vertex]);
                geometry.Indices.Add(i);
                geometry.Colors.Add(color);
            }
        }
        geometry.UpdateBounds();
        return geometry;
    }
}
