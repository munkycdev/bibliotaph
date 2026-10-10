using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Bibliotaph.Core.Reading;

namespace Bibliotaph.Viewer;

/// <summary>One PDF page in the scroll list. Sizes are device-independent pixels (1/96 inch); boxes are PDF points.</summary>
public sealed class ViewerPage(int index, string? label, double widthPts, double heightPts) : INotifyPropertyChanged
{
    public int Index { get; } = index;
    public string? Label { get; } = label;
    public double WidthPts { get; } = widthPts;
    public double HeightPts { get; } = heightPts;

    /// <summary>Bumped on every zoom change and when pixels are dropped; renders for an older generation are discarded.</summary>
    public int Generation { get; set; }

    /// <summary>Pixels per point of the current <see cref="Image"/>.</summary>
    public double RenderedScale { get; set; }

    /// <summary>True while <see cref="Image"/> is the quick low-resolution render waiting to be replaced.</summary>
    public bool IsPreview { get; set; }

    /// <summary>Pixel origins of tiles already on the page, so a tile is never added twice.</summary>
    public HashSet<(int X, int Y)> RenderedTiles { get; } = [];

    /// <summary>The page's selectable text, once it has been asked for.</summary>
    public Task<PageTextLayer>? Text { get; set; }

    /// <summary>The page's links, once the pointer has reached the page.</summary>
    public Task<IReadOnlyList<PageLink>>? Links { get; set; }

    /// <summary>The link at a spot in PDF points, if any; the smallest box wins where links overlap.</summary>
    public PageLink? LinkAt(double x, double y) =>
        Links is { IsCompletedSuccessfully: true } links
            ? links.Result.Where(l => l.Box.Contains(x, y)).OrderBy(l => l.Box.Width * l.Box.Height).FirstOrDefault()
            : null;

    public double Width { get; set => Set(ref field, value); }
    public double Height { get; set => Set(ref field, value); }
    public ImageSource? Image { get; set => Set(ref field, value); }

    public string? Error
    {
        get;
        set
        {
            Set(ref field, value);
            OnChanged(nameof(Placeholder));
        }
    }

    /// <summary>What shows until the page has pixels: its number, or why it couldn't be drawn.</summary>
    public string Placeholder => Error ?? (Label is { } l && l != PdfNumber ? $"p. {l}" : PdfNumber);

    string PdfNumber => (Index + 1).ToString(CultureInfo.CurrentCulture);

    public ObservableCollection<PlacedImage> Tiles { get; } = [];
    public ObservableCollection<PageBox> Highlights { get; } = [];
    public ObservableCollection<PageBox> Selection { get; } = [];

    public bool HasPixels => Image is not null || Tiles.Count > 0;

    public void ClearPixels()
    {
        Image = null;
        IsPreview = false;
        Tiles.Clear();
        RenderedTiles.Clear();
        RenderedScale = 0;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnChanged(name);
    }

    void OnChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A rendered tile placed on its page, in DIPs.</summary>
public sealed record PlacedImage(double X, double Y, double Width, double Height, ImageSource Source);

/// <summary>A highlight or selection rectangle on its page, in DIPs. <see cref="Current"/> marks the hit being shown.</summary>
public sealed record PageBox(double X, double Y, double Width, double Height, bool Current = false);

/// <summary>Where a search hit is: its page and the rectangles of its words in PDF points.</summary>
public sealed record PageHighlight(int PageIndex, IReadOnlyList<PageRect> Rects);
