namespace Bibliotaph.Api.Contracts;

/// <summary>
/// A connected PC asking the web library about a book's ISBNs (13 digits each), so it asks where the server can, with
/// the book's title for a catalogue searched only by name.
/// </summary>
public sealed record PcLookupRequest(IReadOnlyList<string> Isbns, IReadOnlyList<string>? Titles = null);

/// <summary>
/// The server's answer: where it asks (<see cref="Catalogues"/>, "RPGGeek or Open Library or Google Books"), and the
/// book's record in each that knows it, most preferred first (<see cref="Records"/>), with the ones that answered
/// (<see cref="AskedOf"/>). <see cref="Record"/> is the first of them, for an app from before there were several.
/// <see cref="Problem"/> says why it couldn't ask just now.
/// </summary>
public sealed record PcLookupAnswer(string Catalogues, PcBookRecord? Record = null, string? Problem = null,
    IReadOnlyList<PcBookRecord>? Records = null, string? AskedOf = null);

/// <summary>A book's record in an online catalogue, as the server found it.</summary>
public sealed record PcBookRecord(
    string Source, string Key, string Isbn, string Title, IReadOnlyList<string> Authors, IReadOnlyList<string> Publishers, string? Published);
