using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Bibliotaph.Ebooks;

/// <summary>
/// A Mobipocket (.mobi, .prc) or Kindle (.azw, .azw3) book opened over a stream that stays open while it is used.
/// The file is a Palm database of records: a header, compressed text, images and fonts, and indexes. Its text is
/// rebuilt into HTML parts with made-up paths, so the rest of Bibliotaph reads it as it reads an EPUB:
/// <list type="bullet">
/// <item>A KF8 book (AZW3, or the KF8 half of a .mobi that has both) is put back together from its skeletons and
/// fragments into the files it was made from, with its stylesheets from the other flows.</item>
/// <item>An older Mobipocket book is one long HTML text, cut into parts at its page breaks.</item>
/// </list>
/// Links by position (<c>filepos</c>, <c>kindle:pos</c>) become links to anchors placed at those positions, and
/// images and fonts referred to by record number become paths. A book whose text is encrypted is protected: its
/// header and metadata are still read.
/// </summary>
public sealed partial class MobiBook : IEbook
{
    const int Uncompressed = 1, PalmDocCompression = 2, HuffCdic = 0x4448;
    const uint None = 0xFFFFFFFF;
    const string Html = "application/xhtml+xml";

    readonly Stream _stream;
    readonly EbookLimits _limits;
    readonly long[] _offsets;
    readonly Dictionary<string, (Func<byte[]> Read, string MediaType)> _files = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _filesIgnoringCase = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _parts = new(StringComparer.Ordinal);
    long _bytesRead;

    MobiBook(Stream stream, EbookLimits limits, long[] offsets)
    {
        _stream = stream;
        _limits = limits;
        _offsets = offsets;
    }

    public EbookMetadata Metadata { get; private set; } = new();

    public IReadOnlyList<EpubSpineItem> Spine { get; private set; } = [];

    public IReadOnlyList<EpubNavPoint> Contents { get; private set; } = [];

    /// <summary>Always empty: Kindle books keep their printed page numbers in a separate file (.apnx), which isn't read.</summary>
    public IReadOnlyList<EpubNavPoint> PageList => [];

    public string? CoverPath { get; private set; }

    public bool FixedLayout { get; private set; }

    public bool IsProtected { get; private set; }

    /// <summary>Whether the text came from the book's KF8 version.</summary>
    public bool IsKf8 { get; private set; }

    public static MobiBook Open(Stream stream, EbookLimits? limits = null)
    {
        limits ??= EbookLimits.Default;
        try
        {
            var book = new MobiBook(stream, limits, Records(stream));
            book.Read();
            return book;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException or DecoderFallbackException
            or EndOfStreamException)
        {
            throw new EbookException("This Kindle book is damaged or incomplete.", ex);
        }
    }

    /// <summary>The stream is the caller's, as an EPUB's is.</summary>
    public void Dispose() { }

    public bool Contains(string path) => Find(path) is not null;

    public string? MediaTypeOf(string path) => Find(path) is { } name ? _files[name].MediaType : null;

    public bool IsEncrypted(string path) => false;

    public byte[] ReadBytes(string path) =>
        Find(path) is { } name ? _files[name].Read() : throw new EbookException("A file the e-book refers to is missing from it.");

    public string ReadText(string path) =>
        Find(path) is { } name && _parts.TryGetValue(name, out var text) ? text : Encoding.UTF8.GetString(ReadBytes(path));

    public byte[]? ReadCover() => CoverPath is null ? null : ReadBytes(CoverPath);

    string? Find(string path) => _files.ContainsKey(path) ? path : _filesIgnoringCase.GetValueOrDefault(path);

    void Add(string path, string mediaType, Func<byte[]> read)
    {
        _files[path] = (read, mediaType);
        _filesIgnoringCase.TryAdd(path, path);
    }

