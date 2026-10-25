using System.Numerics;
using ModelExplorer.Geometry;

namespace ModelExplorer.Tests;

public class MeshComparisonTests
{
    internal static MeshData Mesh(Vector3[] positions, int[] indices) => new()
    {
        Positions = positions, Indices = indices,
        Normals = Enumerable.Repeat(Vector3.UnitZ, positions.Length).ToArray(),
        Bounds = positions.Aggregate(BoundingBox.Empty, (bounds, point) => bounds.Union(point)),
    };

    private static MeshData Square(float z = 0) => Mesh(
        [new(0, 0, z), new(10, 0, z), new(10, 10, z), new(0, 10, z)], [0, 1, 2, 0, 2, 3]);

    [Fact]
    public void DifferentTriangulationsOfTheSameSurfaceMatch()
    {
        var a = Square();
        var b = Mesh([.. a.Positions, new(5, 5, 0)], [0, 1, 4, 1, 2, 4, 2, 3, 4, 3, 0, 4]);
        var result = MeshComparison.Compare(a, b);
        Assert.InRange(result.MaxDistance, 0, 0.00001f);
        Assert.Equal(1, result.AToB.FractionWithin(0.001f));
        Assert.Equal(1, result.BToA.FractionWithin(0.001f));
    }

    [Fact]
    public void ParallelSurfacesHaveKnownDistanceIncludingTriangleInteriors()
    {
        var result = MeshComparison.Compare(Square(), Square(2));
        Assert.Equal(2, result.MaxDistance, 5);
        Assert.Equal(2, result.AToB.MeanDistance, 5);
        Assert.All(result.BToA.TriangleDistances, distance => Assert.Equal(2, distance, 5));
        Assert.Equal(0, result.BToA.FractionWithin(1.99f));
        Assert.Equal(1, result.BToA.FractionWithin(2));
    }

    [Fact]
    public void CheckingBothDirectionsFindsAdditionalGeometry()
    {
        var a = Square();
        var b = Mesh([.. a.Positions, new(1, 1, 3), new(2, 1, 3), new(1, 2, 3)], [.. a.Indices, 4, 5, 6]);
        var result = MeshComparison.Compare(a, b);
        Assert.InRange(result.AToB.MaxDistance, 0, 0.00001f);
        Assert.Equal(3, result.BToA.MaxDistance, 5);
        Assert.Equal(2.0 / 3, result.BToA.FractionWithin(0.01f), 5);
    }

    [Fact]
    public void CenterAlignmentRemovesTranslationWithoutRemovingScaleDifferences()
    {
        var a = Square();
        var moved = Mesh(a.Positions.Select(p => p + new Vector3(200, -50, 10)).ToArray(), a.Indices);
        Assert.True(MeshComparison.Compare(a, moved).MaxDistance > 100);
        Assert.InRange(MeshComparison.Compare(a, moved, true).MaxDistance, 0, 0.00001f);
        var scaled = Mesh(moved.Positions.Select(p => p * 2).ToArray(), moved.Indices);
        Assert.True(MeshComparison.Compare(a, scaled, true).MaxDistance > 5);
        Assert.Equal(new Vector3(200, -50, 10), moved.Positions[0]);
    }

    [Fact]
    public void FaceCenterSamplingDetectsAMissingInteriorWithMatchingVertices()
    {
        var full = Mesh([new(0, 0, 0), new(6, 0, 0), new(0, 6, 0)], [0, 1, 2]);
        var edges = Mesh(full.Positions, [0, 0, 1, 1, 1, 2, 2, 2, 0]);
        var result = MeshComparison.Compare(full, edges);
        Assert.InRange(result.AToB.MaxDistance, 1.41f, 1.42f);
        Assert.InRange(result.BToA.MaxDistance, 0, 0.00001f);
    }

    [Fact]
    public void DegeneratePointAndEdgeRemainFinite()
    {
        var point = Mesh([new(5, 3, 0)], [0, 0, 0]);
        var edge = Mesh([new(0, 0, 0), new(10, 0, 0)], [0, 0, 1]);
        var result = MeshComparison.Compare(point, edge);
        Assert.Equal(3, result.AToB.MaxDistance, 5);
        Assert.Equal(MathF.Sqrt(34), result.BToA.MaxDistance, 5);
    }

    [Fact]
    public void SpatialTreeFindsAnalyticDistanceToATessellatedPlane()
    {
        var points = new List<Vector3>();
        var indices = new List<int>();
        for (var x = 0; x < 20; x++)
        for (var y = 0; y < 20; y++)
        {
            var start = points.Count;
            points.AddRange([new(x, y, 0), new(x + 1, y, 0), new(x + 1, y + 1, 0), new(x, y + 1, 0)]);
            indices.AddRange([start, start + 1, start + 2, start, start + 2, start + 3]);
        }
        var random = new Random(7);
        var probes = Enumerable.Range(0, 80).Select(_ => new Vector3(
            random.NextSingle() * 30 - 5, random.NextSingle() * 30 - 5, random.NextSingle() * 8 - 4)).ToArray();
        var source = Mesh(probes, Enumerable.Range(0, probes.Length).SelectMany(i => new[] { i, i, i }).ToArray());
        var result = MeshComparison.Compare(source, Mesh(points.ToArray(), indices.ToArray()));
        for (var i = 0; i < probes.Length; i++)
        {
            var p = probes[i];
            var expected = Vector3.Distance(p, new(Math.Clamp(p.X, 0, 20), Math.Clamp(p.Y, 0, 20), 0));
            Assert.Equal(expected, result.AToB.TriangleDistances[i], 4);
        }
    }

    [Fact]
    public void EmptyInvalidAndCanceledInputsDoNotProduceAHeatmap()
    {
        Assert.Throws<ArgumentException>(() => MeshComparison.Compare(MeshData.Empty, Square()));
        Assert.Throws<ArgumentException>(() => MeshComparison.Compare(Mesh([Vector3.Zero], [0, 1, 2]), Square()));
        Assert.Throws<ArgumentException>(() => MeshComparison.Compare(Mesh([new(float.NaN, 0, 0)], [0, 0, 0]), Square()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => MeshComparison.Compare(Square(), Square(), cancellationToken: cancellation.Token));
    }
}
