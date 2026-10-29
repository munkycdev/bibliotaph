namespace Bibliotaph.Core.Metadata;

/// <summary>
/// The vocabularies the AI can propose a new term for: every term field's but editions, which are too easy to get
/// wrong ("5th printing", "Revised") and so never the AI's to add. Settings › Vocabulary offers each a choice: a new
/// name waits in Needs review (Ask first) or joins the vocabulary as the AI uses it (Add them).
/// </summary>
public static class AiTerms
{
    public static IReadOnlyList<string> Extensible { get; } =
        [.. MetadataFields.All.Where(f => f.Kind == FieldKind.Term && f != MetadataFields.Edition).Select(f => f.Vocabulary!).Distinct()];

    public static bool Extends(string vocabulary) => Extensible.Contains(vocabulary, StringComparer.Ordinal);
}
