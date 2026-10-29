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

/// <summary>One of the user's own tags: the words that find it in books' pages, and how many books show it.</summary>
public sealed record UserTag(long Id, string Name, IReadOnlyList<string> Words, int Books);

/// <summary>A tag's name and its words, separated by commas, as typed.</summary>
public sealed record UserTagText(string Name, string Words);

/// <summary>The tag as saved, with the books that show it now, or why nothing was saved.</summary>
public sealed record UserTagChange(UserTag? Tag, string? Problem);
