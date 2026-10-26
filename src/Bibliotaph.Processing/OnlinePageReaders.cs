using System.Net;
using System.Text.RegularExpressions;
using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Processing;

/// <summary>
/// Reading a library page's markup, for services whose page holds no data to read instead: the page cut into one
/// piece per book at a marker each book's markup starts with, and the attributes and text in it.
/// </summary>
static partial class PageMarkup
{
    /// <summary>The markup from each <paramref name="marker"/> to the next, one piece per book.</summary>
    internal static IEnumerable<string> Pieces(string html, string marker)
    {
        var at = html.IndexOf(marker, StringComparison.Ordinal);
        while (at >= 0)
        {
            var next = html.IndexOf(marker, at + marker.Length, StringComparison.Ordinal);
            yield return next < 0 ? html[at..] : html[at..next];
            at = next;
        }
    }

    /// <summary>Text as a person reads it: entities decoded, runs of white space made one space; null when empty.</summary>
    internal static string? Text(string? markup)
    {
        if (markup is null) return null;
        var text = Spaces().Replace(WebUtility.HtmlDecode(Tags().Replace(markup, " ")), " ").Trim();
        return text.Length > 0 ? text : null;
    }

    /// <summary>The first value of <paramref name="pattern"/>'s first group in <paramref name="markup"/>, decoded.</summary>
    internal static string? First(string markup, Regex pattern) => pattern.Match(markup) is { Success: true } match ? Text(match.Groups[1].Value) : null;

    /// <summary>
    /// A web address on the page, made absolute against <paramref name="site"/>, without its query (a tracking tag);
    /// null for one that isn't on the web.
    /// </summary>
    internal static string? Address(string? href, Uri site)
    {
        if (href is null) return null;
        href = WebUtility.HtmlDecode(href).Trim();
        // Checked first: on Linux, "/product/…" is also an absolute file: URI.
        if (href.StartsWith('/')) return href.StartsWith("//", StringComparison.Ordinal) ? null : new Uri(site, href).GetLeftPart(UriPartial.Path);
        return Uri.TryCreate(href, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.GetLeftPart(UriPartial.Path) : null;
    }

    /// <summary>
    /// An image's address as the page gives it, by which the browser's own copy of the picture is found, or the picture
    /// Send to Bibliotaph sends under its file name (<see cref="OnlineImports.CoverFileName"/>).
    /// </summary>
    internal static string? Image(string? src)
    {
        if (src is null) return null;
        src = WebUtility.HtmlDecode(src).Trim();
        return src.Length == 0 || src.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? null : src;
    }

    /// <summary>A title made into a fragment of a link: "Nine Crooked Lanterns" gives "nine-crooked-lanterns".</summary>
    internal static string Slug(string title) => NotLetters().Replace(MetadataText.Normalize(title), "-").Trim('-');

    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NotLetters();
}

/// <summary>
/// A game's Source Library on Demiplane: each game has its own Nexus, whose Sources page shows its books as cards,
/// those the user has unlocked with a View button. The cards are read, as the page's script data holds the page the
/// user first opened, not this one. One game's library is not the whole of Demiplane, so no book is ever listed as
/// missing from it.
/// </summary>
public sealed partial class DemiplaneReader : IOnlineLibraryReader
{
    public string Key => "demiplane";

    public string Name => "Demiplane";

    public Uri LibraryPage { get; } = new("https://app.demiplane.com/");

    public OnlinePage? Read(string html)
    {
        if (!html.Contains("demiplane", StringComparison.OrdinalIgnoreCase)) return null;
        var cards = PageMarkup.Pieces(html, "data-cy=\"source-card\"").ToList();
        if (cards.Count == 0) return null;
        var books = new List<OnlineBook>();
        foreach (var card in cards)
        {
            if (!card.Contains("source-card-details-unlocked-btn", StringComparison.Ordinal)) continue;
            if (PageMarkup.First(card, Link()) is not { } path || (PageMarkup.First(card, ProductName()) ?? PageMarkup.First(card, Heading())) is not { } title)
                continue;
            var link = new Uri(LibraryPage, path).AbsoluteUri;
            if (books.Any(b => b.Link == link)) continue;
            books.Add(new OnlineBook(title, link) { CoverUrl = PageMarkup.Image(Cover().Match(card) is { Success: true } cover ? cover.Groups[1].Value : null) });
        }
        return new OnlinePage(books, 0, Complete: false);
    }

    public string? Hint(string html) =>
        html.Contains("demiplane", StringComparison.OrdinalIgnoreCase)
            ? "Open a game's Nexus on Demiplane, then its Sources page, signed in. Read this page lights up when it shows the books you've unlocked."
            : null;

