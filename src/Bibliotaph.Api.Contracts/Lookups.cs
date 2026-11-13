namespace Bibliotaph.Api.Contracts;

/// <summary>
/// A connected PC asking the web library about a book's ISBNs (13 digits each) and the product codes it prints
/// (capitals and digits, "PZO1110"), so it asks where the server can, with the book's title for a catalogue searched
/// only by name. <see cref="Isbns"/> is empty for a book that prints only a code, and both are for one that prints
/// neither, looked up by its title alone. <see cref="Lines"/> name the line it belongs to where the book prints the
/// name, a broader search for a catalogue that takes records only by ISBN or code (none from an app before it did).
/// <see cref="Families"/> are RPGGeek's ids of the games the books beside it belong to, whose other books it may be.
/// </summary>
public sealed record PcLookupRequest(IReadOnlyList<string> Isbns, IReadOnlyList<string>? Titles = null, IReadOnlyList<string>? ProductCodes = null,
    IReadOnlyList<string>? Lines = null, IReadOnlyList<string>? Families = null);

/// <summary>
/// The server's answer: where it asks (<see cref="Catalogues"/>, "RPGGeek or Open Library or Google Books"), and the
/// book's record in each that knows it, most preferred first (<see cref="Records"/>), with the ones that answered
/// (<see cref="AskedOf"/>). <see cref="Record"/> is the first of them, for an app from before there were several.
/// <see cref="Problem"/> says why it couldn't ask just now. <see cref="Candidates"/> are the items its searches found
/// that may be the book, by name, for the PC to check against what the book prints, which the server never sees.
/// </summary>
public sealed record PcLookupAnswer(string Catalogues, PcBookRecord? Record = null, string? Problem = null,
    IReadOnlyList<PcBookRecord>? Records = null, string? AskedOf = null, IReadOnlyList<PcCatalogueItem>? Candidates = null);

/// <summary>An item a catalogue's search found: its id there and what the catalogue calls it.</summary>
public sealed record PcCatalogueItem(string Source, string Key, string Name);

/// <summary>
/// A book's record in an online catalogue, as the server found it, with what a game catalogue files it under
/// (<see cref="Values"/>, none from a server before it did) and its page count where the catalogue gives one.
/// <see cref="Isbn"/> is empty for a record found by title alone.
/// </summary>
public sealed record PcBookRecord(
    string Source, string Key, string Isbn, string Title, IReadOnlyList<string> Authors, IReadOnlyList<string> Publishers, string? Published,
    IReadOnlyList<PcCatalogueValue>? Values = null, int? Pages = null, IReadOnlyList<string>? Families = null);

/// <summary>Something a catalogue files a book under, in its words, by the key of the field it fills ("system", "theme").</summary>
public sealed record PcCatalogueValue(string Field, string Value);
