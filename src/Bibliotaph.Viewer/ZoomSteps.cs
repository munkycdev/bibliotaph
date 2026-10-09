namespace Bibliotaph.Viewer;

/// <summary>The zoom levels that Ctrl+wheel, Ctrl+plus and Ctrl+minus and the toolbar's − and + step through. 1 is 100%.</summary>
public static class ZoomSteps
{
    static readonly double[] Steps = [0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4];

    public static IReadOnlyList<double> All => Steps;

    /// <summary>
    /// The level after <paramref name="current"/>, the scale actually on screen (so a fitted page steps from its
    /// fitted size), going up or down; it stops at 25% and 400%.
    /// </summary>
    public static double Next(double current, bool up) =>
        up ? Steps.FirstOrDefault(z => z > current + 0.01, Steps[^1]) : Steps.LastOrDefault(z => z < current - 0.01, Steps[0]);
}
