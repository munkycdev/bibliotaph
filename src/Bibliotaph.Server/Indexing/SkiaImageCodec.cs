using Bibliotaph.Processing;
using SkiaSharp;

namespace Bibliotaph.Server.Indexing;

/// <summary>Covers through Skia, the server's stand-in for the Windows app's WPF codecs, with the same JPEG quality.</summary>
public sealed class SkiaImageCodec : IImageCodec
{
    const int Quality = 85;

    public byte[] EncodeJpeg(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        // Pages render onto white, so alpha carries nothing.
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        unsafe
        {
            fixed (byte* pixels = bgra)
            {
                using var pixmap = new SKPixmap(info, (IntPtr)pixels, stride);
                using var data = pixmap.Encode(SKEncodedImageFormat.Jpeg, Quality)
                    ?? throw new InvalidOperationException("The page couldn't be encoded as a JPEG.");
                return data.ToArray();
            }
        }
    }

    public (int Width, int Height)? ReadSize(Stream image)
    {
        // The codec reads the header, not the pixels.
        using var codec = Open(image);
        return codec is null ? null : (codec.Info.Width, codec.Info.Height);
    }

    public byte[]? Thumbnail(Stream image, int maxWidth)
    {
        using var codec = Open(image);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0) return null;

        // Decoding at the nearest size the codec supports keeps a 20,000 px battle map from costing a gigabyte; the
        // last step to the exact width is a resize of something already small.
        var scale = Math.Min(1f, (float)maxWidth / codec.Info.Width);
        var decoded = codec.GetScaledDimensions(scale);
        using var bitmap = new SKBitmap(new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var result = codec.GetPixels(bitmap.Info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;

        var width = Math.Min(maxWidth, bitmap.Width);
        var height = Math.Max(1, (int)Math.Round(bitmap.Height * (double)width / bitmap.Width));
        using var sized = width == bitmap.Width ? null : bitmap.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var encoded = SKImage.FromBitmap(sized ?? bitmap);
        using var data = encoded.Encode(SKEncodedImageFormat.Jpeg, Quality);
        return data?.ToArray();
    }

    /// <summary>A codec over the caller's stream, which stays the caller's to close.</summary>
    static SKCodec? Open(Stream image) => SKCodec.Create(new SKManagedStream(image, disposeManagedStream: false));
}
