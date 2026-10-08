using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace Bibliotaph.Spike.Viewer;

/// <summary>One PDF page in the scroll list. Sizes are in device-independent pixels (1/96 inch).</summary>
public sealed class PageModel(int index, string? label, double widthPts, double heightPts) : INotifyPropertyChanged
{
    double _width, _height;
    ImageSource? _image;
    string? _error;

    public int Index { get; } = index;
    public string? Label { get; } = label;
    public double WidthPts { get; } = widthPts;
    public double HeightPts { get; } = heightPts;

    /// <summary>Bumped on every zoom change; renders for an older generation are discarded.</summary>
    public int Generation { get; set; }

    /// <summary>Pixels per point the current <see cref="Image"/> (or tile set) was rendered at.</summary>
    public double RenderedScale { get; set; }

    /// <summary>When the page scrolled into view without pixels; used for the blank-page metric.</summary>
    public DateTime? BlankSince { get; set; }

    /// <summary>Pixel origins of tiles already on the page, so a tile is never added twice.</summary>
    public HashSet<(int X, int Y)> RenderedTiles { get; } = [];

    public double Width { get => _width; set => Set(ref _width, value); }
    public double Height { get => _height; set => Set(ref _height, value); }
    public ImageSource? Image { get => _image; set => Set(ref _image, value); }
    public string? Error { get => _error; set { Set(ref _error, value); OnChanged(nameof(Placeholder)); } }
    public string Placeholder => Error ?? (Label is { } l && l != (Index + 1).ToString() ? $"p. {l}" : $"{Index + 1}");

    public ObservableCollection<PlacedImage> Tiles { get; } = [];
    public ObservableCollection<HighlightBox> Highlights { get; } = [];

    public bool HasPixels => Image is not null || Tiles.Count > 0;

    public void ClearPixels()
    {
        Image = null;
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

/// <summary>A search highlight on its page, in DIPs.</summary>
public sealed record HighlightBox(double X, double Y, double Width, double Height, bool Current);
