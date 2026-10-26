namespace Bibliotaph.Api.Contracts;

/// <summary>
/// Ask this book, as the reader opens it: whether the book can be asked (<see cref="Unavailable"/> says why not), and
/// its conversation so far, oldest first.
/// </summary>
public sealed record AskBook(bool On, string? Unavailable, IReadOnlyList<AskTurn> History);

/// <summary>An earlier turn: the question, what it looked for when the model rewrote it, and the answer as checked.</summary>
public sealed record AskTurn(string Question, string? LookedFor, IReadOnlyList<AskPart> Answer);

/// <summary>How a piece of an answer shows, as the Windows reader shows it.</summary>
public enum AskPartKind
{
    /// <summary>The answer's own words.</summary>
    Text,

    /// <summary>A quotation that is on the page it cites, in its marks.</summary>
    Quote,

    /// <summary>A quotation that isn't on the page: in italics, without its marks.</summary>
    Paraphrase,

    /// <summary>A [p. N]: a link to that page.</summary>
    Citation,

    /// <summary>The muted note after a sentence whose citations were all taken out.</summary>
    Unsupported,
}

/// <summary>
/// A piece of an answer; a citation has the page it opens and the words to mark there, and in an answer from the
/// library, the book: its document, and the card the reader opens it under.
/// </summary>
public sealed record AskPart(AskPartKind Kind, string Text, int? Page = null, string? Phrase = null, long? DocumentId = null, long? EntryId = null);

public sealed record AskQuestion(string Question);

/// <summary>
/// A page that looks like it answers the question: "p. 112 · The Mill", a few of its lines, and how it was found. In
/// an answer from the library, "The Sunken Keep · p. 112", with the book it opens and what a screen reader says for it.
/// </summary>
public sealed record AskPassageLine(int Page, string Link, string Text, string Found, string? Phrase, long? DocumentId = null, long? EntryId = null,
    string? Open = null);

/// <summary>
/// One line of the stream a question is answered in: each says what has changed since the last. The panel shows
/// <see cref="Step"/> while it is set, the answer as it is written, and the pages and notes once they are found.
/// <see cref="Done"/> ends the stream; <see cref="Turn"/> is the question and answer as they joined the book's conversation.
/// </summary>
public sealed record AskUpdate
{
    public string? Step { get; init; }
    public string? LookedFor { get; init; }
    public IReadOnlyList<AskPart>? Answer { get; init; }
    public IReadOnlyList<AskPassageLine>? Passages { get; init; }
    public string? PassagesHeading { get; init; }
    public string? Message { get; init; }
    public string? Coverage { get; init; }
    public string? Meaning { get; init; }

    /// <summary>The model is writing: Stop is offered.</summary>
    public bool Answering { get; init; }

    public bool Done { get; init; }
    public AskTurn? Turn { get; init; }
}

/// <summary>Whether Ask in the top bar can read a request or answer a question: AI is set up and on.</summary>
public sealed record AskStatus(bool Ready);

/// <summary>A question to the books the Library shows: its filters and the search box's fields in <see cref="Scope"/>.</summary>
public sealed record AskLibraryQuestion(string Question, LibraryQuery Scope);

/// <summary>
/// A request read as a search: the chips, each a field and its value or words to find, and notes on anything changed
/// or left out. Or why there is no reading: <see cref="NeedsAi"/> when AI is off.
/// </summary>
public sealed record AskReading(IReadOnlyList<AskChip> Parts, IReadOnlyList<string> Notes, string? Problem, bool NeedsAi = false);

/// <summary>One chip: the search field's name ("system"), or null for words to find, its value, and whether it leaves the value out.</summary>
public sealed record AskChip(string? Field, string Value, bool Exclude);
