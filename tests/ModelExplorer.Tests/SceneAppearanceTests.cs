using System.Numerics;
using ModelExplorer.App;
using SharpDX.Direct3D11;

namespace ModelExplorer.Tests;

public class SceneAppearanceTests
{
    [Fact]
    public void EveryPresetUsesNormalizedLightDirections()
    {
        foreach (var preset in LightingPresets.All)
        {
            foreach (var light in new[] { preset.Main, preset.Fill, preset.Rim })
            {
                Assert.InRange(light.Direction.Length(), 0.9999f, 1.0001f);
                Assert.InRange(light.Strength, 0f, 1f);
            }
        }
    }

    [Fact]
    public void OrbitDirectionRotatesAroundZAndPreservesElevation()
    {
        var start = Vector3.Normalize(new Vector3(0.8f, 0f, -0.6f));

        var result = LightingPresets.OrbitDirection(start, Math.PI / 2);

        Assert.InRange(MathF.Abs(result.X), 0f, 0.0001f);
        Assert.InRange(result.Y, 0.7999f, 0.8001f);
        Assert.InRange(result.Z, -0.6001f, -0.5999f);
        Assert.InRange(result.Length(), 0.9999f, 1.0001f);
    }

    [Fact]
    public void OrbitDirectionMakesAVerticalLightVisiblyRotatable()
    {
        var result = LightingPresets.OrbitDirection(-Vector3.UnitZ, Math.PI / 4);

        Assert.True(MathF.Abs(result.X) > 0.1f);
        Assert.True(MathF.Abs(result.Y) > 0.1f);
        Assert.True(result.Z < 0);
    }

    [Fact]
    public void ShadingChoicesIncludeEdgesFlatAndWireframeRenderPaths()
    {
        Assert.Contains(ShadingOptions.All, option => option.FlatShading);
        Assert.Contains(ShadingOptions.All, option => option.RenderEdges);
        Assert.Contains(ShadingOptions.All, option => option.FillMode == FillMode.Wireframe);
        Assert.Contains(ShadingOptions.All, option => option.FillMode == FillMode.Solid);
    }

    [Fact]
    public void AppearanceDefaultsAreMembersOfTheirOptionLists()
    {
        Assert.Contains(LightingPresets.Default, LightingPresets.All);
        Assert.Contains(ShadingOptions.Default, ShadingOptions.All);
        Assert.Contains(ModelColors.Default, ModelColors.All);
    }
}
