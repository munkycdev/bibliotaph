using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Bibliotaph.Processing;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.Services;

/// <summary>
/// Cover images for the grid, decoded off the UI thread at display size and frozen, with the most recently used
/// few hundred kept. Covers live in Bibliotaph's own cache folder; a missing or unreadable one just has no image.
/// </summary>
public sealed class CoverImages(CoverCache cache, ILogger<CoverImages> log)
{
    /// <summary>Grid covers are about 150 px wide; twice that stays sharp at 200% scaling.</summary>
    const int DecodeWidth = 300;

    const int Capacity = 600;

    readonly Lock _lock = new();
    readonly Dictionary<string, LinkedListNode<(string Name, ImageSource Image)>> _byName = [];
    readonly LinkedList<(string Name, ImageSource Image)> _recent = new();

    /// <summary>A cover already decoded, so a recycled grid cell can show it without waiting.</summary>
    public ImageSource? TryGet(string name)
    {
        lock (_lock)
        {
            if (!_byName.TryGetValue(name, out var node)) return null;
            _recent.Remove(node);
            _recent.AddFirst(node);
            return node.Value.Image;
        }
    }

    public async Task<ImageSource?> LoadAsync(string name)
    {
        if (TryGet(name) is { } cached) return cached;
        var image = await Task.Run(() => Decode(name));
        if (image is null) return null;
        lock (_lock)
        {
            if (!_byName.ContainsKey(name))
            {
                _byName[name] = _recent.AddFirst((name, image));
                if (_recent.Count > Capacity)
                {
                    _byName.Remove(_recent.Last!.Value.Name);
                    _recent.RemoveLast();
                }
            }
        }
        return image;
    }

    /// <summary>Forgets every cover, as after the cache folder is cleared.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _byName.Clear();
            _recent.Clear();
        }
    }

    BitmapImage? Decode(string name)
    {
        try
        {
            // Read fully first so no file handle outlives the decode.
            var bytes = File.ReadAllBytes(cache.PathFor(name));
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = DecodeWidth;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            log.LogDebug(ex, "Cover {Name} could not be read", name);
            return null;
        }
    }
}
