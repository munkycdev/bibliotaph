using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Bibliotaph.Processing;

namespace Bibliotaph.App.Services;

/// <summary>Covers through WPF's imaging codecs (Windows Imaging Component). Safe off the UI thread: nothing is shared.</summary>
public sealed class WpfImageCodec : IImageCodec
{
    const int Quality = 85;

    public byte[] EncodeJpeg(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        // Pages render onto white, so alpha carries nothing; Bgr32 ignores it.
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, bgra.ToArray(), stride);
        return Encode(bitmap);
    }

    public (int Width, int Height)? ReadSize(Stream image)
    {
        try
        {
            // DelayCreation reads the header, not the pixels.
            var frame = BitmapDecoder.Create(image, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch (Exception ex) when (IsBadImage(ex))
        {
            return null;
        }
    }

    public byte[]? Thumbnail(Stream image, int maxWidth)
    {
        try
        {
            var size = ReadSize(image);
            if (size is null) return null;
            image.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = image;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            // Decoding straight to the target size keeps a 20,000 px battle map from costing a gigabyte.
            if (size.Value.Width > maxWidth) bitmap.DecodePixelWidth = maxWidth;
            bitmap.EndInit();
            bitmap.Freeze();
            return Encode(bitmap);
        }
        catch (Exception ex) when (IsBadImage(ex))
        {
            return null;
        }
    }

    static byte[] Encode(BitmapSource bitmap)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = Quality };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    static bool IsBadImage(Exception ex) =>
        ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or OverflowException
            or System.Runtime.InteropServices.COMException;
}
