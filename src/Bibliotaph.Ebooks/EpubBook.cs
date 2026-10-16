using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Bibliotaph.Ebooks;

/// <summary>What an e-book says about itself in its package.</summary>
public sealed record EbookMetadata
{
    public string? Title { get; init; }

    public IReadOnlyList<string> Authors { get; init; } = [];

    public string? Publisher { get; init; }

    /// <summary>The publication date as written ("2019", "2019-05-14").</summary>
    public string? Date { get; init; }

    public string? Language { get; init; }

    /// <summary>An ISBN-10 or ISBN-13 among the identifiers, digits only (and a final X).</summary>
    public string? Isbn { get; init; }

    public IReadOnlyList<string> Subjects { get; init; } = [];

    /// <summary>The description as plain text, markup removed.</summary>
    public string? Description { get; init; }
}

/// <summary>One document of the reading order: a chapter file, by its path inside the archive.</summary>
public sealed record EpubSpineItem(string Path, string MediaType, bool Linear, bool FixedLayout);

/// <summary>An entry of the contents or the page list: a title and where it points (a path and an optional fragment).</summary>
public sealed record EpubNavPoint(string Title, string Path, string? Fragment, int Depth);

/// <summary>
/// An EPUB (2 or 3) opened over a stream that stays open while it is used: its package, reading order, contents, page
/// list, cover and protection. Every entry is read through <see cref="EbookLimits"/>, and no XML it reads may load a
/// DTD or an external entity.
/// </summary>
public sealed partial class EpubBook : IDisposable
{
    const string ContainerPath = "META-INF/container.xml";
    const string EncryptionPath = "META-INF/encryption.xml";

    /// <summary>Font obfuscation (IDPF and Adobe): only embedded fonts are scrambled, so the book still reads. Not DRM.</summary>
    static readonly HashSet<string> FontObfuscation = new(StringComparer.Ordinal)
    {
        "http://www.idpf.org/2008/embedding",
        "http://ns.adobe.com/pdf/enc#RC",
    };

    readonly ZipArchive _zip;
    readonly EbookLimits _limits;
    readonly Dictionary<string, ZipArchiveEntry> _entries;
    readonly Dictionary<string, ZipArchiveEntry> _entriesIgnoringCase;
    readonly Dictionary<string, string> _mediaTypes = new(StringComparer.Ordinal);
    readonly HashSet<string> _encrypted = new(StringComparer.Ordinal);
    long _bytesRead;

    EpubBook(ZipArchive zip, EbookLimits limits)
    {
        _zip = zip;
        _limits = limits;
        if (zip.Entries.Count > limits.Entries) throw new EbookException($"The e-book holds more than {limits.Entries:N0} files.");
        _entries = new(StringComparer.Ordinal);
        _entriesIgnoringCase = new(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            _entries.TryAdd(name, entry);
            _entriesIgnoringCase.TryAdd(name, entry);
        }
    }

    public EbookMetadata Metadata { get; private set; } = new();

    public IReadOnlyList<EpubSpineItem> Spine { get; private set; } = [];

    public IReadOnlyList<EpubNavPoint> Contents { get; private set; } = [];

    /// <summary>Where each printed page of the book it was made from begins, when the publisher included that.</summary>
    public IReadOnlyList<EpubNavPoint> PageList { get; private set; } = [];

    /// <summary>The cover image's path, when the package names one or the first chapter starts with an image.</summary>
    public string? CoverPath { get; private set; }

    /// <summary>Whether every page is drawn to a fixed size (comics, some art books), rather than flowing.</summary>
    public bool FixedLayout { get; private set; }

    /// <summary>
    /// Whether the book's text is encrypted (DRM such as Adobe ADEPT, Readium LCP or Apple FairPlay). Its package
    /// is still readable, so its metadata is.
    /// </summary>
    public bool IsProtected { get; private set; }

