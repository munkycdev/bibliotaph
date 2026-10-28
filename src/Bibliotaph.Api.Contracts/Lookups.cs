namespace Bibliotaph.Api.Contracts;

/// <summary>A connected PC asking the web library about a book's ISBNs (13 digits each), so it asks where the server can.</summary>
public sealed record PcLookupRequest(IReadOnlyList<string> Isbns);

/// <summary>
/// The server's answer: where it asked (<see cref="Catalogues"/>, "Open Library or Google Books"), and the record of
/// the first ISBN one of them knows, or null when none did. <see cref="Problem"/> says why it couldn't ask just now.
/// </summary>
public sealed record PcLookupAnswer(string Catalogues, PcBookRecord? Record = null, string? Problem = null);

/// <summary>A book's record in an online catalogue, as the server found it.</summary>
public sealed record PcBookRecord(
    string Source, string Key, string Isbn, string Title, IReadOnlyList<string> Authors, IReadOnlyList<string> Publishers, string? Published);