    static long[] Records(Stream stream)
    {
        var header = new byte[78];
        stream.Position = 0;
        stream.ReadExactly(header);
        var count = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(76));
        if (count == 0) throw new EbookException("This Kindle book has no text.");
        var list = new byte[count * 8];
        stream.ReadExactly(list);
        var offsets = new long[count + 1];
        for (var i = 0; i < count; i++)
        {
            offsets[i] = BinaryPrimitives.ReadUInt32BigEndian(list.AsSpan(i * 8));
            if (offsets[i] > stream.Length || i > 0 && offsets[i] < offsets[i - 1]) throw new EbookException("This Kindle book is damaged or incomplete.");
        }
        offsets[count] = stream.Length;
        return offsets;
    }

    int RecordCount => _offsets.Length - 1;

    /// <summary>A record's bytes, or null when there is no such record.</summary>
    byte[]? Record(int index)
    {
        if (index < 0 || index >= RecordCount) return null;
        var length = _offsets[index + 1] - _offsets[index];
        if (length > _limits.EntryBytes) throw TooLarge();
        var bytes = new byte[length];
        _stream.Position = _offsets[index];
        _stream.ReadExactly(bytes);
        return bytes;
    }

    EbookException TooLarge() => new($"The e-book is larger than Bibliotaph reads ({_limits.EntryBytes >> 20} MB a file, {_limits.BookBytes >> 20} MB in all once unpacked).");

    void Read()
    {
        var first = Header.Read(Record(0) ?? throw Damaged(), 0);
        var header = first;
        // A book with both versions has an older one first and its KF8 version after a boundary, which is better kept.
        if (first.Version < 8 && first.Exth(121) is { } boundary && boundary < RecordCount && Record((int)boundary) is { } kf8
            && Header.TryRead(kf8, (int)boundary) is { Version: >= 8 } joint)
        {
            header = joint;
        }
        IsKf8 = header.Version >= 8;

        Metadata = header.Metadata();
        IsProtected = first.Encryption != 0 || header.Encryption != 0;
        if (IsProtected) return;

        // Both versions share one set of resources, counted from record 0's first image and ending before the boundary.
        AddResources(first.FirstImage, header == first ? RecordCount : header.Base - 1);
        FixedLayout = header.ExthText(122) is "true";
        var text = Text(header);
        if (IsKf8) ReadKf8(header, text);
        else ReadMobi(header, text);

        CoverPath = header.Exth(201) is { } cover && cover != None && Resource(cover + 1) is { } path && IsImage(path) ? path : null;
        if (CoverPath is null && Spine.Count > 0 && HtmlText.FirstImage(ReadText(Spine[0].Path)) is { } src
            && EpubBook.Resolve(Spine[0].Path, src).Path is var image && Contains(image) && IsImage(image))
        {
            CoverPath = Find(image);
        }
    }

    bool IsImage(string path) => MediaTypeOf(path) is { } type && type.StartsWith("image/", StringComparison.Ordinal) && type != "image/svg+xml";

    /// <summary>The book's text: its text records decompressed and joined, with each record's trailing entries removed.</summary>
    byte[] Text(Header header)
    {
        if (header.TextLength > _limits.BookBytes) throw TooLarge();
        MobiCompression.Huffman? huffman = null;
        if (header.Compression == HuffCdic)
        {
            var huff = Record(header.Base + header.HuffRecord) ?? throw Damaged();
            var cdics = Enumerable.Range(1, Math.Max(0, header.HuffCount - 1)).Select(i => Record(header.Base + header.HuffRecord + i) ?? throw Damaged());
            huffman = new MobiCompression.Huffman(huff, [.. cdics], (int)Math.Min(int.MaxValue, _limits.EntryBytes));
        }
        else if (header.Compression is not (Uncompressed or PalmDocCompression)) throw new EbookException("This Kindle book's text is compressed in a way Bibliotaph doesn't read.");

        using var text = new MemoryStream();
        for (var i = 1; i <= header.TextRecords; i++)
        {
            var record = Record(header.Base + i) ?? throw Damaged();
            var data = record.AsSpan(0, record.Length - TrailingSize(record, header.ExtraFlags));
            var bytes = header.Compression switch
            {
                PalmDocCompression => MobiCompression.PalmDoc(data, (int)Math.Min(int.MaxValue, _limits.EntryBytes)),
                HuffCdic => huffman!.Unpack(data),
                _ => data.ToArray(),
            };
            _bytesRead += bytes.Length;
            if (_bytesRead > _limits.BookBytes) throw TooLarge();
            text.Write(bytes);
        }
        var all = text.ToArray();
        return all.Length > header.TextLength && header.TextLength > 0 ? all[..(int)header.TextLength] : all;
    }

    /// <summary>The bytes at the end of a text record that aren't text: indexing data and the overlap of a character cut in two.</summary>
    internal static int TrailingSize(ReadOnlySpan<byte> record, int flags)
    {
        var size = 0;
        for (var bits = flags >> 1; bits != 0; bits >>= 1)
        {
            if ((bits & 1) == 0) continue;
            var end = record.Length - size;
            int value = 0, shift = 0;
            for (var i = end - 1; i >= 0; i--)
            {
                value |= (record[i] & 0x7F) << shift;
                shift += 7;
                if ((record[i] & 0x80) != 0 || shift >= 28) break;
            }
            size += value;
            if (size > record.Length) return record.Length;
        }
        if ((flags & 1) != 0 && record.Length > size) size += (record[record.Length - size - 1] & 3) + 1;
        return Math.Min(size, record.Length);
    }

    // Resources (images, fonts) by their number, counted from 1 at the first one.
    readonly Dictionary<int, string> _resources = [];

    string? Resource(long number) => number is > 0 and < int.MaxValue ? _resources.GetValueOrDefault((int)number) : null;

    void AddResources(uint first, int end)
    {
        if (first == None || first >= RecordCount) return;
        for (var i = (int)first; i < Math.Min(end, RecordCount); i++)
        {
            var start = _offsets[i];
            var length = _offsets[i + 1] - start;
            if (length < 4) continue;
            var head = new byte[Math.Min(length, 24)];
            _stream.Position = start;
            _stream.ReadExactly(head);
            var number = i - (int)first + 1;
            var record = i;
            if (ImageType(head) is { } image)
            {
                var path = $"image{number:00000}.{image.Extension}";
                Add(path, image.MediaType, () => Record(record)!);
                _resources[number] = path;
            }
            else if (head.AsSpan().StartsWith("FONT"u8))
            {
                var path = $"font{number:00000}";
                Add(path, "font/sfnt", () => Font(Record(record)!));
                _resources[number] = path;
            }
        }
    }

    static (string Extension, string MediaType)? ImageType(ReadOnlySpan<byte> head) =>
        head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]) ? ("jpg", "image/jpeg")
        : head.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G']) ? ("png", "image/png")
        : head.StartsWith("GIF8"u8) ? ("gif", "image/gif")
        : head.StartsWith("BM"u8) && head.Length >= 6 ? ("bmp", "image/bmp")
        : null;

    /// <summary>A FONT record's font: its data after the header, unscrambled where the start is XORed with a key, then inflated.</summary>
    byte[] Font(byte[] record)
    {
        if (record.Length < 24) throw Damaged();
        var flags = BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(8));
        var start = (int)BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(12));
        var keyLength = (int)BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(16));
        var keyStart = (int)BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(20));
        if (start < 24 || start > record.Length) throw Damaged();
        var data = record[start..];
        if ((flags & 2) != 0 && keyLength > 0 && keyStart >= 0 && keyStart + keyLength <= record.Length)
        {
            for (var n = 0; n < Math.Min(1040, data.Length); n++) data[n] ^= record[keyStart + n % keyLength];
        }
        if ((flags & 1) == 0) return data;
        using var input = new System.IO.Compression.ZLibStream(new MemoryStream(data), System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            if (output.Length + read > _limits.EntryBytes) throw TooLarge();
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    static EbookException Damaged() => new("This Kindle book is damaged or incomplete.");

    // ----- Older Mobipocket books: one HTML text, links by byte position -----

    void ReadMobi(Header header, byte[] text)
    {
        var encoding = header.Encoding;
        var raw = Encoding.Latin1.GetString(text);
        var targets = new SortedSet<int>();
        foreach (Match m in FileposLink().Matches(raw))
            if (int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p > 0 && p < raw.Length) targets.Add(p);

        var ncx = header.Ncx != None ? ReadNcx(header, kf8: false) : [];
        foreach (var entry in ncx) if (entry.Position > 0 && entry.Position < raw.Length) targets.Add(entry.Position);

        // Anchors at every position something links to, outside any tag, then the text cut at its page breaks.
        var (marked, anchors) = Anchor(raw, targets, p => $"bt-pos-{p}");
        var cuts = new List<(int Start, int End)>();
        var from = 0;
        foreach (Match m in PageBreak().Matches(marked))
        {
            cuts.Add((from, m.Index));
            from = m.Index + m.Length;
        }
        cuts.Add((from, marked.Length));
        var parts = new List<(int Start, string Text)>();
        var pending = new StringBuilder();
        var pendingStart = -1;
        foreach (var (start, end) in cuts)
        {
            if (pendingStart < 0) pendingStart = start;
            pending.Append(marked, start, end - start);
            var part = pending.ToString();
            if (!HasContent(part))
            {
                if (end != marked.Length) continue;
                if (parts.Count > 0)
                {
                    parts[^1] = (parts[^1].Start, parts[^1].Text + part);
                    break;
                }
            }
            parts.Add((pendingStart, part));
            pending.Clear();
            pendingStart = -1;
        }

        string PartOf(int offset)
        {
            var i = parts.FindLastIndex(p => p.Start <= offset);
            return PartPath(Math.Max(0, i));
        }
        string Href(int position) => anchors.TryGetValue(position, out var anchor) ? $"{PartOf(anchor.At)}#{anchor.Id}" : PartPath(0);

        var spine = new List<EpubSpineItem>();
        for (var i = 0; i < parts.Count; i++)
        {
            var part = FileposAttribute().Replace(parts[i].Text, m =>
                int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? $"href=\"{Href(p)}\"" : "");
            part = RecordIndex().Replace(part, m =>
                int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && Resource(n) is { } image ? $"src=\"{image}\"" : "");
            var html = encoding.GetString(Encoding.Latin1.GetBytes(part));
            AddPart(PartPath(i), html);
            spine.Add(new EpubSpineItem(PartPath(i), Html, true, FixedLayout));
        }
        Spine = spine;

        if (ncx.Count > 0)
        {
            Contents = [.. ncx.Where(e => e.Title.Length > 0).Select(e =>
            {
                var href = e.Position > 0 ? Href(e.Position) : PartPath(0);
                var (path, fragment) = EpubBook.Resolve("", href);
                return new EpubNavPoint(e.Title, path, fragment, e.Depth);
            })];
        }
        else Contents = GuideContents(raw, marked, anchors, Href, encoding);
    }

    /// <summary>
    /// An older book without a contents index: the links on the page its guide names as the table of contents, up to
    /// the next page break.
    /// </summary>
    static List<EpubNavPoint> GuideContents(string raw, string marked, Dictionary<int, (int At, string Id)> anchors, Func<int, string> href, Encoding encoding)
    {
        if (GuideToc().Match(raw) is not { Success: true } toc
            || !int.TryParse(toc.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var position)
            || !anchors.TryGetValue(position, out var anchor)) return [];
        var start = anchor.At;
        var end = PageBreak().Match(marked, start) is { Success: true } next ? next.Index : marked.Length;
        var points = new List<EpubNavPoint>();
        foreach (Match link in TocLink().Matches(marked[start..end]))
        {
            if (points.Count >= EpubBook.MaxNavPoints) break;
            if (!int.TryParse(link.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var target)) continue;
            var title = HtmlText.Plain(encoding.GetString(Encoding.Latin1.GetBytes(link.Groups[2].Value)));
            if (title.Length == 0) continue;
            var (path, fragment) = EpubBook.Resolve("", href(target));
            points.Add(new EpubNavPoint(title, path, fragment, 0));
        }
        return points;
    }

    static bool HasContent(string part) =>
        part.Contains("<img", StringComparison.OrdinalIgnoreCase) || AnyTag().Replace(part, "").Any(c => !char.IsWhiteSpace(c));

    static string PartPath(int i) => $"part{i:0000}.html";

    void AddPart(string path, string html)
    {
        _parts[path] = html;
        Add(path, Html, () => Encoding.UTF8.GetBytes(html));
    }

    /// <summary>
    /// The text with an id at each target, and the id each target got, with where it is. A target at the start of an
    /// element names that element, by the id it has or one added to it, so a link to a heading lands on the heading;
    /// anywhere else, an empty anchor is put there, moved past the end of a tag the target falls inside.
    /// </summary>
    static (string Text, Dictionary<int, (int At, string Id)> Ids) Anchor(string text, IEnumerable<int> targets, Func<int, string> idFor)
    {
        var result = new StringBuilder(text.Length + 64);
        var ids = new Dictionary<int, (int, string)>();
        var from = 0;
        foreach (var target in targets.Order())
        {
            var position = Math.Clamp(target, 0, text.Length);
            var open = position > 0 ? text.LastIndexOf('<', position - 1) : -1;
            var close = position > 0 ? text.LastIndexOf('>', position - 1) : -1;
            if (open > close)
            {
                var end = text.IndexOf('>', position);
                position = end < 0 ? text.Length : end + 1;
            }
            if (position < from) position = from;
            result.Append(text, from, position - from);
            from = position;
            var id = idFor(target);
            if (OpeningTag().Match(text, position) is { Success: true } tag && tag.Index == position && !tag.Groups[1].Value.Equals("a", StringComparison.OrdinalIgnoreCase))
            {
                if (IdAttribute().Match(tag.Value) is { Success: true } existing) id = existing.Groups[1].Value;
                else
                {
                    var nameEnd = position + 1 + tag.Groups[1].Length;
                    result.Append(text, position, nameEnd - position).Append(" id=\"").Append(id).Append('"');
                    from = nameEnd;
                }
                ids[target] = (result.Length, id);
                continue;
            }
            ids[target] = (result.Length, id);
            result.Append("<a id=\"").Append(id).Append("\"></a>");
        }
        result.Append(text, from, text.Length - from);
        return (result.ToString(), ids);
    }

    // ----- KF8: skeletons and fragments, flows, and links by fragment and offset -----

    sealed record Piece(int InsertAt, int Length);

    void ReadKf8(Header header, byte[] raw)
    {
        var flows = Flows(header, raw);
        var text = flows.Count > 0 ? flows[0] : raw;
        var latin = Encoding.Latin1.GetString(text);
        if (header.Skeleton == None || header.Fragment == None)
        {
            // No skeleton index: the text is one file.
            ReadParts(header, [(0, latin.Length, latin)], [], flows);
            return;
        }

        var skeletons = MobiIndex.Read(i => Record(header.Base + i), (int)header.Skeleton, Encoding.UTF8);
        var fragmentIndex = MobiIndex.Read(i => Record(header.Base + i), (int)header.Fragment, Encoding.UTF8);
        var fragments = fragmentIndex.Entries.Select(e => new Piece(
            int.TryParse(e.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var at) ? at : 0,
            e.Tags.TryGetValue(6, out var span) && span.Count >= 2 ? span[1] : 0)).ToList();

        var parts = new List<(int Start, int End, string Text)>();
        var next = 0;
        foreach (var (_, tags) in skeletons.Entries)
        {
            if (!tags.TryGetValue(1, out var counts) || counts.Count < 1 || !tags.TryGetValue(6, out var span) || span.Count < 2) throw Damaged();
            var (start, length) = (span[0], span[1]);
            if (start < 0 || length < 0 || start + length > latin.Length) throw Damaged();
            var skeleton = new StringBuilder(latin, start, length, length * 4);
            var position = start + length;
            for (var k = 0; k < counts[0]; k++)
            {
                if (next >= fragments.Count) throw Damaged();
                var fragment = fragments[next++];
                if (position + fragment.Length > latin.Length) throw Damaged();
                var insert = Math.Clamp(fragment.InsertAt - start, 0, skeleton.Length);
                skeleton.Insert(insert, latin.AsSpan(position, fragment.Length));
                position += fragment.Length;
            }
            parts.Add((start, position, skeleton.ToString()));
        }
        ReadParts(header, parts, fragments, flows);
    }

    /// <summary>The text's flows, as the FDST record cuts it: the HTML first, then stylesheets and SVG.</summary>
    List<byte[]> Flows(Header header, byte[] raw)
    {
        if (header.Fdst == None || Record(header.Base + (int)header.Fdst) is not { Length: >= 12 } fdst || !fdst.AsSpan().StartsWith("FDST"u8)) return [raw];
        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(fdst.AsSpan(8));
        if (count is < 1 or > 10_000 || 12 + count * 8 > fdst.Length) return [raw];
        var flows = new List<byte[]>(count);
        for (var i = 0; i < count; i++)
        {
            var start = (int)BinaryPrimitives.ReadUInt32BigEndian(fdst.AsSpan(12 + i * 8));
            var end = (int)BinaryPrimitives.ReadUInt32BigEndian(fdst.AsSpan(16 + i * 8));
            if (start < 0 || end < start || end > raw.Length) throw Damaged();
            flows.Add(raw[start..end]);
        }
        return flows;
    }

    void ReadParts(Header header, List<(int Start, int End, string Text)> parts, List<Piece> fragments, List<byte[]> flows)
    {
        for (var i = 1; i < flows.Count; i++)
        {
            var flow = flows[i];
            var svg = flow.AsSpan().IndexOf("<svg"u8) >= 0;
            var path = $"flow{i:0000}.{(svg ? "svg" : "css")}";
            var content = Kindle(Encoding.UTF8.GetString(flow), _ => null, i);
            Add(path, svg ? "image/svg+xml" : "text/css", () => Encoding.UTF8.GetBytes(content));
        }

        var ncx = header.Ncx != None ? ReadNcx(header, kf8: true) : [];
        int? Position(int fid, int offset) => fid >= 0 && fid < fragments.Count ? fragments[fid].InsertAt + offset : null;

        // Every position a link or the contents points at, so an anchor can be placed there.
        var targets = new SortedSet<int>();
        foreach (var (_, _, text) in parts)
            foreach (Match m in KindlePos().Matches(text))
                if (Position(Base32(m.Groups[1].Value), Base32(m.Groups[2].Value)) is { } p) targets.Add(p);
        foreach (var entry in ncx)
            if (entry.Fid is { } fid && Position(fid, entry.Offset) is { } p) targets.Add(p);

        int PartIndex(int position)
        {
            var i = parts.FindLastIndex(p => p.Start <= position);
            return Math.Max(0, i);
        }
        // Ids at every target first, since a link can point into a part further on.
        var ids = new Dictionary<int, string>();
        var marked = new List<string>(parts.Count);
        for (var i = 0; i < parts.Count; i++)
        {
            var (start, end, text) = parts[i];
            var local = targets.Where(t => t >= start && (t < end || i == parts.Count - 1)).Select(t => t - start).ToList();
            var (anchored, placed) = Anchor(text, local, p => $"bt-pos-{p + start}");
            foreach (var (p, (_, id)) in placed) ids[p + start] = id;
            marked.Add(anchored);
        }
        string Href(int position) => $"{PartPath(PartIndex(position))}#{ids.GetValueOrDefault(position, $"bt-pos-{position}")}";

        var spine = new List<EpubSpineItem>();
        for (var i = 0; i < parts.Count; i++)
        {
            var html = Kindle(Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(marked[i])),
                m => Position(Base32(m.Groups[1].Value), Base32(m.Groups[2].Value)) is { } p ? Href(p) : null, -1);
            AddPart(PartPath(i), html);
            spine.Add(new EpubSpineItem(PartPath(i), Html, true, FixedLayout));
        }
        Spine = spine;

        Contents = [.. ncx.Where(e => e.Title.Length > 0).Select(e =>
        {
            var href = e.Fid is { } fid && Position(fid, e.Offset) is { } p ? Href(p) : e.Position > 0 ? Href(e.Position) : PartPath(0);
            var (path, fragment) = EpubBook.Resolve("", href);
            return new EpubNavPoint(e.Title, path, fragment, e.Depth);
        })];
    }

    /// <summary>Kindle addresses as paths of the rebuilt book: kindle:pos links, kindle:embed resources and kindle:flow files.</summary>
    string Kindle(string text, Func<Match, string?> position, int flow)
    {
        text = KindlePos().Replace(text, m => position(m) ?? "#");
        text = KindleEmbed().Replace(text, m => Resource(Base32(m.Groups[1].Value)) ?? "");
        return KindleFlow().Replace(text, m => Base32(m.Groups[1].Value) is var n && n > 0 && n != flow
            && Find($"flow{n:0000}.css") is { } css ? css : Find($"flow{n:0000}.svg") ?? "");
    }

    static int Base32(string digits)
    {
        var value = 0;
        foreach (var c in digits)
        {
            var digit = c is >= '0' and <= '9' ? c - '0' : c is >= 'A' and <= 'V' ? c - 'A' + 10 : c is >= 'a' and <= 'v' ? c - 'a' + 10 : -1;
            if (digit < 0 || value > (int.MaxValue >> 5)) return -1;
            value = value * 32 + digit;
        }
        return value;
    }

    sealed record NcxEntry(string Title, int Depth, int Position, int? Fid, int Offset);

    List<NcxEntry> ReadNcx(Header header, bool kf8)
    {
        try
        {
            var index = MobiIndex.Read(i => Record(header.Base + i), (int)header.Ncx, header.Encoding);
            var entries = new List<NcxEntry>();
            foreach (var (_, tags) in index.Entries.Take(EpubBook.MaxNavPoints))
            {
                int? Tag(int tag, int i = 0) => tags.TryGetValue(tag, out var v) && v.Count > i ? v[i] : null;
                var title = Tag(3) is { } label && index.String(label) is { } s ? HtmlText.Collapse(s) : "";
                int? fid = kf8 && Tag(6) is { } f ? f : null;
                entries.Add(new NcxEntry(title, Math.Clamp(Tag(4) ?? 0, 0, EpubBook.MaxNavDepth), Tag(1) ?? 0, fid, kf8 ? Tag(6, 1) ?? 0 : 0));
            }
            // A KF8 index lists each level in turn; by position, an entry comes after its parent and before its next sibling.
            return [.. entries.OrderBy(e => e.Position).ThenBy(e => e.Depth)];
        }
        catch (EbookException)
        {
            // The contents are a nicety: a book whose index can't be read still reads.
            return [];
        }
    }

    /// <summary>Record 0's PalmDOC and MOBI headers and the EXTH metadata after them.</summary>
    sealed class Header
    {
        readonly Dictionary<int, List<byte[]>> _exth = [];
        string _fullName = "";

        public int Base { get; private init; }
        public int Compression { get; private init; }
        public long TextLength { get; private init; }
        public int TextRecords { get; private init; }
        public int Encryption { get; private init; }
        public int Version { get; private init; }
        public Encoding Encoding { get; private init; } = Encoding.UTF8;
        /// <summary>Like every record number here, counted from <see cref="Base"/>, the header's own record.</summary>
        public uint FirstImage { get; private init; } = None;
        public int HuffRecord { get; private init; }
        public int HuffCount { get; private init; }
        public int ExtraFlags { get; private init; }
        public uint Ncx { get; private init; } = None;
        public uint Fragment { get; private init; } = None;
        public uint Skeleton { get; private init; } = None;
        public uint Fdst { get; private init; } = None;

        public static Header? TryRead(byte[] record, int @base)
        {
            try
            {
                return Read(record, @base);
            }
            catch (EbookException)
            {
                return null;
            }
        }

        public static Header Read(byte[] r, int @base)
        {
            if (r.Length < 16) throw Damaged();
            var compression = BinaryPrimitives.ReadUInt16BigEndian(r);
            var textLength = BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(4));
            var textRecords = BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(8));
            var encryption = BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(12));
            if (r.Length < 0x18 || !r.AsSpan(16, 4).SequenceEqual("MOBI"u8))
            {
                // A plain PalmDOC book: text and nothing else.
                return new Header { Base = @base, Compression = compression, TextLength = textLength, TextRecords = textRecords, Encryption = encryption };
            }
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(0x14));
            var end = Math.Min(r.Length, 16 + length);
            uint U32(int at) => at + 4 <= end ? BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(at)) : None;
            int U16(int at) => at + 2 <= end ? BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(at)) : 0;
            var codePage = U32(0x1C);
            var encoding = codePage == 1252 ? CodePagesEncodingProvider.Instance.GetEncoding(1252) ?? Encoding.Latin1 : Encoding.UTF8;
            var version = (int)U32(0x24);
            var header = new Header
            {
                Base = @base,
                Compression = compression,
                TextLength = textLength,
                TextRecords = textRecords,
                Encryption = encryption,
                Version = version < 0 ? 0 : version,
                Encoding = encoding,
                FirstImage = U32(0x6C),
                HuffRecord = (int)Math.Min(U32(0x70), int.MaxValue),
                HuffCount = (int)Math.Min(U32(0x74), 1000),
                ExtraFlags = length >= 0xE4 ? U16(0xF2) : 0,
                Ncx = U32(0xF4),
                Fragment = version >= 8 ? U32(0xF8) : None,
                Skeleton = version >= 8 ? U32(0xFC) : None,
                Fdst = version >= 8 ? U32(0xC0) : None,
            };
            var fullName = U32(0x54);
            var fullLength = U32(0x58);
            if (fullName != None && fullLength != None && fullName + fullLength <= r.Length) header._fullName = encoding.GetString(r, (int)fullName, (int)fullLength);
            if ((U32(0x80) & 0x40) != 0 && 16 + length + 12 <= r.Length && r.AsSpan(16 + length, 4).SequenceEqual("EXTH"u8))
            {
                var p = 16 + length + 12;
                var count = BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(16 + length + 8));
                for (var i = 0; i < count && p + 8 <= r.Length; i++)
                {
                    var type = (int)BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(p));
                    var size = (int)BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(p + 4));
                    if (size < 8 || p + size > r.Length) break;
                    if (!header._exth.TryGetValue(type, out var list)) header._exth[type] = list = [];
                    list.Add(r[(p + 8)..(p + size)]);
                    p += size;
                }
            }
            return header;
        }

        public uint? Exth(int type) => _exth.TryGetValue(type, out var list) && list[0].Length == 4 ? BinaryPrimitives.ReadUInt32BigEndian(list[0]) : null;

        public string? ExthText(int type) => ExthTexts(type).FirstOrDefault();

        IEnumerable<string> ExthTexts(int type) => _exth.TryGetValue(type, out var list)
            ? list.Select(b => HtmlText.Collapse(Encoding.GetString(b))).Where(s => s.Length > 0)
            : [];

        public EbookMetadata Metadata() => new()
        {
            Title = ExthText(503) ?? (HtmlText.Collapse(_fullName) is { Length: > 0 } name ? name : null),
            Authors = [.. ExthTexts(100).Distinct(StringComparer.OrdinalIgnoreCase)],
            Publisher = ExthText(101),
            Description = ExthText(103) is { } description ? HtmlText.Plain(description) : null,
            Isbn = ExthTexts(104).Select(EpubBook.IsbnOf).OfType<string>().FirstOrDefault(),
            Subjects = [.. ExthTexts(105).SelectMany(s => s.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                .Distinct(StringComparer.OrdinalIgnoreCase)],
            Date = ExthText(106) is { } date ? DatePart().Match(date) is { Success: true } day ? day.Value : date : null,
            Language = ExthText(524),
        };
    }

    [GeneratedRegex(@"<[^<>]+filepos\s*=\s*['""]?0*(\d+)[^<>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex FileposLink();

    [GeneratedRegex(@"\bfilepos\s*=\s*['""]?0*(\d+)['""]?", RegexOptions.IgnoreCase)]
    private static partial Regex FileposAttribute();

    [GeneratedRegex(@"\b(?:hi|lo)?recindex\s*=\s*['""]?0*(\d+)['""]?", RegexOptions.IgnoreCase)]
    private static partial Regex RecordIndex();

    [GeneratedRegex(@"<mbp:pagebreak[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex PageBreak();

    [GeneratedRegex(@"<reference[^>]*type\s*=\s*['""]?toc['""]?[^>]*filepos\s*=\s*['""]?0*(\d+)|<reference[^>]*filepos\s*=\s*['""]?0*(\d+)[^>]*type\s*=\s*['""]?toc", RegexOptions.IgnoreCase)]
    private static partial Regex GuideToc();

    [GeneratedRegex(@"<a[^>]*filepos\s*=\s*['""]?0*(\d+)[^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TocLink();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"\G<([A-Za-z][A-Za-z0-9:\-]*)\b[^<>]*>")]
    private static partial Regex OpeningTag();

    [GeneratedRegex(@"\sid\s*=\s*[""']([^""'<>]*)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex IdAttribute();

    [GeneratedRegex(@"kindle:pos:fid:([0-9A-Va-v]{4}):off:([0-9A-Va-v]{10})")]
    private static partial Regex KindlePos();

    [GeneratedRegex(@"kindle:embed:([0-9A-Va-v]{4})(?:\?mime=[A-Za-z0-9/+.\-]*)?")]
    private static partial Regex KindleEmbed();

    [GeneratedRegex(@"kindle:flow:([0-9A-Va-v]{4})(?:\?mime=[A-Za-z0-9/+.\-]*)?")]
    private static partial Regex KindleFlow();

    [GeneratedRegex(@"^\d{4}(?:-\d{2}(?:-\d{2})?)?")]
    private static partial Regex DatePart();
}
