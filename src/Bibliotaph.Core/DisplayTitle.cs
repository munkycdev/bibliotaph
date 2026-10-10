using System.Globalization;
using System.Text.RegularExpressions;

namespace Bibliotaph.Core;

/// <summary>
/// A readable title from a file name, used until slice 2's metadata supplies a better one:
/// separators become spaces and download-site suffixes ("digital", "hi-res", dates) are dropped. Hyphens count as
/// separators only in a name that has no spaces or underscores ("the-sunless-citadel"), so "Half-Elf" survives.
/// </summary>
public static partial class DisplayTitle
{
    static readonly HashSet<string> NoiseWords =
    [
        with(StringComparer.OrdinalIgnoreCase),
        "digital", "hi-res", "hires", "lo-res", "lowres", "watermarked", "opt", "optimized", "web", "print", "final", "bookmarked",
    ];

    [GeneratedRegex(@"[_\s]+")]
    private static partial Regex Separators();

    [GeneratedRegex(@"\b(hi|lo)-res\b", RegexOptions.IgnoreCase)]
    private static partial Regex HyphenatedResolution();

    [GeneratedRegex(@"^(v?\d+([.\-]\d+)*|\d{6,})$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionOrDate();

    public static string FromFileName(string fileName) => FromStem(Path.GetFileNameWithoutExtension(fileName));

    /// <summary>A pack's title from its folder's name, which has no extension to drop ("Tokens.v2" keeps its dot).</summary>
    public static string FromFolderName(string folderName) => FromStem(folderName);

    static string FromStem(string stem)
    {
        var words = Separators().IsMatch(stem)
            ? Separators().Split(stem)
            : HyphenatedResolution().Replace(stem, "$1res").Split('-');
        words = [.. words.Where(w => w.Length > 0)];

        // Drop noise from the end only, so a title that starts with a number keeps it.
        while (words.Length > 1 && (NoiseWords.Contains(words[^1]) || VersionOrDate().IsMatch(words[^1])))
            words = words[..^1];

        var title = string.Join(' ', words);
        if (title.Length == 0) return stem;
        // An all-lowercase name reads better capitalised; anything with capitals is left as its author wrote it.
        return title.Any(char.IsUpper) ? title : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(title);
    }
}
