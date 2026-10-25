using System.Text.RegularExpressions;

namespace Bibliotaph.Web.Icons;

/// <summary>
/// The Windows app's icons (src/Bibliotaph.App/Icons/Icons.xaml, embedded here as it is), so both draw the same Lucide
/// shapes from one file. WPF's path data, as that file writes it, is also valid SVG path data.
/// </summary>
public static partial class LucideIcons
{
    static readonly Lazy<IReadOnlyDictionary<string, string>> Loaded = new(Load);

    /// <summary>Path data by name without the Icon. prefix: LibraryBig, Settings2.</summary>
    public static IReadOnlyDictionary<string, string> Paths => Loaded.Value;

    static Dictionary<string, string> Load()
    {
        using var stream = typeof(LucideIcons).Assembly.GetManifestResourceStream("Bibliotaph.Web.Icons.xaml")
            ?? throw new InvalidOperationException("Icons.xaml is not embedded.");
        using var reader = new StreamReader(stream);
        return Geometry().Matches(reader.ReadToEnd()).ToDictionary(m => m.Groups["name"].Value, m => m.Groups["data"].Value, StringComparer.Ordinal);
    }

    [GeneratedRegex("<Geometry x:Key=\"Icon\\.(?<name>[A-Za-z0-9]+)\">(?<data>[^<]*)</Geometry>")]
    private static partial Regex Geometry();
}
