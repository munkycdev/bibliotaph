using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Processing;

/// <summary>
/// A book read from the user's library on an online service: what becomes a book owned elsewhere, as the user would
/// type it in the inspector. <see cref="Link"/> is the book's page on the service, its key for importing again;
/// <see cref="CoverUrl"/> is the cover the page showed, which is only looked up among the images the page already
/// has, never fetched.
/// </summary>
public sealed record OnlineBook(string Title, string Link)
{
    public string? Publisher { get; init; }

    /// <summary>The kind of book as the service names it ("adventure", "sourcebook"), read through the type vocabulary.</summary>
    public string? Type { get; init; }

    /// <summary>The game system and edition as the service names them ("D&amp;D", "5.5e"), read through the vocabulary.</summary>
    public string? System { get; init; }

    public string? Edition { get; init; }

    public int? Year { get; init; }

    public string? CoverUrl { get; init; }
}

/// <summary>
/// What one page of an online library holds: the books the user owns, how many shared with them were left out, and
/// whether it is the whole library, so books from earlier imports missing from it can be listed.
/// </summary>
public sealed record OnlinePage(IReadOnlyList<OnlineBook> Books, int Shared, bool Complete)
{
    /// <summary>
    /// This page with the books of another page of the same library (a library spread over pages builds up as each is
    /// read), each book once by its link.
    /// </summary>
    public OnlinePage With(OnlinePage more)
    {
        var seen = Books.Select(b => b.Link).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new OnlinePage([.. Books, .. more.Books.Where(b => seen.Add(b.Link))], Math.Max(Shared, more.Shared), Complete || more.Complete);
    }
}

/// <summary>
/// Reads one service's library page: the page the in-app browser shows when the user presses Read this page, or a
/// page they saved with Save as. It reads only the page it is given and fetches nothing. A page it doesn't recognise
/// gives null, so the user is told plainly rather than importing nothing.
/// </summary>
public interface IOnlineLibraryReader
{
    /// <summary>The service's term where <see cref="LinkField"/> keeps it: "dnd-beyond" in Also own, "drivethrurpg" in Get it from.</summary>
    string Key { get; }

    /// <summary>The service's name, as the page and the cards show it.</summary>
    string Name { get; }

    /// <summary>Where the user's library is on the service: the page the in-app browser opens, and the page to save.</summary>
    Uri LibraryPage { get; }

    /// <summary>The books on the page, or null when it isn't this service's library page.</summary>
    OnlinePage? Read(string html);

    /// <summary>Other sites the service signs in on, which the online reader follows as it follows the service's own.</summary>
    IReadOnlyList<string> SignInSites => [];

    /// <summary>
    /// Where a book's link goes: Also own for a service whose books are read there, as D&amp;D Beyond's are; Get it
    /// from for a store whose books are files still to download (Humble Bundle, DriveThruRPG).
    /// </summary>
    MetadataField LinkField => MetadataFields.AlsoOwn;

    /// <summary>
    /// Why a page of this service isn't one Read this page can read, when the reader can tell ("choose eBook under
    /// Platform"); null for the usual line.
    /// </summary>
    string? Hint(string html) => null;

    /// <summary>
    /// What a page title on the service carries after the page's own and the book's, besides the service's name, last
    /// first: D&amp;D Beyond's "… - Dungeons &amp; Dragons - Sources - D&amp;D Beyond".
    /// </summary>
    IReadOnlyList<string> TitleTail => [];

    /// <summary>
    /// Reading mode's stylesheet for the service's pages in the online reader: it hides what isn't the book, such as
    /// menus, ads and footers, and leaves the page's own contents. Null for a service without one.
    /// </summary>
    string? ReadingStyle => null;
}

/// <summary>Pages of books owned online, as the online reader and binders name them.</summary>
public static class OnlinePages
{
    static readonly string[] Separators = [" - ", " | ", " – ", " — "];

