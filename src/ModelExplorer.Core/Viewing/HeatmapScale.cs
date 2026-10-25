using System.Numerics;

namespace ModelExplorer.Core.Viewing;

/// <summary>
/// The colour scale of the deviation view: green within tolerance, then yellow
/// through red up to the largest deviation found.
/// </summary>
/// <remarks>
/// The pixel shader evaluates the same function per triangle from a buffer of
/// distances, so changing the tolerance recolours a mesh without rebuilding it.
/// This copy serves the legend and the tests.
/// </remarks>
public static class HeatmapScale
{
    public static readonly Vector3 Within = new(0x36 / 255f, 0xc9 / 255f, 0x8f / 255f);
    public static readonly Vector3 Middle = new(0xf5 / 255f, 0xcd / 255f, 0x4c / 255f);
    public static readonly Vector3 Worst = new(0xf0 / 255f, 0x51 / 255f, 0x4f / 255f);

    public static Vector3 Color(float distance, float tolerance, float maximum)
    {
        if (distance <= tolerance)
        {
            return Within;
        }

        var t = Math.Clamp((distance - tolerance) / MathF.Max(maximum - tolerance, 1e-12f), 0, 1);
        return t < 0.5f ? Vector3.Lerp(Within, Middle, t * 2) : Vector3.Lerp(Middle, Worst, (t * 2) - 1);
    }
}
