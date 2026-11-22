using Bibliotaph.Presentation.Services;
using Bibliotaph.Processing;
using AppKit;
using CoreGraphics;
using Foundation;
using ImageIO;

namespace Bibliotaph.Mac.Platform;

/// <summary>
/// Covers and pictures through ImageIO, the Mac's own codecs, at the same JPEG quality as the Windows app. ImageIO is
/// safe to call from the indexing threads.
/// </summary>
public sealed class MacImageCodec : IImageCodec, IPictureDecoder
{
    const float Quality = 0.85f;

    public byte[] EncodeJpeg(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        // Pages render onto white, so alpha carries nothing.
        using var data = NSData.FromArray(bgra.ToArray());
        using var provider = new CGDataProvider(data);
        using var space = CGColorSpace.CreateSrgb();
        using var image = new CGImage(width, height, 8, 32, stride, space,
            CGBitmapFlags.ByteOrder32Little | CGBitmapFlags.NoneSkipFirst, provider, null, false, CGColorRenderingIntent.Default);
        return Jpeg(image) ?? throw new InvalidOperationException("The page couldn't be encoded as a JPEG.");
    }

    public (int Width, int Height)? ReadSize(Stream image)
    {
        using var source = Open(image);
        return ReadSize(source);
    }

    public byte[]? Thumbnail(Stream image, int maxWidth)
    {
        using var source = Open(image);
        if (ReadSize(source) is not { } size || size.Width <= 0 || size.Height <= 0) return null;
        // The thumbnail's limit is on its longer side; scaled so the width comes out at most maxWidth.
        var longest = size.Width >= size.Height ? Math.Min(maxWidth, size.Width) : (int)Math.Ceiling(Math.Min(maxWidth, size.Width) * (double)size.Height / size.Width);
        using var thumbnail = source!.CreateThumbnail(0, new CGImageThumbnailOptions
        {
            CreateThumbnailFromImageAlways = true,
            CreateThumbnailWithTransform = true,
            MaxPixelSize = longest,
        });
        return thumbnail is null ? null : Jpeg(thumbnail);
    }

    object? IPictureDecoder.DecodeForViewing(Stream image, long maxPixels) => DecodeForViewing(image, maxPixels);

    /// <summary>A picture for the reader, made smaller first if it has more pixels than the reader will show.</summary>
    public static NSImage? DecodeForViewing(Stream image, long maxPixels)
    {
        using var source = Open(image);
        if (ReadSize(source) is not { } size || size.Width <= 0 || size.Height <= 0) return null;
        var pixels = (long)size.Width * size.Height;
        var scale = pixels > maxPixels ? Math.Sqrt((double)maxPixels / pixels) : 1;
        using var decoded = source!.CreateThumbnail(0, new CGImageThumbnailOptions
        {
            CreateThumbnailFromImageAlways = true,
            CreateThumbnailWithTransform = true,
            MaxPixelSize = (int)Math.Ceiling(Math.Max(size.Width, size.Height) * scale),
        });
        return decoded is null ? null : new NSImage(decoded, new CGSize(decoded.Width, decoded.Height));
    }

    /// <summary>From the header; the pixels aren't decoded.</summary>
    static (int Width, int Height)? ReadSize(CGImageSource? source)
    {
        if (source is null || source.ImageCount == 0) return null;
        var properties = source.GetProperties(0, null);
        return properties?.PixelWidth is { } width && properties.PixelHeight is { } height ? (width, height) : null;
    }

    /// <summary>A source over the caller's stream, which stays the caller's to close.</summary>
    static CGImageSource? Open(Stream image)
    {
        using var copy = new MemoryStream();
        image.CopyTo(copy);
        using var data = NSData.FromArray(copy.ToArray());
        return CGImageSource.FromData(data);
    }

    internal static byte[]? Jpeg(CGImage image)
    {
        using var output = new NSMutableData();
        using var destination = CGImageDestination.Create(output, UniformTypeIdentifiers.UTTypes.Jpeg.Identifier, 1);
        if (destination is null) return null;
        destination.AddImage(image, new CGImageDestinationOptions { LossyCompressionQuality = Quality });
        return destination.Close() ? output.ToArray() : null;
    }
}
