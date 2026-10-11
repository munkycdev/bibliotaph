using System.Text;

namespace Bibliotaph.Core.Metadata;

/// <summary>
/// A Get it from value (scenario review, row 7): a store's key, and the book's page there when someone has one. It is
/// stored as the key, or the key and the link after a space: "drivethrurpg https://www.drivethrurpg.com/…". Only
/// absolute http and https links are kept, so a link is always one a browser can open.
/// </summary>
public readonly record struct StoreLink(string Store, string? Url)
{
    /// <summary>The store a link goes to when its site is none the vocabulary knows.</summary>
    public const string OtherStore = "other-store";

    /// <summary>The longest <see cref="ShortUrl"/>.</summary>
    const int ShortLength = 48;

    public bool HasLink => Url is not null;

    /// <summary>A stored value: the key, then the link after a space when there is one.</summary>
    public static StoreLink Parse(string stored)
    {
        var space = stored.IndexOf(' ', StringComparison.Ordinal);
        if (space < 0) return new StoreLink(stored, null);
        var url = stored[(space + 1)..].Trim();
        return new StoreLink(stored[..space], url.Length > 0 ? url : null);
    }

    /// <summary>The stored form.</summary>
    public override string ToString() => Url is null ? Store : $"{Store} {Url}";

    /// <summary>The link as it reads on a button: its site and path, without "https://" or "www.", cut short.</summary>
    public string? ShortUrl
    {
        get
        {
            if (Url is null || !Uri.TryCreate(Url, UriKind.Absolute, out var uri)) return Url;
            var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
            var text = (host + uri.AbsolutePath).TrimEnd('/');
            return text.Length <= ShortLength ? text : text[..(ShortLength - 1)] + "…";
        }
    }

    /// <summary>
    /// What was typed for one store: its name, and the web link in it if any ("DriveThruRPG https://…", or a link on its
    /// own). The problem says why a link can't be kept: it isn't http or https, or there are two.
    /// </summary>
    public static (string Name, string? Url, string? Problem) Split(string typed)
    {
        string? url = null;
        var name = new List<string>();
        foreach (var word in typed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var bare = word.Trim('(', ')', '<', '>', '[', ']', '"', '\'');
            if (!LooksLikeLink(bare))
            {
                name.Add(word);
                continue;
            }
            if (url is not null) return (typed, null, "Give one link for each store, and separate stores with commas.");
            if (WebAddress(bare) is not { } address)
                return (typed, null, $"“{(bare.Length > 40 ? bare[..40] + "…" : bare)}” isn't a web link a browser can open (http or https).");
            url = address;
        }
        return (string.Join(' ', name).Trim(' ', '-', '–', '—', ':', '·', '|'), url, null);
    }

    /// <summary>
    /// What was typed for one store, filed against the store vocabulary: the store it names, or else the store whose
    /// site the link is on (a store's site is one of its names: "drivethrurpg.com"), or Other store for a link to a site
    /// the vocabulary doesn't know. Term is null for a name the vocabulary doesn't know, which becomes the user's own.
    /// </summary>
    public static (Term? Term, string Name, string? Url) Read(string typed, Vocabulary vocabulary)
    {
        var store = MetadataFields.GetItFrom.Vocabulary!;
        var (name, url, _) = Split(MetadataText.Tidy(typed));
        if (MetadataText.Normalize(name).Length > 0) return (vocabulary.Resolve(store, name), name, url);
        var site = url is null ? null : FromSite(url, vocabulary) ?? vocabulary.Find(store, OtherStore);
        return (site, site?.Label ?? "Other store", url);
    }

    /// <summary>The store a link's site belongs to: "shop.example.itch.io" is itch.io's, through its site name "itch.io".</summary>
    public static Term? FromSite(string url, Vocabulary vocabulary)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.ToLowerInvariant();
        while (true)
        {
            if (vocabulary.Resolve(MetadataFields.GetItFrom.Vocabulary!, host) is { } term) return term;
            var dot = host.IndexOf('.', StringComparison.Ordinal);
            if (dot < 0 || host.IndexOf('.', dot + 1) < 0) return null;
            host = host[(dot + 1)..];
        }
    }

    /// <summary>
    /// Splits a typed list of stores at commas and semicolons, except those inside a web link: a link runs to the next
    /// space, and only a comma or semicolon at its very end separates.
    /// </summary>
    public static IReadOnlyList<string> SplitList(string text)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        var inLink = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) inLink = false;
            else if (!inLink && (i == 0 || !char.IsLetterOrDigit(text[i - 1])) && StartsLink(text.AsSpan(i))) inLink = true;
            if (c is ',' or ';' && (!inLink || i + 1 == text.Length || char.IsWhiteSpace(text[i + 1])))
            {
                Flush();
                inLink = false;
                continue;
            }
            current.Append(c);
        }
        Flush();
        return values;

        void Flush()
        {
            var value = current.ToString().Trim();
            if (value.Length > 0) values.Add(value);
            current.Clear();
        }
    }

    static bool StartsLink(ReadOnlySpan<char> text) =>
        text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("www.", StringComparison.OrdinalIgnoreCase);

    static bool LooksLikeLink(string word) => StartsLink(word) || word.Contains("://", StringComparison.Ordinal);

    /// <summary>An absolute http or https address with a dotted host, in its canonical form; "www.…" gets https.</summary>
    public static string? WebAddress(string text)
    {
        var candidate = text.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + text : text;
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Contains('.', StringComparison.Ordinal)
            && string.IsNullOrEmpty(uri.UserInfo)
            ? uri.AbsoluteUri
            : null;
    }
}