    [GeneratedRegex("""^data-cy="source-card"[^>]*?data-product-name="([^"]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex ProductName();

    [GeneratedRegex("""<h3[^>]*>(.*?)</h3>""", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex Heading();

    // The live page links cards relative to the site; a page a browser saved may have them absolute.
    [GeneratedRegex("""href="((?:https://app\.demiplane\.com)?/nexus/[^/"]+/sources/[^"/?#]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex Link();

    [GeneratedRegex("""<img[^>]*?\ssrc="([^"]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex Cover();
}

/// <summary>
/// Humble Bundle's library: every product the user has from Humble Bundle, books and games alike, each with its name,
/// its publisher and its icon, but no page of its own. So the page is read only with eBook chosen under Platform, and
/// each book's link is the library with the book's name after a #, which opens the library and is the book's key for
/// importing again. Books bought there are files to download, so they go in Get it from.
/// </summary>
public sealed partial class HumbleBundleReader : IOnlineLibraryReader
{
    public string Key => "bundle";

    public string Name => "Humble Bundle";

    public Uri LibraryPage { get; } = new("https://www.humblebundle.com/home/library");

    public MetadataField LinkField => MetadataFields.GetItFrom;

    public OnlinePage? Read(string html)
    {
        if (!html.Contains("humblebundle", StringComparison.OrdinalIgnoreCase) || Platform(html) is not "ebook") return null;
        var books = new List<OnlineBook>();
        foreach (var item in PageMarkup.Pieces(html, "class=\"subproduct-selector"))
        {
            if ((PageMarkup.First(item, TitleAttribute()) ?? PageMarkup.First(item, Heading())) is not { } title) continue;
            var link = $"{LibraryPage.AbsoluteUri}#{PageMarkup.Slug(title)}";
            if (books.Any(b => b.Link == link)) continue;
            books.Add(new OnlineBook(title, link)
            {
                Publisher = PageMarkup.First(item, Publisher()),
                CoverUrl = PageMarkup.Image(Icon().Match(item) is { Success: true } icon ? icon.Groups[1].Value : null),
            });
        }
        // A search typed in the library leaves books out, so the page is the whole library only without one.
        return new OnlinePage(books, 0, Complete: PageMarkup.First(html, Search()) is null);
    }

    public string? Hint(string html) =>
        html.Contains("subproduct-selector", StringComparison.Ordinal) && Platform(html) is not "ebook"
            ? "Choose eBook under Platform, so the library lists only books. Read this page then lights up."
            : null;

    /// <summary>The platform chosen in the library's Platform list ("ebook"), or null when the page has none.</summary>
    static string? Platform(string html) =>
        PlatformList().Match(html) is { Success: true } list && Chosen().Match(list.Groups[1].Value) is { Success: true } chosen ? chosen.Groups[1].Value : null;

    [GeneratedRegex("""<select[^>]*id="switch-platform"[^>]*>(.*?)</select>""", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex PlatformList();

    [GeneratedRegex("""<option[^>]*value="([^"]*)"[^>]*\sselected""", RegexOptions.CultureInvariant)]
    private static partial Regex Chosen();

    [GeneratedRegex("""<h2[^>]*\stitle="([^"]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex TitleAttribute();

    [GeneratedRegex("""<h2[^>]*>(.*?)</h2>""", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex Heading();

    [GeneratedRegex("""</h2>\s*<p>(.*?)</p>""", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex Publisher();

    [GeneratedRegex("""background-image:\s*url\(['"]?([^'")]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex Icon();

    [GeneratedRegex("""<input[^>]*id="search"[^>]*\svalue="([^"]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex Search();
}

/// <summary>
/// DriveThruRPG's library, which is DMs Guild's too: one site and one library for both stores. A page of title cards,
/// each with the product's page, its publisher and its thumbnail, thirty to a page. Each page read adds its books,
/// and only a library that fits on one page is the whole library. Books bought there are files to download, so they
/// go in Get it from.
/// </summary>
public sealed partial class DriveThruRpgReader : IOnlineLibraryReader
{
    readonly Uri _site = new("https://www.drivethrurpg.com");

    public string Key => "drivethrurpg";

    public string Name => "DriveThruRPG";

    public Uri LibraryPage { get; } = new("https://www.drivethrurpg.com/en/mylibrary");

    /// <summary>DMs Guild's own site shows the same library.</summary>
    public IReadOnlyList<string> SignInSites { get; } = ["dmsguild.com"];

    public MetadataField LinkField => MetadataFields.GetItFrom;

    public OnlinePage? Read(string html)
    {
        if (!html.Contains("drivethrurpg.com", StringComparison.OrdinalIgnoreCase) && !html.Contains("dmsguild.com", StringComparison.OrdinalIgnoreCase))
            return null;
        var cards = PageMarkup.Pieces(html, "<obs-library-title-card").ToList();
        if (cards.Count == 0) return null;
        var books = new List<OnlineBook>();
        foreach (var card in cards)
        {
            string? title = null, link = null, publisher = null;
            foreach (Match anchor in Anchor().Matches(card))
            {
                var text = PageMarkup.Text(anchor.Groups[2].Value);
                if (text is null) continue;
                if (link is null && anchor.Groups[1].Value.Contains("/product/", StringComparison.Ordinal))
                    (link, title) = (PageMarkup.Address(anchor.Groups[1].Value, _site), text);
                else if (publisher is null && anchor.Groups[1].Value.Contains("/publisher/", StringComparison.Ordinal))
                    publisher = text;
            }
            if (title is null || link is null || books.Any(b => b.Link == link)) continue;
            books.Add(new OnlineBook(title, link)
            {
                Publisher = publisher,
                CoverUrl = PageMarkup.Image(Thumbnail().Match(card) is { Success: true } image ? image.Groups[1].Value : null),
            });
        }
        return new OnlinePage(books, 0, Complete: !OtherPage().IsMatch(html));
    }

    [GeneratedRegex("""<a\s[^>]*?href="([^"]+)"[^>]*>(.*?)</a>""", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex Anchor();

    [GeneratedRegex("""<img[^>]*?\ssrc="([^"]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex Thumbnail();

    /// <summary>A link to another page of the library: a library on one page has none.</summary>
    [GeneratedRegex("""<a[^>]*aria-label="Page \d+""", RegexOptions.CultureInvariant)]
    private static partial Regex OtherPage();
}
