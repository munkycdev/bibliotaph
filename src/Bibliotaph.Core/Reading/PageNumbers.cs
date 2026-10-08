using System.Globalization;

namespace Bibliotaph.Core.Reading;

/// <summary>Page numbers as a reader types and reads them: the printed label where the file has one, else the PDF page.</summary>
public static class PageNumbers
{
    /// <summary>
    /// The PDF page index for what was typed in the page box: a printed label first ("xii", "42"), then a PDF page
    /// number. Null when neither matches.
    /// </summary>
    public static int? Find(string entry, IReadOnlyList<string?> labels, int pageCount)
    {
        entry = entry.Trim();
        if (entry.Length == 0) return null;
        for (var i = 0; i < Math.Min(labels.Count, pageCount); i++)
            if (string.Equals(labels[i]?.Trim(), entry, StringComparison.OrdinalIgnoreCase)) return i;
        return int.TryParse(entry, NumberStyles.Integer, CultureInfo.CurrentCulture, out var number) && number >= 1 && number <= pageCount
            ? number - 1
            : null;
    }

    /// <summary>What the page box shows for a page: its printed label, or its PDF page number.</summary>
    public static string Display(int index, IReadOnlyList<string?> labels) =>
        index < labels.Count && labels[index] is { Length: > 0 } label ? label.Trim() : (index + 1).ToString(CultureInfo.CurrentCulture);
}
