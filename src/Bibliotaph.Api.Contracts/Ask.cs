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

/// <summary>A piece of an answer; a citation has the page it opens and the words to mark there.</summary>
public sealed record AskPart(AskPartKind Kind, string Text, int? Page = null, string? Phrase = null);

public sealed record AskQuestion(string Question);

/// <summary>A page that looks like it answers the question: "p. 112 · The Mill", a few of its lines, and how it was found.</summary>
public sealed record AskPassageLine(int Page, string Link, string Text, string Found, string? Phrase);

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
