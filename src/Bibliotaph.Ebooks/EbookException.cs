namespace Bibliotaph.Ebooks;

/// <summary>
/// An e-book that can't be read: not a valid package, damaged, or larger than the limits allow. The message says why in
/// words a reader understands, and never quotes the book.
/// </summary>
public sealed class EbookException : Exception
{
    public EbookException(string message) : base(message) { }

    public EbookException(string message, Exception inner) : base(message, inner) { }

    public EbookException() { }
}

/// <summary>
/// How much of an e-book Bibliotaph will decompress. An EPUB is a ZIP whose entries say their own sizes, so a crafted
/// one could claim little and expand to gigabytes; the counts here are of bytes actually read, not of what the
/// archive claims.
/// </summary>
public sealed record EbookLimits
{
    public long EntryBytes { get; init; } = 64L << 20;

    public long BookBytes { get; init; } = 512L << 20;

    public int Entries { get; init; } = 20_000;

    public static EbookLimits Default { get; } = new();
}
