using System.Numerics;
using ModelExplorer.Core.Viewing;

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
        Assert.Contains(ShadingOptions.All, option => option.Wireframe);
        Assert.Contains(ShadingOptions.All, option => !option.Wireframe);
    }

    [Fact]
    public void OrbitingLightAdvancesByFrameTimeAndIgnoresLongPauses()
    {
        var appearance = new Appearance { RotateMainLight = true, RotationSpeed = 90 };

        appearance.Advance(TimeSpan.FromSeconds(0.05));
        Assert.Equal(Math.PI / 2 * 0.05, appearance.LightAngle, 6);

        // A debugger pause or a hidden window must not turn into a jump.
        appearance.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(Math.PI / 2 * 0.15, appearance.LightAngle, 6);

        appearance.Lighting = LightingPresets.All[1];
        Assert.Equal(0, appearance.LightAngle);
    }

    [Fact]
    public void DisabledLightsContributeNothing()
    {
        var appearance = new Appearance { AmbientEnabled = false };

        Assert.Equal(Vector3.Zero, appearance.AmbientColor);
        Assert.Equal(appearance.Lighting.Main.Direction, appearance.MainDirection);
    }

    [Fact]
    public void AppearanceDefaultsAreMembersOfTheirOptionLists()
    {
        Assert.Contains(LightingPresets.Default, LightingPresets.All);
        Assert.Contains(ShadingOptions.Default, ShadingOptions.All);
        Assert.Contains(ModelColors.Default, ModelColors.All);
    }
}
