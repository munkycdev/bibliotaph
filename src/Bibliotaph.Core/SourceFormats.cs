namespace Bibliotaph.Core;

/// <summary>The file types slice 1 indexes. Everything else in a source folder is counted and left alone.</summary>
public static class SourceFormats
{
    public const string Pdf = "pdf";
    public const string Jpeg = "jpg";
    public const string Png = "png";

    /// <summary>The format for a file name, or null when Bibliotaph doesn't index that type.</summary>
    public static string? FromFileName(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".pdf" => Pdf,
        ".jpg" or ".jpeg" => Jpeg,
        ".png" => Png,
        _ => null,
    };

    public static bool IsImage(string format) => format is Jpeg or Png;
}
