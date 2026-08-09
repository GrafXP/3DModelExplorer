using System.Numerics;
using ModelExplorer.App;

namespace ModelExplorer.Tests;

public class BuildPlateTests
{
    private static readonly BuildPlate Plate = new("Test printer", 256, 256, 250);

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(120, 80, 45)]
    [InlineData(256, 256, 250)]
    public void FitsAcceptsModelsAtOrWithinEveryLimit(float x, float y, float z)
    {
        Assert.True(Plate.Fits(new Vector3(x, y, z)));
    }

    [Theory]
    [InlineData(256.01f, 256, 250)]
    [InlineData(256, 256.01f, 250)]
    [InlineData(256, 256, 250.01f)]
    public void FitsRejectsAModelThatExceedsAnyLimit(float x, float y, float z)
    {
        Assert.False(Plate.Fits(new Vector3(x, y, z)));
    }

    [Fact]
    public void OverrunsReturnsOnlyExceededAxesWorstFirst()
    {
        var overruns = Plate.Overruns(new Vector3(281, 261.25f, 280));

        Assert.Equal(
        [
            $"Z by {30f:N1} mm",
            $"X by {25f:N1} mm",
            $"Y by {5.25f:N1} mm",
        ], overruns);
    }

    [Fact]
    public void OverrunsIsEmptyAtTheExactBuildVolume()
    {
        Assert.Empty(Plate.Overruns(new Vector3(256, 256, 250)));
    }

    [Fact]
    public void DefaultPlateMatchesTheBambuStudioProfile()
    {
        var plate = BuildPlates.Default;

        Assert.Equal("Bambu Lab X1C / P1S", plate.Name);
        Assert.Equal(256, plate.Width);
        Assert.Equal(256, plate.Depth);
        Assert.Equal(250, plate.Height);
        Assert.Same(BuildPlates.All[0], plate);
    }
}
