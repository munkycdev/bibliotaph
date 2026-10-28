using System.Text.RegularExpressions;

namespace Bibliotaph.Core;

/// <summary>What kind of number names a book: an ISBN, or a publisher's product code such as PZO1110.</summary>
public enum IdentifierKind
{
    /// <summary>An ISBN, always kept as its 13 digits, an ISBN-10 converted.</summary>
    Isbn,

    /// <summary>A publisher's product or stock code, kept in capitals without spaces or hyphens: PZO1110, TSR9123.</summary>
    ProductCode,
}

/// <summary>
/// A number a book prints that names it: <see cref="Value"/> in its stored form, the page it was first found on (null
/// for an e-book's package), and <see cref="Printed"/>, how the book writes it.
/// </summary>
public sealed record BookIdentifier(IdentifierKind Kind, string Value, int? PdfPage, string Printed);

/// <summary>ISBNs: reading one as written, with its check digit, and its stored 13-digit form.</summary>
public static class Isbn
{
    /// <summary>
    /// The 13 digits of the ISBN written in <paramref name="text"/> ("978-0-12-345678-9", "0 12 345678 X"), when its
    /// check digit is right; null for anything else.
    /// </summary>
    public static string? Normalize(string text)
    {
        var digits = new string([.. text.Where(c => char.IsAsciiDigit(c) || c is 'x' or 'X')]).ToUpperInvariant();
        return digits.Length switch
        {
            13 when IsIsbn13(digits) => digits,
            10 when IsIsbn10(digits) => FromIsbn10(digits),
            _ => null,
        };
    }

    /// <summary>
    /// The ISBN at the start of <paramref name="digits"/>, which may run on into other numbers ("ISBN 0-12-345678-9
    /// 2019"): its first 13 digits when they are one, else its first 10. Null when neither checks out.
    /// </summary>
    internal static string? Leading(string digits)
    {
        if (digits.Length >= 13 && IsIsbn13(digits[..13])) return digits[..13];
        if (digits.Length >= 10 && IsIsbn10(digits[..10])) return FromIsbn10(digits[..10]);
        return null;
    }

    static bool IsIsbn13(string digits)
    {
        if (!digits.All(char.IsAsciiDigit) || digits[..3] is not ("978" or "979")) return false;
        var sum = digits[..12].Select((c, i) => (c - '0') * (i % 2 == 0 ? 1 : 3)).Sum();
        return (10 - sum % 10) % 10 == digits[12] - '0';
    }

    static bool IsIsbn10(string digits)
    {
        if (!digits[..9].All(char.IsAsciiDigit) || !(char.IsAsciiDigit(digits[9]) || digits[9] == 'X')) return false;
        var sum = digits[..9].Select((c, i) => (c - '0') * (10 - i)).Sum() + (digits[9] == 'X' ? 10 : digits[9] - '0');
        return sum % 11 == 0;
    }

    static string FromIsbn10(string digits)
    {
        var body = "978" + digits[..9];
        var sum = body.Select((c, i) => (c - '0') * (i % 2 == 0 ? 1 : 3)).Sum();
        return body + (char)('0' + (10 - sum % 10) % 10);
    }
}

/// <summary>
/// Finds the ISBNs and product codes a book prints about itself, from its stored page text, so a lookup can name the
/// book exactly rather than by its title. Only the pages a book describes itself on are read: the first 8 with text
/// (title, credits and copyright pages) and the last 3 (the back cover and its barcode).
/// <list type="bullet">
/// <item>An ISBN counts after the word ISBN, or as a bare 978 or 979 number on a copyright page or one of the last
/// pages, where barcodes print; either way its check digit must be right.</item>
/// <item>A product code counts after a label ("Product code", "Stock #"), or with the prefix of a publisher known to
/// print them on one of those pages.</item>
/// <item>A book that shows more than <see cref="MostIsbns"/> ISBNs is listing other books, as a catalogue page does;
/// it keeps only those labelled on its copyright pages.</item>
/// </list>
/// </summary>
public static partial class BookIdentifiers
{
    /// <summary>Pages with text read from the front of a book.</summary>
    public const int FrontPages = 8;

    /// <summary>Pages read from the back of a book.</summary>
    public const int BackPages = 3;

    /// <summary>More distinct ISBNs than this are adverts for other books, not the book's own formats.</summary>
    public const int MostIsbns = 5;

    [GeneratedRegex(@"\bISBN(?:[\s-]?1[03])?\s*[:.#]?\s*(?:\((?:print|pdf|e-?book|hardcover|softcover|paperback|hc|sc)\)\s*[:.]?\s*)?(?<n>\d(?:[\s\-‐‑–]?[\dXx]){9,16})",
        RegexOptions.IgnoreCase)]
    private static partial Regex LabelledIsbn();

    // A barcode's number is printed "9 781234 567897", its first digit apart.
    [GeneratedRegex(@"(?<![\d\-‐‑–])(?<n>9\s?7[89](?:[\s\-‐‑–]?\d){10})(?![\d\-‐‑–])")]
    private static partial Regex BareIsbn();

