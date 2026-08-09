using System.Numerics;
using System.Windows.Media;

namespace ModelExplorer.App;

/// <summary>A named material colour offered by the viewport appearance panel.</summary>
public sealed record ModelColorOption(string Name, byte Red, byte Green, byte Blue)
{
    public Color Color => Color.FromRgb(Red, Green, Blue);

    public SolidColorBrush Swatch { get; } = CreateSwatch(Red, Green, Blue);

    private static SolidColorBrush CreateSwatch(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}

public static class ModelColors
{
    public static IReadOnlyList<ModelColorOption> All { get; } =
    [
        new("Slate", 158, 171, 189),
        new("Pearl", 224, 226, 230),
        new("Graphite", 92, 99, 110),
        new("Ocean", 67, 145, 214),
        new("Emerald", 62, 166, 123),
        new("Amber", 230, 151, 62),
        new("Coral", 218, 93, 83),
        new("Violet", 151, 103, 204),
        new("Bronze", 167, 112, 68),
    ];

    public static ModelColorOption Default => All[0];
}

/// <summary>
/// The renderer settings behind a named shading choice. These are deliberately
/// material/render flags rather than alternate meshes, so they also apply to a
/// live cut preview and to the capped mesh that replaces it.
/// </summary>
public sealed record ShadingOption(
    string Name,
    bool FlatShading,
    bool RenderEdges,
    global::SharpDX.Direct3D11.FillMode FillMode,
    float SpecularStrength,
    float Shininess);

public static class ShadingOptions
{
    public static IReadOnlyList<ShadingOption> All { get; } =
    [
        new("Smooth", false, false, global::SharpDX.Direct3D11.FillMode.Solid, 0.22f, 24f),
        new("Matte", false, false, global::SharpDX.Direct3D11.FillMode.Solid, 0.045f, 8f),
        new("Flat", true, false, global::SharpDX.Direct3D11.FillMode.Solid, 0.08f, 12f),
        new("Surface + edges", false, true, global::SharpDX.Direct3D11.FillMode.Solid, 0.16f, 18f),
        new("Wireframe", false, false, global::SharpDX.Direct3D11.FillMode.Wireframe, 0f, 1f),
    ];

    public static ShadingOption Default => All[0];
}

/// <summary>One directional component in a three-light preset.</summary>
public sealed record DirectionalLightOption(
    Vector3 Direction,
    float Strength,
    byte Red = 255,
    byte Green = 255,
    byte Blue = 255);

/// <summary>A main/fill/rim rig. Every direction is world-space and normalized.</summary>
public sealed record LightingPreset(
    string Name,
    DirectionalLightOption Main,
    DirectionalLightOption Fill,
    DirectionalLightOption Rim);

public static class LightingPresets
{
    public static IReadOnlyList<LightingPreset> All { get; } =
    [
        new(
            "Studio (3 lights)",
            Light(-0.55f, -0.75f, -0.85f, 1f),
            Light(0.80f, 0.35f, -0.28f, 0.50f, 215, 229, 255),
            Light(-0.25f, 0.85f, 0.30f, 0.34f, 255, 224, 198)),
        new(
            "Balanced sides",
            Light(-0.85f, -0.55f, -0.55f, 0.78f),
            Light(0.85f, -0.45f, -0.45f, 0.66f, 220, 233, 255),
            Light(0f, 0.90f, -0.40f, 0.50f, 255, 228, 204)),
        new(
            "Front",
            Light(-0.12f, 0.90f, -0.55f, 1f),
            Light(0.75f, -0.30f, -0.20f, 0.28f, 218, 232, 255),
            Light(-0.70f, -0.25f, 0.15f, 0.22f, 255, 224, 198)),
        new(
            "Left + right",
            Light(-1f, 0f, -0.35f, 0.92f),
            Light(1f, 0f, -0.35f, 0.72f, 218, 232, 255),
            Light(0f, 0.90f, 0.15f, 0.24f, 255, 224, 198)),
        new(
            "Top",
            Light(0f, 0f, -1f, 1f),
            Light(0.75f, -0.45f, -0.25f, 0.36f, 218, 232, 255),
            Light(-0.70f, 0.50f, 0.15f, 0.24f, 255, 224, 198)),
    ];

    public static LightingPreset Default => All[0];

    /// <summary>
    /// Rotates a light around the model's Z axis while preserving its elevation.
    /// A vertical preset is given a useful oblique elevation first; rotating a
    /// perfectly vertical vector would otherwise have no visible effect.
    /// </summary>
    public static Vector3 OrbitDirection(Vector3 direction, double radians)
    {
        direction = Vector3.Normalize(direction);
        if (MathF.Abs(direction.X) + MathF.Abs(direction.Y) < 0.0001f)
        {
            direction = Vector3.Normalize(new Vector3(0.8f, 0f, direction.Z < 0 ? -0.6f : 0.6f));
        }

        var cosine = (float)Math.Cos(radians);
        var sine = (float)Math.Sin(radians);
        return Vector3.Normalize(new Vector3(
            direction.X * cosine - direction.Y * sine,
            direction.X * sine + direction.Y * cosine,
            direction.Z));
    }

    private static DirectionalLightOption Light(
        float x,
        float y,
        float z,
        float strength,
        byte red = 255,
        byte green = 255,
        byte blue = 255) =>
        new(Vector3.Normalize(new Vector3(x, y, z)), strength, red, green, blue);
}
