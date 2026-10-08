namespace Bibliotaph.Core;

/// <summary>
/// How trustworthy a page's extracted text looks, from 0 (nothing usable) to 1. Pages below
/// <see cref="OcrThreshold"/> are queued for OCR; the original text stays searchable until OCR replaces it.
/// </summary>
public static class TextQuality
{
    public const double OcrThreshold = 0.5;

    /// <summary>Fewer visible characters than this and the page is treated as having no text layer.</summary>
    public const int MinimumCharacters = 20;

    /// <param name="text">The page text as extracted.</param>
    /// <param name="unmappedChars">Characters the PDF engine could not map to Unicode.</param>
    public static double Score(string text, int unmappedChars)
    {
        int visible = 0, letters = 0, junk = 0;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) continue;
            visible++;
            if (char.IsLetter(c)) letters++;
            else if (c == '�' || char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.PrivateUse) junk++;
        }
        if (visible < MinimumCharacters) return 0;

        // Broken font encodings show up as unmapped or private-use characters; scans with a junk
        // text layer show up as runs of symbols with few letters.
        var clean = 1.0 - (double)(junk + Math.Min(unmappedChars, visible)) / visible;
        var wordy = Math.Min(1.0, (double)letters / visible / 0.6);
        return Math.Clamp(clean * wordy, 0, 1);
    }

    public static bool NeedsOcr(double score) => score < OcrThreshold;
}
