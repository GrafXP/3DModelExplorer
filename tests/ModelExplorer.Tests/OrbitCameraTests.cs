using System.Numerics;
using ModelExplorer.Core.Viewing;
using ModelExplorer.Geometry;

namespace ModelExplorer.Tests;

public class OrbitCameraTests
{
    private const float Width = 800, Height = 500;

    private static OrbitCamera Camera() => OrbitCamera.Framing(new BoundingBox(new(-10), new(10)), Width / Height);

    [Theory]
    [InlineData(0.35f)]
    [InlineData(1f)]
    [InlineData(3f)]
    public void FramingKeepsTheBoundingSphereInsideBothScreenDimensions(float aspect)
    {
        var bounds = new BoundingBox(new(-10), new(10));
        var camera = OrbitCamera.Framing(bounds, aspect);
        var radius = bounds.Size.Length() / 2;
        var reach = MathF.Tan(MathF.Asin(radius / camera.Distance)) / MathF.Tan(OrbitCamera.FieldOfView / 2);

        Assert.True(reach < 0.8f, "taller than the view");
        Assert.True(reach / aspect < 0.8f, "wider than the view");
        Assert.Equal(Vector3.Zero, camera.Target);
        Assert.InRange(Vector3.Distance(camera.Forward, OrbitCamera.DefaultDirection), 0, 1e-5f);
    }

    [Fact]
    public void OrbitingWithoutAPivotTurnsAboutTheTarget()
    {
        var camera = Camera();
        var target = camera.Target;
        var distance = camera.Distance;

        camera.Orbit(120, -40);

        Assert.Equal(target, camera.Target);
        Assert.Equal(distance, Vector3.Distance(camera.Position, target), 3);
    }

    [Fact]
    public void OrbitingAboutAPivotKeepsItWhereItIsOnScreen()
    {
        var camera = Camera();
        var pivot = new Vector3(6, -4, 7);
        camera.Pivot = pivot;
        var before = camera.Project(pivot, Width, Height)!.Value;

        camera.Orbit(150, 30);

        var after = camera.Project(pivot, Width, Height)!.Value;
        Assert.InRange(Vector2.Distance(before, after), 0, 0.05f);
        Assert.NotEqual(Vector3.Zero, camera.Target);
    }

    [Fact]
    public void OrbitingNeverTipsOverTheTop()
    {
        var camera = Camera();

        camera.Orbit(0, -100_000);
        Assert.Equal(OrbitCamera.MaxPitch, camera.Pitch, 5);
        camera.Orbit(0, 100_000);
        Assert.Equal(-OrbitCamera.MaxPitch, camera.Pitch, 5);
    }

    [Fact]
    public void PanningMovesTheSceneWithTheMouse()
    {
        var camera = Camera();
        var point = camera.Target;

        camera.Pan(25, -10, Height);

        var at = camera.Project(point, Width, Height)!.Value;
        Assert.Equal((Width / 2) + 25, at.X, 2);
        Assert.Equal((Height / 2) - 10, at.Y, 2);
    }

    [Fact]
    public void ZoomingKeepsThePointUnderTheCursorInPlace()
    {
        var camera = Camera();
        const float ndcX = 0.5f, ndcY = -0.25f;
        var t = MathF.Tan(OrbitCamera.FieldOfView / 2) * camera.Distance;
        var underCursor = camera.Target + (camera.Right * (ndcX * t * Width / Height)) + (camera.Up * (ndcY * t));
        var before = camera.Project(underCursor, Width, Height)!.Value;
        var distance = camera.Distance;

        camera.Zoom(0.5f, ndcX, ndcY, Width / Height);

        Assert.Equal(distance / 2, camera.Distance, 4);
        Assert.InRange(Vector2.Distance(before, camera.Project(underCursor, Width, Height)!.Value), 0, 0.05f);
    }

    [Fact]
    public void ARayThroughAPixelProjectsBackOntoIt()
    {
        var camera = Camera();
        var (origin, direction) = camera.Ray(-0.3f, 0.6f, Width / Height);

        var at = camera.Project(origin + (direction * 37), Width, Height)!.Value;

        Assert.Equal((-0.3f + 1) / 2 * Width, at.X, 2);
        Assert.Equal((1 - 0.6f) / 2 * Height, at.Y, 2);
    }

    [Fact]
    public void ProjectionReversesDepthFromOneAtTheNearPlaneToZeroAtInfinity()
    {
        var projection = OrbitCamera.Projection(1.6f, 0.5f);

        var near = Vector4.Transform(new Vector4(0, 0, -0.5f, 1), projection);
        var far = Vector4.Transform(new Vector4(0, 0, -1e7f, 1), projection);

        Assert.Equal(1, near.Z / near.W, 5);
        Assert.InRange(far.Z / far.W, 0, 1e-6f);
    }

    [Fact]
    public void ThePointsBehindTheCameraAreNotProjected()
    {
        var camera = Camera();

        Assert.Null(camera.Project(camera.Position - (camera.Forward * 5), Width, Height));
    }
}

public class PaneLayoutTests
{
    [Fact]
    public void TwoModelsSitSideBySideWhenWideAndAboveEachOtherWhenTall()
    {
        Assert.Equal((2, 1), PaneLayout.BestGrid(2, 1600, 800));
        Assert.Equal((1, 2), PaneLayout.BestGrid(2, 600, 1200));
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(3, 3, 1)]
    [InlineData(4, 2, 2)]
    [InlineData(16, 4, 4)]
    public void TheGridFitsTheCount(int count, int columns, int rows)
    {
        var grid = PaneLayout.BestGrid(count, count == 3 ? 2400 : 1200, count == 3 ? 600 : 900);

        Assert.Equal((columns, rows), grid);
    }

    [Fact]
    public void PanesAreEqualAndInsideTheAreaWithoutOverlapping()
    {
        var area = new PaneRect(100, 50, 1001, 703);

        var panes = PaneLayout.Arrange(5, area, gap: 3);

        Assert.Equal(5, panes.Length);
        Assert.All(panes, pane =>
        {
            Assert.Equal(panes[0].Width, pane.Width);
            Assert.Equal(panes[0].Height, pane.Height);
            Assert.True(pane.X >= area.X && pane.Right <= area.Right && pane.Y >= area.Y && pane.Bottom <= area.Bottom);
        });
        for (var i = 0; i < panes.Length; i++)
        {
            for (var j = i + 1; j < panes.Length; j++)
            {
                var a = panes[i];
                var b = panes[j];
                Assert.False(a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom, $"panes {i} and {j} overlap");
            }
        }
    }

    [Fact]
    public void NoModelsNoPanes() => Assert.Empty(PaneLayout.Arrange(0, new PaneRect(0, 0, 100, 100), 2));
}