    /// <summary>The service a page is on, by its site or a site it signs in on; null for none Bibliotaph knows.</summary>
    public static IOnlineLibraryReader? ServiceOf(string link) =>
        SiteOf(link) is { } site
            ? OnlineImports.Services.FirstOrDefault(r => SiteOf(r.LibraryPage.AbsoluteUri) == site)
            : null;

    /// <summary>A page's site: its host without "www.", lowercase; null for a link that isn't on the web.</summary>
    public static string? SiteOf(string link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? (uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host).ToLowerInvariant()
            : null;

    /// <summary>
    /// A binder item's label for a page: its title without what follows the page's own name, the place ("D&amp;D
    /// Beyond"), the service's own tail ("Sources", "Dungeons &amp; Dragons") and the book's title, so "The Vault Door -
    /// The Ember Vault - Dungeons &amp; Dragons - Sources - D&amp;D Beyond" gives "The Vault Door". The book's own page
    /// keeps the book's title. Null when the page has no title.
    /// </summary>
    public static string? LabelOf(string? pageTitle, string? place, string? bookTitle = null, IOnlineLibraryReader? service = null)
    {
        var label = pageTitle?.Trim();
        if (string.IsNullOrEmpty(label)) return null;
        string[] tails = [.. place is null ? [] : new[] { place }, .. service is null ? [] : new[] { service.Name }, .. service?.TitleTail ?? []];
        for (var cut = true; cut;)
        {
            cut = false;
            foreach (var tail in tails)
                if (WithoutLast(label, tail) is { } shorter)
                    (label, cut) = (shorter, true);
        }
        return bookTitle is not null && WithoutLast(label, bookTitle) is { } chapter ? chapter : label;
    }

    /// <summary>The title without its last part when that part is <paramref name="part"/>, matched as titles are; else null.</summary>
    static string? WithoutLast(string title, string part)
    {
        var key = OnlineImports.TitleKey(part);
        if (key.Length == 0) return null;
        foreach (var separator in Separators)
        {
            for (var at = title.LastIndexOf(separator, StringComparison.Ordinal); at > 0; at = title.LastIndexOf(separator, at - 1, StringComparison.Ordinal))
            {
                if (OnlineImports.TitleKey(title[(at + separator.Length)..]) == key) return title[..at].Trim();
            }
        }
        return null;
    }
}

/// <summary>
/// D&amp;D Beyond's library page. The page is drawn by Next.js, whose data travels in the page as
/// <c>self.__next_f.push([1,"…"])</c> scripts; joined together, they hold the whole D&amp;D Beyond catalogue as
/// "sources" objects, each marked isOwned and isSharedWithMe for the user. That data is read rather than the page's
/// markup, whose class names change with every release, and it holds every owned book whichever tab is showing, so one
/// read is the whole library.
/// </summary>
public sealed partial class DndBeyondReader : IOnlineLibraryReader
{
    const string Site = "https://www.dndbeyond.com";

    public string Key => "dnd-beyond";

    public string Name => "D&D Beyond";

    public Uri LibraryPage { get; } = new($"{Site}/en/library?ownership=owned");

    /// <summary>D&amp;D Beyond's sign-in with a Wizards account goes through Wizards of the Coast's site.</summary>
    public IReadOnlyList<string> SignInSites { get; } = ["wizards.com"];

    public IReadOnlyList<string> TitleTail { get; } = ["Sources", "Dungeons & Dragons"];

    /// <summary>
    /// Hides the site's menus, ads, page-header extras, quick actions and footer; the book's contents and its table of
    /// contents stay.
    /// </summary>
    public string ReadingStyle => """
        #mega-menu-target, .ad-container, .page-header__extras, .compendium-quick-actions, #footer-push, .ddb-footer,
        .site-overlay { display: none !important; }
        """;

