namespace ModelExplorer.App;

/// <summary>
/// A printer's usable build volume, in millimetres.
/// </summary>
/// <param name="Height">
/// The Z the printer can actually reach, which is not always the number on the
/// box: Bambu's own profile gives the X1 Carbon 250 mm, not the advertised 256.
/// </param>
public sealed record BuildPlate(string Name, float Width, float Depth, float Height)
{
    public string Label => $"{Name}  ·  {Width:N0} × {Depth:N0} × {Height:N0}";

    /// <summary>Whether a model of this size would print without hitting a limit.</summary>
    public bool Fits(System.Numerics.Vector3 size) =>
        size.X <= Width && size.Y <= Depth && size.Z <= Height;

    /// <summary>
    /// The axes a model overruns and by how much, worst first. Empty when it fits.
    /// </summary>
    public IEnumerable<string> Overruns(System.Numerics.Vector3 size)
    {
        (string Axis, float Over)[] axes =
        [
            ("X", size.X - Width),
            ("Y", size.Y - Depth),
            ("Z", size.Z - Height),
        ];

        return axes
            .Where(axis => axis.Over > 0)
            .OrderByDescending(axis => axis.Over)
            .Select(axis => $"{axis.Axis} by {axis.Over:N1} mm");
    }
}

/// <summary>
/// The printers offered in the viewer.
/// </summary>
/// <remarks>
/// Every size here was read out of Bambu Studio's own machine profiles
/// (<c>resources/profiles/*/machine/*.json</c>, <c>printable_area</c> and
/// <c>printable_height</c> resolved through their <c>inherits</c> chain) rather
/// than from spec sheets, because the slicer's numbers are the ones a print is
/// actually held to.
///
/// A deliberately short list. It covers the machines most models are printed on;
/// the full profile set runs to hundreds of variants that differ only by nozzle.
/// </remarks>
public static class BuildPlates
{
    public static IReadOnlyList<BuildPlate> All { get; } =
    [
        new("Bambu Lab X1C / P1S", 256, 256, 250),
        new("Bambu Lab A1", 256, 256, 256),
        new("Bambu Lab A1 mini", 180, 180, 180),
        new("Bambu Lab H2D", 350, 320, 325),
        new("Prusa MK3S", 250, 210, 210),
        new("Prusa MINI", 180, 180, 180),
        new("Creality Ender-3 V3", 220, 220, 250),
        new("Creality K1 Max", 300, 300, 300),
        new("Elegoo Neptune 4 Pro", 235, 230, 265),
        new("Qidi X-Max 3", 325, 325, 315),
        new("Voron 2.4 350", 350, 350, 325),
    ];

    /// <summary>The machine most of this library was likely printed on.</summary>
    public static BuildPlate Default { get; } = All[0];
}
