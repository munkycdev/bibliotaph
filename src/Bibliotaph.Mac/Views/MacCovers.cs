using AppKit;
using Bibliotaph.Processing;
using CoreGraphics;
using Foundation;
using ImageIO;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// Cover images for the grid, decoded off the main thread at display size, with the most recently used few hundred
/// kept. Covers live in Bibliotaph's own cache folder; a missing or unreadable one just has no image.
/// </summary>
public sealed class MacCovers(CoverCache cache, Platform.MacSiteIcons sites, ILogger<MacCovers> log)
{
    /// <summary>The icons of the sites books owned online are on, for their covers' badge.</summary>
    public Platform.MacSiteIcons Sites => sites;

    /// <summary>Grid covers are about 156 by 208 points; twice that on the long side stays sharp on a Retina screen.</summary>
    const int DecodeLongSide = 420;
    const int Capacity = 600;

    readonly Lock _lock = new();
    readonly Dictionary<string, LinkedListNode<(string Name, NSImage Image)>> _byName = [];
    readonly LinkedList<(string Name, NSImage Image)> _recent = new();

    /// <summary>A cover already decoded, so a reused grid cell can show it without waiting.</summary>
    public NSImage? TryGet(string name)
    {
        lock (_lock)
        {
            if (!_byName.TryGetValue(name, out var node)) return null;
            _recent.Remove(node);
            _recent.AddFirst(node);
            return node.Value.Image;
        }
    }

    public async Task<NSImage?> LoadAsync(string name)
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

    NSImage? Decode(string name)
    {
        try
        {
            // Read fully first so no file handle outlives the decode.
            var bytes = File.ReadAllBytes(cache.PathFor(name));
            using var data = NSData.FromArray(bytes);
            using var source = CGImageSource.FromData(data);
            using var image = source?.CreateThumbnail(0, new CGImageThumbnailOptions
            {
                CreateThumbnailFromImageAlways = true,
                CreateThumbnailWithTransform = true,
                MaxPixelSize = DecodeLongSide,
            });
            return image is null ? null : new NSImage(image, new CGSize(image.Width, image.Height));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogDebug(ex, "Cover {Name} could not be read", name);
            return null;
        }
    }
}
