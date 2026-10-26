namespace Bibliotaph.Api.Contracts;

/// <summary>
/// A term in Settings › Vocabulary, as the Windows app lists it: its name, the short one shown on covers, whether the
/// user added it, and its other names.
/// </summary>
public sealed record VocabularyTerm(long Id, string Label, string? ShortLabel, bool Yours, IReadOnlyList<VocabularyAlias> Aliases);

public sealed record VocabularyAlias(long Id, string Text);

public sealed record NewTerm(string Label);

public sealed record TermRename(string Label, string? ShortLabel);

public sealed record NewAlias(string Text);

/// <summary>
/// What a vocabulary edit did: the term it changed, or why it was refused, and anything else it changed ("“one shot”
/// was another name for Adventure; now it isn't.").
/// </summary>
public sealed record VocabularyChange(long? TermId, string? Problem, string? Note);

/// <summary>One of the user's own tags: the words that find it in books' pages, and how many books show it.</summary>
public sealed record UserTag(long Id, string Name, IReadOnlyList<string> Words, int Books);

/// <summary>A tag's name and its words, separated by commas, as typed.</summary>
public sealed record UserTagText(string Name, string Words);

/// <summary>The tag as saved, with the books that show it now, or why nothing was saved.</summary>
public sealed record UserTagChange(UserTag? Tag, string? Problem);
