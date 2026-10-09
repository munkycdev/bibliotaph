using System.Globalization;

namespace Bibliotaph.Core.Layout;

/// <summary>A rectangle on the desktop in physical pixels, as Windows reports windows and monitors.</summary>
public readonly record struct ScreenRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;

    public long Area => (long)Math.Max(0, Width) * Math.Max(0, Height);

    /// <summary>How many pixels of this rectangle lie inside <paramref name="other"/>.</summary>
    public long Overlap(ScreenRect other)
    {
        var width = Math.Min(Right, other.Right) - Math.Max(Left, other.Left);
        var height = Math.Min(Bottom, other.Bottom) - Math.Max(Top, other.Top);
        return width <= 0 || height <= 0 ? 0 : (long)width * height;
    }

    /// <summary>The squared distance between the centres, for finding the nearest screen.</summary>
    public long DistanceTo(ScreenRect other)
    {
        var dx = ((2L * Left) + Width - ((2L * other.Left) + other.Width)) / 2;
        var dy = ((2L * Top) + Height - ((2L * other.Top) + other.Height)) / 2;
        return (dx * dx) + (dy * dy);
    }
}

/// <summary>
/// Where a window was left: its restored rectangle in physical pixels, and whether it was maximized. Saved as text
/// in the settings table, so the next reader window opens on the same monitor at the same size.
/// </summary>
public sealed record WindowPlacement(ScreenRect Bounds, bool Maximized)
{
    /// <summary>How far a window's top edge may sit above its screen: the invisible resize border Windows adds.</summary>
    const int TopSlack = 16;

    /// <summary>The height of a title bar to grab, which must stay on the screen.</summary>
    const int GrabHeight = 48;

    /// <summary>"left,top,width,height,maximized", in invariant numbers.</summary>
    public string Format() => string.Create(CultureInfo.InvariantCulture,
        $"{Bounds.Left},{Bounds.Top},{Bounds.Width},{Bounds.Height},{(Maximized ? 1 : 0)}");

    /// <summary>A saved placement, or null for text that isn't one.</summary>
    public static WindowPlacement? Parse(string? text)
    {
        var parts = text?.Split(',');
        if (parts is not { Length: 5 }) return null;
        var numbers = new int[5];
        for (var i = 0; i < 5; i++)
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[i])) return null;
        if (numbers[2] <= 0 || numbers[3] <= 0 || numbers[4] is not (0 or 1)) return null;
        return new WindowPlacement(new ScreenRect(numbers[0], numbers[1], numbers[2], numbers[3]), numbers[4] == 1);
    }

    /// <summary>
    /// Keeps the window where it was when it is mostly on one of <paramref name="workAreas"/> with its title bar
    /// reachable. Otherwise (its monitor was unplugged, or the screen got smaller) it moves onto the screen it overlaps
    /// most, or the nearest one, shrunk to fit and kept inside it. With no screens to go by, nothing changes.
    /// </summary>
    public WindowPlacement Fit(IReadOnlyList<ScreenRect> workAreas)
    {
        if (workAreas.Count == 0) return this;
        if (workAreas.Any(Reachable)) return this;

        var best = workAreas.MaxBy(Bounds.Overlap);
        var screen = Bounds.Overlap(best) > 0 ? best : workAreas.MinBy(Bounds.DistanceTo);
        var width = Math.Min(Bounds.Width, screen.Width);
        var height = Math.Min(Bounds.Height, screen.Height);
        var left = Math.Clamp(Bounds.Left, screen.Left, screen.Right - width);
        var top = Math.Clamp(Bounds.Top, screen.Top, screen.Bottom - height);
        return this with { Bounds = new ScreenRect(left, top, width, height) };
    }

    /// <summary>The next window opened from the same place, moved down and right so it doesn't hide this one.</summary>
    public WindowPlacement Cascade(int step) =>
        this with { Bounds = Bounds with { Left = Bounds.Left + step, Top = Bounds.Top + step } };

    /// <summary>At least half the window on this screen, and its top edge where it can be grabbed.</summary>
    bool Reachable(ScreenRect screen) =>
        Bounds.Overlap(screen) * 2 >= Bounds.Area
        && Bounds.Top >= screen.Top - TopSlack
        && Bounds.Top <= screen.Bottom - GrabHeight;
}