    [GeneratedRegex(@"\b(?i:product|stock|item|catalog(?:ue)?)\s*(?i:code|number|no\.?|#)\s*[:#.]?\s*(?<code>[A-Z]{2,5}[\s-]?\d{3,8}[A-Z]?)\b")]
    private static partial Regex LabelledCode();

    /// <summary>
    /// The prefixes of publishers whose product codes are a fixed prefix and a number on the cover or credits page, so
    /// the code alone names the book: Paizo, TSR, Chaosium, Catalyst, Modiphius, Green Ronin, White Wolf, Pinnacle,
    /// Goodman Games, Mongoose and Evil Hat.
    /// </summary>
    [GeneratedRegex(@"\b(?<code>(?:PZO|TSR|CHA|CAT|MUH|GRR|WW|S2P|GMG|MGP|EHP)[\s-]?\d{4,8})\b")]
    private static partial Regex PrefixedCode();

    [GeneratedRegex(@"©|\(c\)\s*\d{4}|\bcopyright\b|\ball rights reserved\b", RegexOptions.IgnoreCase)]
    private static partial Regex CopyrightWording();

    /// <summary>
    /// The identifiers in a book's pages (<paramref name="pages"/>, the text of each page in order, null or empty for a
    /// page without any) and in its e-book package (<paramref name="packageIsbn"/>), each once, in the order found.
    /// </summary>
    public static IReadOnlyList<BookIdentifier> Find(IReadOnlyList<string?> pages, string? packageIsbn = null)
    {
        var found = new List<(BookIdentifier Identifier, bool Labelled, bool Copyright)>();
        if (packageIsbn is not null && Isbn.Normalize(packageIsbn) is { } fromPackage)
            found.Add((new BookIdentifier(IdentifierKind.Isbn, fromPackage, null, fromPackage), true, true));

        foreach (var index in PagesToRead(pages))
        {
            var text = pages[index]!;
            var copyright = CopyrightWording().IsMatch(text);
            var back = index >= pages.Count - BackPages;
            foreach (Match match in LabelledIsbn().Matches(text))
                if (Isbn.Leading(Digits(match.Groups["n"].Value)) is { } isbn)
                    found.Add((new BookIdentifier(IdentifierKind.Isbn, isbn, index, PrintedIsbn(match.Groups["n"].Value, isbn)), true, copyright));
            if (copyright || back)
                foreach (Match match in BareIsbn().Matches(text))
                    if (Isbn.Normalize(match.Groups["n"].Value) is { } isbn)
                        found.Add((new BookIdentifier(IdentifierKind.Isbn, isbn, index, match.Groups["n"].Value.Trim()), false, copyright));
            foreach (Match match in LabelledCode().Matches(text))
                found.Add((Code(match, index), true, copyright));
            foreach (Match match in PrefixedCode().Matches(text))
                if (!LooksLikeAYear(match.Groups["code"].Value)) found.Add((Code(match, index), false, copyright));
        }

        var isbns = found.Where(f => f.Identifier.Kind == IdentifierKind.Isbn).Select(f => f.Identifier.Value).Distinct(StringComparer.Ordinal).Count();
        if (isbns > MostIsbns) found.RemoveAll(f => f.Identifier.Kind == IdentifierKind.Isbn && !(f.Labelled && f.Copyright));

        return [.. found.Select(f => f.Identifier).DistinctBy(i => (i.Kind, i.Value))];
    }

    /// <summary>The first <see cref="FrontPages"/> pages with text and the last <see cref="BackPages"/>, in page order.</summary>
    static IEnumerable<int> PagesToRead(IReadOnlyList<string?> pages)
    {
        var front = Enumerable.Range(0, pages.Count).Where(i => !string.IsNullOrWhiteSpace(pages[i])).Take(FrontPages);
        var back = Enumerable.Range(Math.Max(0, pages.Count - BackPages), Math.Min(BackPages, pages.Count)).Where(i => !string.IsNullOrWhiteSpace(pages[i]));
        return front.Union(back).Order();
    }

    static string Digits(string text) => new([.. text.Where(c => char.IsAsciiDigit(c) || c is 'x' or 'X')]);

    /// <summary>The ISBN as the page writes it, without the numbers that may run on after it.</summary>
    static string PrintedIsbn(string written, string isbn)
    {
        var digits = Digits(written);
        var wanted = digits.Length >= 13 && digits[..13] == isbn ? 13 : 10;
        var seen = 0;
        for (var i = 0; i < written.Length; i++)
            if ((char.IsAsciiDigit(written[i]) || written[i] is 'x' or 'X') && ++seen == wanted) return written[..(i + 1)].Trim();
        return written.Trim();
    }

    static BookIdentifier Code(Match match, int page)
    {
        var printed = match.Groups["code"].Value;
        return new BookIdentifier(IdentifierKind.ProductCode, new string([.. printed.Where(char.IsAsciiLetterOrDigit)]), page, printed);
    }

    /// <summary>"TSR 1987", the prefix and four digits apart, is a copyright line written backwards, not a stock number.</summary>
    static bool LooksLikeAYear(string code)
    {
        var digits = code.SkipWhile(c => !char.IsAsciiDigit(c)).ToArray();
        return code.Any(c => c is ' ' or '-') && digits.Length == 4
            && int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture) is >= 1970 and <= 2039;
    }
}
