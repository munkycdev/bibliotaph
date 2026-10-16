namespace Bibliotaph.Ebooks;

/// <summary>
/// An e-book as the rest of Bibliotaph reads it, whatever its format: its metadata, a reading order of HTML chapter
/// files, its contents and page list, and the files those chapters use, each by a path. An EPUB's paths are its
/// archive's; a Kindle book's are made up for the parts Bibliotaph rebuilds from it.
/// </summary>
public interface IEbook : IDisposable
{
    EbookMetadata Metadata { get; }

    IReadOnlyList<EpubSpineItem> Spine { get; }

    IReadOnlyList<EpubNavPoint> Contents { get; }

    /// <summary>Where each printed page of the book it was made from begins, when the book says.</summary>
    IReadOnlyList<EpubNavPoint> PageList { get; }

    /// <summary>The cover image's path, when the book has one.</summary>
    string? CoverPath { get; }

    /// <summary>Whether every page is drawn to a fixed size (comics, some art books), rather than flowing.</summary>
    bool FixedLayout { get; }

    /// <summary>Whether the book's text is encrypted (DRM). Its metadata is still readable.</summary>
    bool IsProtected { get; }

    /// <summary>Whether the book holds <paramref name="path"/>.</summary>
    bool Contains(string path);

    /// <summary>The media type of <paramref name="path"/>, or null when the book doesn't say.</summary>
    string? MediaTypeOf(string path);

    /// <summary>Whether <paramref name="path"/> is encrypted, so its bytes are not the file itself.</summary>
    bool IsEncrypted(string path);

    /// <summary>A file's bytes, within the limits.</summary>
    byte[] ReadBytes(string path);

    /// <summary>A file as text.</summary>
    string ReadText(string path);

    /// <summary>The cover image's bytes, or null when there is none.</summary>
    byte[]? ReadCover();
}

/// <summary>Opens an e-book by what its bytes are, not by its name: an .azw can be a Mobipocket book or a KFX one.</summary>
public static class Ebook
{
    /// <summary>Opens an EPUB or a Mobipocket or Kindle (KF8) book. The stream must be seekable and stay open until the book is disposed.</summary>
    public static IEbook Open(Stream stream, EbookLimits? limits = null)
    {
        Span<byte> head = stackalloc byte[68];
        stream.Position = 0;
        var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        stream.Position = 0;
        var start = head[..read];
        if (start.StartsWith("PK\u0003\u0004"u8)) return EpubBook.Open(stream, limits);
        if (read == head.Length && head[60..68].SequenceEqual("BOOKMOBI"u8)) return MobiBook.Open(stream, limits);
        if (start.StartsWith(KfxDrm) || start.StartsWith("CONT"u8)) throw new EbookException("This is a Kindle KFX book, which Bibliotaph can't read.");
        if (start.StartsWith("TPZ"u8)) throw new EbookException("This is a Kindle Topaz book, which Bibliotaph can't read.");
        throw new EbookException("This file isn't an e-book Bibliotaph can read; it may be damaged or incomplete.");
    }

    /// <summary>A KFX book in Amazon's DRM wrapper; one without it starts "CONT".</summary>
    static ReadOnlySpan<byte> KfxDrm => [0xEA, (byte)'D', (byte)'R', (byte)'M', (byte)'I', (byte)'O', (byte)'N', 0xEE];
}