    /// <summary>Opens an EPUB. The stream must be seekable and stay open until the book is disposed.</summary>
    public static EpubBook Open(Stream stream, EbookLimits? limits = null)
    {
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex)
        {
            throw new EbookException("This file isn't a readable EPUB; it may be damaged or incomplete.", ex);
        }

        var book = new EpubBook(zip, limits ?? EbookLimits.Default);
        try
        {
            book.ReadPackage();
            return book;
        }
        catch
        {
            book.Dispose();
            throw;
        }
    }

    public void Dispose() => _zip.Dispose();

    /// <summary>Whether the archive holds <paramref name="path"/>.</summary>
    public bool Contains(string path) => Find(path) is not null;

    /// <summary>The media type the package gives <paramref name="path"/>, or null when it isn't in the manifest.</summary>
    public string? MediaTypeOf(string path) => _mediaTypes.GetValueOrDefault(path);

    /// <summary>Whether <paramref name="path"/> is encrypted, so its bytes are not the file itself.</summary>
    public bool IsEncrypted(string path) => _encrypted.Contains(path);

    /// <summary>An entry's bytes, within the limits.</summary>
    public byte[] ReadBytes(string path)
    {
        var entry = Find(path) ?? throw new EbookException("A file the e-book refers to is missing from it.");
        if (entry.Length > _limits.EntryBytes) throw TooLarge();
        using var input = entry.Open();
        using var output = new MemoryStream(entry.Length is > 0 and < int.MaxValue ? (int)entry.Length : 0);
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            if (output.Length + read > _limits.EntryBytes) throw TooLarge();
            _bytesRead += read;
            if (_bytesRead > _limits.BookBytes) throw TooLarge();
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    /// <summary>An entry as text, decoded by its byte order mark or declared encoding, else UTF-8.</summary>
    public string ReadText(string path) => Decode(ReadBytes(path));

    /// <summary>A path inside the archive for <paramref name="href"/> as written in <paramref name="basePath"/>, without its fragment.</summary>
    public static (string Path, string? Fragment) Resolve(string basePath, string href)
    {
        var hash = href.IndexOf('#', StringComparison.Ordinal);
        var fragment = hash >= 0 ? Uri.UnescapeDataString(href[(hash + 1)..]) : null;
        var relative = Uri.UnescapeDataString(hash >= 0 ? href[..hash] : href).Replace('\\', '/');
        var query = relative.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0) relative = relative[..query];
        if (relative.Length == 0) return (basePath, fragment);

        var parts = relative.StartsWith('/') ? new List<string>() : [.. basePath.Split('/')[..^1]];
        foreach (var part in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        return (string.Join('/', parts), string.IsNullOrEmpty(fragment) ? null : fragment);
    }

    ZipArchiveEntry? Find(string path) => _entries.GetValueOrDefault(path) ?? _entriesIgnoringCase.GetValueOrDefault(path);

    EbookException TooLarge() => new($"The e-book is larger than Bibliotaph reads ({_limits.EntryBytes >> 20} MB a file, {_limits.BookBytes >> 20} MB in all once unpacked).");

    void ReadPackage()
    {
        if (Find(ContainerPath) is null) throw new EbookException("This file isn't an EPUB: it has no package description.");
        var container = LoadXml(ContainerPath);
        var opfPath = container.Descendants().Where(e => e.Name.LocalName == "rootfile")
            .Select(e => (Path: (string?)e.Attribute("full-path"), Type: (string?)e.Attribute("media-type")))
            .Where(r => !string.IsNullOrEmpty(r.Path))
            .OrderBy(r => r.Type == "application/oebps-package+xml" ? 0 : 1)
            .Select(r => r.Path!)
            .FirstOrDefault();
        if (opfPath is null || Find(opfPath) is null) throw new EbookException("This EPUB's package description is missing.");

        ReadEncryption();
        var opf = LoadXml(opfPath);
        var package = opf.Root!;
        var metadata = Child(package, "metadata");
        var manifest = Child(package, "manifest");
        var spine = Child(package, "spine");
        if (manifest is null || spine is null) throw new EbookException("This EPUB's package description has no reading order.");

        var items = new Dictionary<string, (string Path, string MediaType, string Properties)>(StringComparer.Ordinal);
        foreach (var item in manifest.Elements().Where(e => e.Name.LocalName == "item"))
        {
            if ((string?)item.Attribute("id") is not { } id || (string?)item.Attribute("href") is not { } href) continue;
            var path = Resolve(opfPath, href).Path;
            var type = ((string?)item.Attribute("media-type") ?? "").Trim().ToLowerInvariant();
            items.TryAdd(id, (path, type, (string?)item.Attribute("properties") ?? ""));
            _mediaTypes.TryAdd(path, type);
        }

        var metas = metadata?.Elements().Where(e => e.Name.LocalName == "meta").ToList() ?? [];
        FixedLayout = metas.Any(m => (string?)m.Attribute("property") == "rendition:layout" && m.Value.Trim() == "pre-paginated");

        var spineItems = new List<EpubSpineItem>();
        foreach (var itemref in spine.Elements().Where(e => e.Name.LocalName == "itemref"))
        {
            if ((string?)itemref.Attribute("idref") is not { } idref || !items.TryGetValue(idref, out var item)) continue;
            if (!IsHtml(item.MediaType, item.Path) || Find(item.Path) is null) continue;
            var properties = ((string?)itemref.Attribute("properties") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var fixedLayout = properties.Contains("rendition:layout-pre-paginated") || FixedLayout && !properties.Contains("rendition:layout-reflowable");
            spineItems.Add(new EpubSpineItem(item.Path, item.MediaType, (string?)itemref.Attribute("linear") != "no", fixedLayout));
        }
        if (spineItems.Count == 0) throw new EbookException("This EPUB has no chapters to read.");
        Spine = spineItems;
        IsProtected = spineItems.Any(s => _encrypted.Contains(s.Path));

        Metadata = ReadMetadata(metadata);
        ReadNavigation(items, (string?)spine.Attribute("toc"));
        CoverPath = FindCover(items, metas, package, opfPath);
    }

    void ReadEncryption()
    {
        if (Find(EncryptionPath) is null) return;
        XDocument encryption;
        try
        {
            encryption = LoadXml(EncryptionPath);
        }
        catch (EbookException)
        {
            // An encryption manifest that can't be read can't be trusted to list only fonts.
            IsProtected = true;
            return;
        }
        foreach (var data in encryption.Descendants().Where(e => e.Name.LocalName == "EncryptedData"))
        {
            var algorithm = (string?)data.Descendants().FirstOrDefault(e => e.Name.LocalName == "EncryptionMethod")?.Attribute("Algorithm") ?? "";
            if (FontObfuscation.Contains(algorithm)) continue;
            foreach (var reference in data.Descendants().Where(e => e.Name.LocalName == "CipherReference"))
                if ((string?)reference.Attribute("URI") is { Length: > 0 } uri)
                    _encrypted.Add(Resolve("", uri).Path);
        }
    }

    static EbookMetadata ReadMetadata(XElement? metadata)
    {
        if (metadata is null) return new EbookMetadata();
        var dc = metadata.Elements().ToList();
        List<XElement> Named(string name) => [.. dc.Where(e => e.Name.LocalName == name && e.Name.NamespaceName.Contains("purl.org/dc", StringComparison.Ordinal))];

        // EPUB 3 marks the main title with a refinement; otherwise the first title is the book's.
        var titles = Named("title");
        var main = titles.FirstOrDefault(t => (string?)t.Attribute("id") is { } id && dc.Any(m => m.Name.LocalName == "meta"
            && (string?)m.Attribute("refines") == "#" + id && (string?)m.Attribute("property") == "title-type" && m.Value.Trim() == "main"));

        return new EbookMetadata
        {
            Title = Text(main ?? titles.FirstOrDefault()),
            Authors = [.. Named("creator").Select(Text).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)],
            Publisher = Text(Named("publisher").FirstOrDefault()),
            Date = Text(Named("date").FirstOrDefault()),
            Language = Text(Named("language").FirstOrDefault()),
            Isbn = Named("identifier").Select(Text).OfType<string>().Select(IsbnOf).OfType<string>().FirstOrDefault(),
            Subjects = [.. Named("subject").Select(Text).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)],
            Description = Text(Named("description").FirstOrDefault()) is { } description ? HtmlText.Plain(description) : null,
        };
    }

    static string? Text(XElement? element) =>
        element is null ? null : Whitespace().Replace(element.Value, " ").Trim() is { Length: > 0 } text ? text : null;

    /// <summary>An ISBN in an identifier ("urn:isbn:978-...", "ISBN 0-..."), checked by its check digit.</summary>
    internal static string? IsbnOf(string identifier)
    {
        var digits = new string([.. identifier.Where(c => char.IsAsciiDigit(c) || c is 'x' or 'X')]).ToUpperInvariant();
        if (digits.Length == 13 && digits.All(char.IsAsciiDigit) && digits[..3] is "978" or "979")
        {
            var sum = digits[..12].Select((c, i) => (c - '0') * (i % 2 == 0 ? 1 : 3)).Sum();
            return (10 - sum % 10) % 10 == digits[12] - '0' ? digits : null;
        }
        if (digits.Length == 10 && digits[..9].All(char.IsAsciiDigit))
        {
            var sum = digits[..9].Select((c, i) => (c - '0') * (10 - i)).Sum() + (digits[9] == 'X' ? 10 : digits[9] - '0');
            return digits[9] is 'X' or >= '0' and <= '9' && sum % 11 == 0 ? digits : null;
        }
        return null;
    }

    void ReadNavigation(Dictionary<string, (string Path, string MediaType, string Properties)> items, string? ncxId)
    {
        // EPUB 3's navigation document first; EPUB 2's NCX when there is none, or it lists nothing.
        if (items.Values.FirstOrDefault(i => i.Properties.Split(' ').Contains("nav")) is { Path: { } navPath } && Find(navPath) is not null
            && !_encrypted.Contains(navPath))
        {
            var (contents, pages) = HtmlNavigation.Read(ReadText(navPath), navPath);
            Contents = contents;
            PageList = pages;
        }
        if (Contents.Count > 0 && PageList.Count > 0) return;

        var ncx = ncxId is not null && items.TryGetValue(ncxId, out var item) ? item.Path
            : items.Values.FirstOrDefault(i => i.MediaType == "application/x-dtbncx+xml").Path;
        if (ncx is null || Find(ncx) is null || _encrypted.Contains(ncx)) return;
        XDocument doc;
        try
        {
            doc = LoadXml(ncx);
        }
        catch (EbookException)
        {
            return; // The contents are a convenience; the book reads without them.
        }
        if (Contents.Count == 0 && doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "navMap") is { } map)
        {
            var points = new List<EpubNavPoint>();
            void Walk(XElement parent, int depth)
            {
                foreach (var point in parent.Elements().Where(e => e.Name.LocalName == "navPoint"))
                {
                    if (points.Count >= MaxNavPoints) return;
                    if (NcxTarget(point, ncx) is { } target) points.Add(target with { Depth = depth });
                    if (depth < MaxNavDepth) Walk(point, depth + 1);
                }
            }
            Walk(map, 0);
            Contents = points;
        }
        if (PageList.Count == 0 && doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "pageList") is { } list)
            PageList = [.. list.Elements().Where(e => e.Name.LocalName == "pageTarget").Take(MaxNavPoints)
                .Select(p => NcxTarget(p, ncx)).OfType<EpubNavPoint>()];
    }

    internal const int MaxNavPoints = 20_000;
    internal const int MaxNavDepth = 32;

    static EpubNavPoint? NcxTarget(XElement point, string ncxPath)
    {
        var label = point.Elements().FirstOrDefault(e => e.Name.LocalName == "navLabel")?.Value;
        var src = (string?)point.Elements().FirstOrDefault(e => e.Name.LocalName == "content")?.Attribute("src");
        if (src is null) return null;
        var (path, fragment) = Resolve(ncxPath, src);
        return new EpubNavPoint(Whitespace().Replace(label ?? "", " ").Trim(), path, fragment, 0);
    }

    string? FindCover(Dictionary<string, (string Path, string MediaType, string Properties)> items, List<XElement> metas, XElement package, string opfPath)
    {
        bool Usable(string? path) => path is not null && Find(path) is not null && !_encrypted.Contains(path) && IsImage(MediaTypeOf(path), path);

        // EPUB 3: the manifest marks the cover image.
        if (items.Values.FirstOrDefault(i => i.Properties.Split(' ').Contains("cover-image")).Path is { } marked && Usable(marked)) return marked;

        // EPUB 2: <meta name="cover" content="item id">, sometimes with the path in place of the id.
        if (metas.FirstOrDefault(m => (string?)m.Attribute("name") == "cover") is { } meta && (string?)meta.Attribute("content") is { } content)
        {
            if (items.TryGetValue(content, out var item) && Usable(item.Path)) return item.Path;
            if (items.Values.FirstOrDefault(i => i.Path.EndsWith(content, StringComparison.OrdinalIgnoreCase)).Path is { } byPath && Usable(byPath)) return byPath;
        }

        // The cover page the guide names, else the first chapter: its first image.
        var guideCover = Child(package, "guide")?.Elements().FirstOrDefault(e => e.Name.LocalName == "reference" && (string?)e.Attribute("type") == "cover");
        var pages = new List<string>();
        if ((string?)guideCover?.Attribute("href") is { } href) pages.Add(Resolve(opfPath, href).Path);
        pages.Add(Spine[0].Path);
        foreach (var page in pages.Distinct())
        {
            if (Find(page) is null || _encrypted.Contains(page)) continue;
            var media = MediaTypeOf(page) ?? "";
            if (IsImage(media, page)) return Usable(page) ? page : null;
            if (!IsHtml(media, page)) continue;
            if (HtmlText.FirstImage(ReadText(page)) is { } src && Resolve(page, src).Path is var image && Usable(image)) return image;
        }
        return null;
    }

    /// <summary>The cover image's bytes, or null when there is none.</summary>
    public byte[]? ReadCover() => CoverPath is null ? null : ReadBytes(CoverPath);

    static XElement? Child(XElement parent, string name) => parent.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    static bool IsHtml(string? mediaType, string path) => mediaType is "application/xhtml+xml" or "text/html"
        || mediaType is null or "" && Path.GetExtension(path).ToLowerInvariant() is ".xhtml" or ".html" or ".htm";

    /// <summary>A raster image Windows can decode; SVG covers are left out, since they can't be drawn as a thumbnail.</summary>
    static bool IsImage(string? mediaType, string path) => mediaType is "image/jpeg" or "image/png" or "image/gif" or "image/webp" or "image/bmp"
        || mediaType is null or "" && Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp";

    XDocument LoadXml(string path)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = _limits.EntryBytes,
        };
        try
        {
            using var reader = XmlReader.Create(new StringReader(ReadText(path)), settings);
            return XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new EbookException("This EPUB's package description is damaged.", ex);
        }
    }

    /// <summary>Text by its byte order mark, else the encoding an XML declaration or meta tag names, else UTF-8.</summary>
    internal static string Decode(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 1024));
        if (DeclaredEncoding().Match(head) is { Success: true } match)
        {
            try
            {
                var name = match.Groups[1].Value;
                var encoding = CodePagesEncodingProvider.Instance.GetEncoding(name) ?? Encoding.GetEncoding(name);
                if (encoding.CodePage != Encoding.UTF8.CodePage) return encoding.GetString(bytes);
            }
            catch (ArgumentException)
            {
                // An encoding .NET doesn't know: UTF-8 reads most of it right.
            }
        }
        return Encoding.UTF8.GetString(bytes);
    }

    [GeneratedRegex(@"(?:encoding|charset)\s*=\s*[""']?([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DeclaredEncoding();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