    public OnlinePage? Read(string html)
    {
        if (!html.Contains("dndbeyond", StringComparison.OrdinalIgnoreCase)) return null;
        var flight = FlightData(html);
        var books = new List<OnlineBook>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = false;
        foreach (var list in SourceLists(flight))
        {
            found = true;
            foreach (var source in list.EnumerateArray())
            {
                if (source.ValueKind != JsonValueKind.Object || Text(source, "name") is not { } name || LinkOf(source) is not { } link) continue;
                if (Flag(source, "isOwned"))
                {
                    if (seen.Add(link)) books.Add(Book(source, name, link));
                }
                else if (Flag(source, "isSharedWithMe")) shared.Add(link);
            }
        }
        if (!found) return null;
        shared.ExceptWith(seen);
        return new OnlinePage(books, shared.Count, Complete: true);
    }

    static OnlineBook Book(JsonElement source, string name, string link) => new(name.Trim(), link)
    {
        Publisher = Text(source, "publisher"),
        Type = Text(source, "type"),
        // Everything D&D Beyond sells is for D&D; its rulesets are "5e" (2014) and "5.5e" (2024), which the edition
        // vocabulary knows by those names.
        System = "D&D",
        Edition = source.TryGetProperty("ruleset", out var ruleset) && ruleset.ValueKind == JsonValueKind.Object ? Text(ruleset, "name") : null,
        Year = Text(source, "releaseDate") is { Length: >= 4 } date
            && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? year : null,
        CoverUrl = source.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.Object ? Text(image, "src") : null,
    };

    /// <summary>The book's page: relativePath is "/sources/dnd/…" on D&amp;D Beyond's site.</summary>
    static string? LinkOf(JsonElement source)
    {
        if (Text(source, "relativePath") is not { } path) return null;
        // Checked first: on Linux, "/sources/…" is also an absolute file: URI.
        if (path.StartsWith('/')) return path.StartsWith("//", StringComparison.Ordinal) ? null : Site + path;
        return Uri.TryCreate(path, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https" ? absolute.AbsoluteUri : null;
    }

    static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } text
            && text.Trim().Length > 0 ? text.Trim() : null;

    static bool Flag(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// The Next.js data in the page, its chunks joined: each push is a JSON string literal, and one object can be split
    /// across two of them.
    /// </summary>
    internal static string FlightData(string html)
    {
        var data = new StringBuilder();
        foreach (Match push in Push().Matches(html))
        {
            try
            {
                data.Append(JsonSerializer.Deserialize<string>(push.Groups[1].ValueSpan));
            }
            catch (JsonException)
            {
                // A chunk that isn't a plain string literal holds nothing of the library.
            }
        }
        return data.ToString();
    }

    /// <summary>Every "sources" list in the data: the catalogue, which the page can carry more than once.</summary>
    static IEnumerable<JsonElement> SourceLists(string flight)
    {
        const string Marker = "\"sources\":[";
        for (var at = flight.IndexOf(Marker, StringComparison.Ordinal); at >= 0; at = flight.IndexOf(Marker, at + Marker.Length, StringComparison.Ordinal))
        {
            var bytes = Encoding.UTF8.GetBytes(flight[(at + Marker.Length - 1)..]);
            JsonElement? list = null;
            try
            {
                var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { AllowTrailingCommas = false });
                if (reader.Read() && reader.TokenType == JsonTokenType.StartArray && JsonElement.TryParseValue(ref reader, out var parsed)) list = parsed;
            }
            catch (JsonException)
            {
                // A list cut short, as in a page saved before it finished loading: there is nothing to read in it.
            }
            if (list is { } found) yield return found;
        }
    }

    [GeneratedRegex("""self\.__next_f\.push\(\[1,("(?:[^"\\]|\\.)*")\]\)""", RegexOptions.CultureInvariant)]
    private static partial Regex Push();
}

/// <summary>
/// A page from Bibliotaph's browser saved where the user chose (Save page), as the page showed it: the document as drawn,
/// so a page built by script keeps what it showed. Nothing else on the page (images, scripts) is fetched or saved.
/// </summary>
public static class SavedPages
{
    public static async Task SaveAsync(string path, string html, CancellationToken ct = default)
    {
        var partial = path + ".partial";
        await File.WriteAllTextAsync(partial, "<!DOCTYPE html>\n" + html, Encoding.UTF8, ct);
        File.Move(partial, path, overwrite: true);
    }
}
