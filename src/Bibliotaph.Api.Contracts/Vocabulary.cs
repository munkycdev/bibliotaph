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
/// Settings › Vocabulary's choice for a vocabulary the AI can add to: a new term the AI uses joins it at once (Add
/// them) or waits in Needs review (Ask first).
/// </summary>
public sealed record AiTermChoice(bool Add);

/// <summary>The choice as saved, and how many terms waiting in Needs review choosing Add them added.</summary>
public sealed record AiTermChange(bool Add, int Added);

/// <summary>What both clients say about the choice.</summary>
public static class AiTermText
{
    public const string Title = "New names from the AI";

    public const string Help = "Ask first: a name the AI uses that isn't in this list waits in Needs review. "
        + "Add them: it joins the list at once, and the books that use it get it as a suggestion. Either way, you can rename or merge it here later.";

    /// <summary>The note after choosing Add them: what was waiting is in.</summary>
    public static string Added(int count) => count switch
    {
        0 => "Nothing was waiting in Needs review.",
        1 => "Added the 1 term that was waiting in Needs review.",
        _ => $"Added the {count} terms that were waiting in Needs review.",
    };
}

/// <summary>
/// What a vocabulary edit did: the term it changed, or why it was refused, and anything else it changed ("“one shot”
/// was another name for Adventure; now it isn't.").
/// </summary>
public sealed record VocabularyChange(long? TermId, string? Problem, string? Note);

/// <summary>
/// Find overlaps, GET /api/vocabulary/{key}/overlaps: the terms that look like the same thing, in groups, and the
/// pairs the user kept apart.
/// </summary>
public sealed record TermOverlapPage(IReadOnlyList<TermOverlapGroup> Proposals, IReadOnlyList<KeptApartTerms> KeptApart);

/// <summary>Terms that look like the same thing: each with how many books use it, why, whether the names alone make it likely, and which to keep by default.</summary>
public sealed record TermOverlapGroup(IReadOnlyList<TermOverlapMember> Terms, string Reason, bool Sure, long KeepId);

public sealed record TermOverlapMember(long Id, string Label, int Books);

/// <summary>A pair the user said are not the same thing, by their labels.</summary>
public sealed record KeptApartTerms(long Id, string First, string Second);

/// <summary>Combine: the term that stays and the terms folded into it.</summary>
public sealed record CombineTerms(long KeepId, IReadOnlyList<long> MergeIds);

/// <summary>Keep apart: the terms of a proposal, every pair of which is remembered.</summary>
public sealed record KeepTermsApart(IReadOnlyList<long> Ids);

/// <summary>What both clients say about overlaps.</summary>
public static class OverlapText
{
    public const string Title = "Find overlaps";

    public const string Help = "Looks for terms in this list that may be the same thing under two names, and offers to combine them.";

    public const string None = "No overlaps found.";

    public const string NoneLeft = "No overlaps left.";

    public const string Comparing = "Comparing names…";

    public const string Keep = "Keep";

    public const string Combine = "Combine";

    public const string CombineHelp = "Folds the other terms into the one kept: their names become its other names, and the books filed under them move to it.";

    public const string KeepApart = "Keep apart";

    public const string KeepApartHelp = "They're not the same thing. Find overlaps leaves this pair alone from now on.";

    public const string Forget = "Offer this pair again";

    public const string NamesOnly = "By names only. With AI on, it also asks the model what each name refers to.";

    public const string UndoFailed = "The terms couldn't be put back.";

    public static string Found(int groups) => groups == 1 ? "1 group of terms may be the same thing." : $"{groups:N0} groups of terms may be the same thing.";

    public static string Books(int count) => count == 1 ? "1 book" : $"{count:N0} books";

    public static string KeptApart(int pairs) => pairs == 1 ? "1 pair kept apart" : $"{pairs:N0} pairs kept apart";

    /// <summary>"Kept Greyhawk and Grayhawk apart."</summary>
    public static string KeptApartNote(IReadOnlyList<string> labels) => labels.Count switch
    {
        0 => "Nothing to keep apart.",
        1 => $"Kept {labels[0]} apart.",
        2 => $"Kept {labels[0]} and {labels[1]} apart.",
        _ => $"Kept {string.Join(", ", labels.Take(labels.Count - 1))} and {labels[^1]} apart.",
    };

    /// <summary>"Forgotten Realms · 12 books", as the Keep list shows a term.</summary>
    public static string Member(string label, int books) => books == 0 ? label : $"{label} · {Books(books)}";
}

/// <summary>One of the user's own tags: the words that find it in books' pages, and how many books show it.</summary>
public sealed record UserTag(long Id, string Name, IReadOnlyList<string> Words, int Books);

/// <summary>A tag's name and its words, separated by commas, as typed.</summary>
public sealed record UserTagText(string Name, string Words);

/// <summary>The tag as saved, with the books that show it now, or why nothing was saved.</summary>
public sealed record UserTagChange(UserTag? Tag, string? Problem);
