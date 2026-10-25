namespace Bibliotaph.Api.Contracts;

/// <summary>A field's value as typed, a list comma-separated; or one value, stored or normalized, to keep, use or reject.</summary>
public sealed record FieldEdit(string Text);

/// <summary>
/// After a change to a book's details: the details as they are now, or the problem with what was typed; and, after a
/// correction, the offer to correct the books from the same folder that showed what this one did. <see cref="Message"/>
/// says what was done when the drawer shows it in words, as adding an adventure does.
/// </summary>
public sealed record EditResult(BookDetails? Details, string? Problem = null, CorrectionOffer? Offer = null, string? Message = null);

/// <summary>"Change 14 other books from “Fey Court” too?": <see cref="Token"/> is for POST /api/library/held/{token}.</summary>
public sealed record CorrectionOffer(string Token, string Question, string Detail);

/// <summary>
/// What a change did, in words, with the token that undoes it when it can be undone, and the book to show next when
/// it isn't this one any more.
/// </summary>
public sealed record ActionResult(string Message, string? Undo = null, long? Show = null);

public sealed record SetFavorite(bool Favorite);

/// <summary>The Notes tab: the book's own note, and the notes on its pages.</summary>
public sealed record BookNotes(string Text, IReadOnlyList<PageNoteLine> PageNotes, string TabLabel);

/// <summary>A note on pages: "p. 42–44", its text, and why it may not open there now.</summary>
public sealed record PageNoteLine(string Pages, string Text, string? Reason, bool Changed);

/// <summary>Pages of a file as an adventure: its title when new, and its first and last pages as the file numbers them.</summary>
public sealed record PartEdit(string? Title, string From, string To);

/// <summary>Cataloged wrong… closed: whether anything was changed or kept, which decides if Needs review keeps a card.</summary>
public sealed record CorrectionEnd(bool Decided);

/// <summary>How typed text will be filed in a term field: "“Campaign” is another name for Adventure".</summary>
public sealed record Resolution(string Text);

/// <summary>A pack's cover: one of its files, or null for the mosaic of them.</summary>
public sealed record PackCover(long? Member);
