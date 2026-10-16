using System.IO.Compression;
using System.Text;

namespace Bibliotaph.Ebooks.Tests;

/// <summary>
/// Builds small EPUBs in memory for the tests: invented text only, never a real book. Each chapter is XHTML body
/// markup; the package, contents and page list are written around them.
/// </summary>
public sealed class SyntheticEpub
{
    readonly List<(string Id, string Href, string Body)> _chapters = [];
    readonly List<(string Id, string Path, byte[] Bytes, string MediaType, string? Properties)> _files = [];
    readonly List<(string Path, string Algorithm)> _encrypted = [];

    public string Title { get; set; } = "The Sunken Lantern";

    /// <summary>Extra Dublin Core and meta elements for the package's metadata.</summary>
    public string ExtraMetadata { get; set; } = "";

    /// <summary>The navigation document's body, or null to write a contents list of the chapters' first headings.</summary>
    public string? NavBody { get; set; }

    /// <summary>An EPUB 2 NCX's navMap and pageList, or null for none.</summary>
    public string? Ncx { get; set; }

    public bool FixedLayout { get; set; }

    public bool WriteContainer { get; set; } = true;

    public SyntheticEpub Chapter(string href, string body)
    {
        _chapters.Add(($"c{_chapters.Count + 1}", href, body));
        return this;
    }

    public SyntheticEpub File(string path, byte[] bytes, string mediaType, string? properties = null)
    {
        _files.Add(($"f{_files.Count}", path, bytes, mediaType, properties));
        return this;
    }

    public SyntheticEpub Encrypt(string path, string algorithm = "http://www.w3.org/2001/04/xmlenc#aes256-cbc")
    {
        _encrypted.Add((path, algorithm));
        return this;
    }

    public byte[] Build()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "mimetype", "application/epub+zip");
            if (WriteContainer)
                Write(zip, "META-INF/container.xml", """
                    <?xml version="1.0"?>
                    <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                      <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
                    </container>
                    """);
            if (_encrypted.Count > 0)
                Write(zip, "META-INF/encryption.xml", $"""
                    <?xml version="1.0"?>
                    <encryption xmlns="urn:oasis:names:tc:opendocument:xmlns:container" xmlns:enc="http://www.w3.org/2001/04/xmlenc#">
                    {string.Concat(_encrypted.Select(e => $"""<enc:EncryptedData><enc:EncryptionMethod Algorithm="{e.Algorithm}"/><enc:CipherData><enc:CipherReference URI="{e.Path}"/></enc:CipherData></enc:EncryptedData>"""))}
                    </encryption>
                    """);

            var manifest = new StringBuilder();
            manifest.Append("""<item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>""");
            if (Ncx is not null) manifest.Append("""<item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>""");
            foreach (var (id, href, _) in _chapters) manifest.Append($"""<item id="{id}" href="{href}" media-type="application/xhtml+xml"/>""");
            foreach (var (fileId, path, _, type, properties) in _files)
                manifest.Append($"""<item id="{fileId}" href="{path}" media-type="{type}"{(properties is null ? "" : $" properties=\"{properties}\"")}/>""");
            var spine = string.Concat(_chapters.Select(c => $"""<itemref idref="{c.Id}"/>"""));
            var layout = FixedLayout ? """<meta property="rendition:layout">pre-paginated</meta>""" : "";

            Write(zip, "OEBPS/content.opf", $"""
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="uid">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                    <dc:identifier id="uid">urn:uuid:00000000-0000-0000-0000-000000000001</dc:identifier>
                    <dc:title>{Title}</dc:title>
                    <dc:language>en</dc:language>
                    {layout}
                    {ExtraMetadata}
                  </metadata>
                  <manifest>{manifest}</manifest>
                  <spine{(Ncx is null ? "" : " toc=\"ncx\"")}>{spine}</spine>
                </package>
                """);

            var nav = NavBody ?? $"""<nav epub:type="toc"><ol>{string.Concat(_chapters.Select(c => $"""<li><a href="{c.Href}">{c.Id}</a></li>"""))}</ol></nav>""";
            Write(zip, "OEBPS/nav.xhtml", Xhtml(nav));
            if (Ncx is not null)
                Write(zip, "OEBPS/toc.ncx", $"""<?xml version="1.0"?><ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1">{Ncx}</ncx>""");
            foreach (var (_, href, body) in _chapters) Write(zip, "OEBPS/" + Uri.UnescapeDataString(href), Xhtml(body));
            foreach (var (_, path, bytes, _, _) in _files)
            {
                using var stream = zip.CreateEntry("OEBPS/" + path).Open();
                stream.Write(bytes);
            }
        }
        return output.ToArray();
    }

    public EpubBook Open(EbookLimits? limits = null) => EpubBook.Open(new MemoryStream(Build()), limits);

    static string Xhtml(string body) => $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <!DOCTYPE html>
        <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
        <head><title>Chapter</title><style>p { color: red; }</style></head>
        <body>{{body}}</body>
        </html>
        """;

    static void Write(ZipArchive zip, string name, string text)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>A paragraph of invented words, <paramref name="length"/> characters long, in sentences.</summary>
    public static string Words(int length, string seed = "lantern")
    {
        var text = new StringBuilder();
        var n = 0;
        while (text.Length < length)
        {
            text.Append($"The {seed} {n} drifts past the reef. ");
            n++;
        }
        return text.ToString(0, length).Trim();
    }
}
