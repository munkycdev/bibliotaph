namespace Bibliotaph.Api.Contracts;

/// <summary>"Add a book I own elsewhere": the dialog's words, and the game systems, types and places it offers.</summary>
public sealed record ElsewhereChoices(string Eyebrow, string Heading, string Intro, IReadOnlyList<string> Systems, IReadOnlyList<string> Types,
    IReadOnlyList<string> Owns, string OtherHint, string OtherTip);

/// <summary>
/// The book to add: its title, and what was picked or typed. <see cref="Other"/> is somewhere not in the list, or
/// the book's link online. <see cref="Cover"/> is the token of a cover read when it was picked.
/// </summary>
public sealed record ElsewhereAdd(string Title, string? System, string? Type, string? Publisher, IReadOnlyList<string> Owns, string Other, string? Cover);

/// <summary>The new card, or why it couldn't be made, such as a year that isn't one.</summary>
public sealed record ElsewhereAdded(long? EntryId, string? Problem);

/// <summary>A picture read as a cover: its token until the book is added, or why it can't be one.</summary>
public sealed record ChosenCoverRead(string? Cover, string? Problem);
