
namespace Bibliotaph.Presentation.Services;

/// <summary>
/// Decodes a picture for the reader. The result is the platform's own image (an ImageSource on Windows): the view model
/// only holds it for the view, which draws it.
/// </summary>
public interface IPictureDecoder
{
    /// <summary>The picture, shrunk to at most <paramref name="maxPixels"/>; null when the file isn't a readable image.</summary>
    object? DecodeForViewing(Stream image, long maxPixels);
}
