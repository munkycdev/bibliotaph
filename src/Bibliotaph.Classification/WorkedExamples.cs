using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Classification;

/// <summary>
/// One of the user's books that could be a worked example: the folders its files are in, whether any of them is in a
/// library folder kept from AI, and its metadata.
/// </summary>
public sealed record ExampleBook(EntryId EntryId, IReadOnlyCollection<FolderKey> Folders, bool KeptFromAi, EffectiveMetadata Metadata);

/// <summary>The book about to be classified, as far as choosing examples for it goes: where it is, and its game system and publisher so far.</summary>
public sealed record ExampleTarget(EntryId EntryId, IReadOnlyCollection<FolderKey> Folders, string? System = null, string? Publisher = null);

/// <summary>
/// Worked examples (scenario review, row 8): a few of the user's own books sent with each classification, so the model
/// follows the user's conventions without any training. Only values the user confirmed are sent, as short
/// "field: value" lines with no text from any book, and never a book with a file in a library folder kept from AI.
/// Books are chosen by how alike they are: from the same folder first, then for the same game system, then from the
/// same publisher; a book like none of these isn't sent, since an unrelated book teaches the model nothing about this one.
/// </summary>
public static class WorkedExamples
{
    const int SameFolder = 3;
    const int SameSystem = 2;
    const int SamePublisher = 1;

    /// <summary>
    /// The examples for <paramref name="target"/>: the most alike books first, and among equals those the user decided
    /// most recently. At most <paramref name="max"/>.
    /// </summary>
    public static IReadOnlyList<WorkedExample> Choose(ExampleTarget target, IEnumerable<ExampleBook> books, Vocabulary vocabulary,
        int max = ClassifierPrompt.MaxExamples) =>
        [.. books
            .Where(b => b.EntryId != target.EntryId && !b.KeptFromAi)
            .Select(b => (Book: b, Score: Score(target, b), Example: Describe(b.Metadata, vocabulary)))
            .Where(c => c.Score > 0 && c.Example is not null)
            .OrderByDescending(c => c.Score).ThenByDescending(c => Decided(c.Book.Metadata)).ThenByDescending(c => c.Book.EntryId.Value)
            .Take(max)
            .Select(c => c.Example!)];

    /// <summary>How alike a book is to the one being classified: 3 from the same folder, 2 for the same system, 1 from the same publisher, else 0.</summary>
    public static int Score(ExampleTarget target, ExampleBook book)
    {
        if (book.Folders.Any(target.Folders.Contains)) return SameFolder;
        if (target.System is { } system && book.Metadata[MetadataFields.System].First?.Value == system) return SameSystem;
        if (target.Publisher is { } publisher && book.Metadata[MetadataFields.Publisher].First is { } theirs
            && MetadataText.Normalize(theirs.Value) == MetadataText.Normalize(publisher)) return SamePublisher;
        return 0;
    }

    /// <summary>
    /// A book as an example: the values the user confirmed in the fields the classifier fills, as the model would
    /// answer them (a term's listed name, "1-5", text as written). Null when the user has confirmed nothing there but
    /// the title, which alone shows no convention.
    /// </summary>
    public static WorkedExample? Describe(EffectiveMetadata metadata, Vocabulary vocabulary)
    {
        var values = new List<(string Field, string Value)>();
        foreach (var field in ClassifierPrompt.Fields)
            foreach (var value in metadata[field.Field].Values.Where(v => v.Confirmed).Take(field.MaxValues))
                values.Add((field.Key, field.Field.Kind == FieldKind.Term ? vocabulary.Label(field.Field.Vocabulary!, value.Value) : value.Value));
        return values.Any(v => v.Field != MetadataFields.Title.Key) ? new WorkedExample(values) : null;
    }

    /// <summary>When the user last decided any of the fields an example shows.</summary>
    static DateTime Decided(EffectiveMetadata metadata) =>
        ClassifierPrompt.Fields.SelectMany(f => metadata[f.Field].Values.Where(v => v.Confirmed)).Select(v => v.Source.Settled).DefaultIfEmpty().Max();
}
