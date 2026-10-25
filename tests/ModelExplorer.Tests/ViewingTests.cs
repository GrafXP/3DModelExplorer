using System.Numerics;
using ModelExplorer.Core.Viewing;
using ModelExplorer.Geometry;

namespace ModelExplorer.Tests;

public class CutPlaneSettingsTests
{
    private static readonly BoundingBox Box = new(new(0, 0, 0), new(10, 20, 30));

    [Fact]
    public void TheDefaultKeepsTheUpperHalfThroughTheCentre()
    {
        var plane = CutPlaneSettings.Default.For(Box);

        Assert.Equal(Vector3.UnitZ, plane.Normal);
        Assert.Equal(0, Plane.DotCoordinate(plane, Box.Center), 4);
        Assert.True(Plane.DotCoordinate(plane, new Vector3(5, 10, 29)) > 0);
        Assert.True(Plane.DotCoordinate(plane, new Vector3(5, 10, 1)) < 0);
    }

    [Fact]
    public void FlippingKeepsTheOtherSide()
    {
        var plane = (CutPlaneSettings.Default with { KeepOtherSide = true }).For(Box);

        Assert.True(Plane.DotCoordinate(plane, new Vector3(5, 10, 1)) > 0);
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 0, -1)]
    [InlineData(0.6f, 0, 0.8f)]
    public void FacingTurnsThePlaneOntoTheNormal(float x, float y, float z)
    {
        var normal = Vector3.Normalize(new Vector3(x, y, z));

        Assert.InRange(Vector3.Distance(CutPlaneSettings.Facing(normal).Normal, normal), 0, 1e-5f);
    }

    [Fact]
    public void AManipulatedTransformMapsBackToTheSameSettings()
    {
        var settings = CutPlaneSettings.Facing(Vector3.Normalize(new Vector3(1, 2, 3))) with { Origin = new Vector3(1, -2, 3) };

        var again = CutPlaneSettings.Default.WithTransform(settings.Transform(Box), Box);

        Assert.InRange(Vector3.Distance(settings.Normal, again.Normal), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(settings.Origin, again.Origin), 0, 1e-4f);
    }

    [Fact]
    public void TheOffsetMovesThePlaneAlongItsNormal()
    {
        var settings = CutPlaneSettings.Facing(Vector3.UnitX).WithOffset(4);

        Assert.Equal(4, settings.Offset, 5);
        Assert.Equal(-(Box.Center.X + 4), settings.For(Box).D, 4);
    }
}

public class MeshPickingTests
{
    private static readonly MeshData Cube = TestMeshes.Cube(0, 10);

    [Fact]
    public void TheNearestSurfaceIsHit()
    {
        var t = MeshPicking.Raycast(Cube, new Vector3(5, 5, 25), -Vector3.UnitZ);

        Assert.Equal(15, t!.Value, 4);
    }

    [Fact]
    public void WhatACutRemovedIsNotHit()
    {
        // Keep the lower half: the top face is gone, the ray goes on to the bottom.
        var keepBelowFive = new Plane(-Vector3.UnitZ, 5);

        var t = MeshPicking.Raycast(Cube, new Vector3(5, 5, 25), -Vector3.UnitZ, keepBelowFive);

        Assert.Equal(25, t!.Value, 4);
    }

    [Fact]
    public void EmptySpaceIsAMiss()
    {
        Assert.Null(MeshPicking.Raycast(Cube, new Vector3(50, 5, 25), -Vector3.UnitZ));
        Assert.Null(MeshPicking.Raycast(MeshData.Empty, Vector3.Zero, Vector3.UnitX));
    }
}

public class HeatmapScaleTests
{
    [Fact]
    public void TheScaleRunsFromGreenThroughYellowToRed()
    {
        Assert.Equal(HeatmapScale.Within, HeatmapScale.Color(0, 0.1f, 2));
        Assert.Equal(HeatmapScale.Within, HeatmapScale.Color(0.1f, 0.1f, 2));
        Assert.InRange(Vector3.Distance(HeatmapScale.Middle, HeatmapScale.Color(1.05f, 0.1f, 2)), 0, 1e-5f);
        Assert.Equal(HeatmapScale.Worst, HeatmapScale.Color(2, 0.1f, 2));
        Assert.Equal(HeatmapScale.Worst, HeatmapScale.Color(9, 0.1f, 2));
    }
}
