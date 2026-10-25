namespace ModelExplorer.Core.Viewing;

/// <summary>A pane's rectangle in window pixels.</summary>
public readonly record struct PaneRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public float Aspect => Width / (float)Math.Max(Height, 1);

    public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;
}

/// <summary>
/// Where the panes go when several models are shown: a grid chosen so that each
/// model is drawn as large as the view allows.
/// </summary>
public static class PaneLayout
{
    /// <summary>
    /// The number of columns and rows for <paramref name="count"/> equal panes that
    /// shows content of the given aspect largest: two models go next to each other
    /// in a wide window and above each other in a tall one. Between grids that show
    /// it equally large, the one with fewer empty cells wins.
    /// </summary>
    public static (int Columns, int Rows) BestGrid(int count, double areaWidth, double areaHeight, double contentAspect = 4.0 / 3, double gap = 0)
    {
        if (count <= 1)
        {
            return (1, 1);
        }

        var bestColumns = 1;
        var bestEmpty = int.MaxValue;
        var bestScale = double.MinValue;
        for (var columns = 1; columns <= count; columns++)
        {
            var rows = (count + columns - 1) / columns;
            var paneWidth = (areaWidth - (gap * (columns - 1))) / columns;
            var paneHeight = (areaHeight - (gap * (rows - 1))) / rows;
            var scale = Math.Min(paneWidth / contentAspect, paneHeight);
            var empty = (columns * rows) - count;
            var same = Math.Abs(scale - bestScale) <= 1e-9 * Math.Abs(bestScale);
            if ((!same && scale > bestScale) || (same && empty < bestEmpty))
            {
                bestColumns = columns;
                bestScale = scale;
                bestEmpty = empty;
            }
        }

        return (bestColumns, (count + bestColumns - 1) / bestColumns);
    }

    /// <summary>
    /// The rectangle of pane <paramref name="index"/> in a grid over
    /// <paramref name="area"/>. Every pane has the same size, so one linked camera
    /// frames each model at the same scale; leftover pixels are split evenly
    /// around the grid.
    /// </summary>
    public static PaneRect Pane(int index, int columns, int rows, PaneRect area, int gap)
    {
        var width = Math.Max(1, (area.Width - (gap * (columns - 1))) / columns);
        var height = Math.Max(1, (area.Height - (gap * (rows - 1))) / rows);
        var left = area.X + (Math.Max(0, area.Width - ((width * columns) + (gap * (columns - 1)))) / 2);
        var top = area.Y + (Math.Max(0, area.Height - ((height * rows) + (gap * (rows - 1)))) / 2);
        return new PaneRect(left + (index % columns * (width + gap)), top + (index / columns * (height + gap)), width, height);
    }

    /// <summary>All pane rectangles for <paramref name="count"/> panes over <paramref name="area"/>.</summary>
    public static PaneRect[] Arrange(int count, PaneRect area, int gap)
    {
        if (count <= 0)
        {
            return [];
        }

        var (columns, rows) = BestGrid(count, area.Width, area.Height, gap: gap);
        var panes = new PaneRect[count];
        for (var i = 0; i < count; i++)
        {
            panes[i] = Pane(i, columns, rows, area, gap);
        }

        return panes;
    }
}
