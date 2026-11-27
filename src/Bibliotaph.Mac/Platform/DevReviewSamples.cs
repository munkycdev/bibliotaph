using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Presentation.ViewModels;
using Bibliotaph.Processing;

namespace Bibliotaph.Mac.Platform;

/// <summary>
/// One made-up card of each kind, for --review-samples, so every Needs review layout can be looked at without a library
/// that happens to raise them all. Its answers aren't meant to be clicked: the ids are made up.
/// </summary>
static class DevReviewSamples
{
    public static IEnumerable<DecidedCardViewModel> Make(IReviewActions actions)
    {
        var system = new Term("system", "sample-system", "Sample System");
        var vocabulary = new Vocabulary([system, new Term("system", "other-system", "Other System"), new Term("setting", "sample-world", "Sample World")]);
        var now = DateTime.UtcNow;
        var one = new EntryId(1);
        var two = new EntryId(2);

        yield return new TermCardViewModel(new PendingTerm(1, new Term("system", "new-system", "New System"), MetadataFields.System, 3, "…for New System, second edition…"), vocabulary, actions);
        yield return new OverlapCardViewModel(new OverlapProposal("o", "setting",
            [new OverlapMember(1, "sample-world", "Sample World", 12), new OverlapMember(2, "the-sample-world", "The Sample World", 2)], "Names differ only by “The”", false, 1), actions);
        yield return new VersionCardViewModel(new VersionItem(new PendingVersion(1, 1, one, 2, two, VersionEvidence.SharedPages, 180, 200, 210, 200, now),
            "Sample Adventure v2", "Adventures/Sample Adventure v2.pdf", "Sample Adventure", "Adventures/Sample Adventure.pdf"), actions);
        var changed = new PlacedPages(PlaceKind.SessionItem, 1, one, new PageRange(1, 12, 12, "10", "10"), "Session one: the gate");
        var found = new PlacedPages(PlaceKind.PageNote, 2, one, new PageRange(1, 30, 30, "28", "28"), "The innkeeper's secret");
        yield return new RevisionCardViewModel(new RevisionItem(one, 1, "Sample Adventure", "Adventures/Sample Adventure v2.pdf",
            [new RevisionPlace(changed, new PageRefCheck(PageCheck.NeedsLook, 1, 13, 13), "11"), new RevisionPlace(found, new PageRefCheck(PageCheck.Found, 1, 31, 31), "29")]), actions);
        yield return new ElsewhereCardViewModel(new ElsewhereItem(new PendingElsewhere(1, one, 1, two, now), "Sample Core Rules", null, "Sample Core Rules", "Rules/Sample Core Rules.pdf"), actions);
        yield return new LookupMatchCardViewModel(new LookupMatchItem(new PendingLookup([1], ["h"], one, 1,
            new BookRecord("Open Library", "k", "9780000000000", "Sample Core Rules", ["A. Writer"], ["Sample Press"], "2020"), now), "Sample Core Rules", "Rules/Sample Core Rules.pdf"), actions);
        yield return new SourceBookCardViewModel(new SourceBookProposal([new EntryId(5), new EntryId(6)], ["Gate map", "Cellar map"], "Adventures/Maps",
            [new SourceBookChoice(one, "Sample Adventure", "Same folder · names the gate"), new SourceBookChoice(two, "Sample Sequel", "Same folder")]), actions);
        yield return new CorrectionCardViewModel(new CorrectionItem(one, 1, "Sample Bestiary", now), actions);
        yield return new FolderRuleCardViewModel(new FolderRuleInfo(1, new FolderKey(1, "Sample System"), "Library", "system", "sample-system", FolderRuleState.Proposed, 6, now), vocabulary, actions);
        yield return new FolderCollectionCardViewModel(new FolderCollectionProposal(new FolderKey(1, "Sample Campaign"), "Library", [one, two]), actions);
        yield return new CollectionSuggestionCardViewModel(new CollectionSuggestion(1, "Sample Campaign",
            [new SuggestedBook(one, "Sample Adventure", 1), new SuggestedBook(two, "Sample Sequel", 2)], Setting: "sample-world"), vocabulary, actions);
        string?[] labels = [.. Enumerable.Range(1, 120).Select(p => (string?)p.ToString(System.Globalization.CultureInfo.InvariantCulture))];
        yield return new PartProposalCardViewModel(new PartProposalItem(one, "Sample Anthology", 1, PartProposalSource.Rules,
            [new ProposedPart("The First Gate", new PartPages(4, 22), "Levels 1–3"), new ProposedPart("Under the Hill", new PartPages(23, 48), "Levels 3–5")]), labels, actions);
        yield return new UnplacedPartCardViewModel(new UnplacedPart(new EntryId(7), one, "The First Gate", "Sample Anthology", 1, new PartPages(4, 22)), labels, actions);
        yield return new PackCardViewModel(new PackProposal(new EntryId(8), new PackPlace("Library", "Handouts", false, PackAnswer.Packed), "Handouts", [1, 2, 3], false), [], actions);
    }
}
