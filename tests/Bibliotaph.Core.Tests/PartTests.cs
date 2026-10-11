using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Core.Tests;

/// <summary>The rules for adventures inside a file (slice 5): their pages, and what they take from the file.</summary>
public class PartTests
{
    static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Pages_are_described_by_their_printed_numbers()
    {
        string?[] labels = ["Cover", "i", "1", "2", "3", null];
        Assert.Equal("pp. 1–3", new PartPages(2, 4).Describe(labels));
        Assert.Equal("p. i", new PartPages(1, 1).Describe(labels));
        Assert.Equal("pp. 3–6", new PartPages(4, 5).Describe(labels[..5]));
        Assert.Equal("pp. 34–52", new PartPages(40, 58).Describe("34", "52"));
        Assert.Equal("pp. 41–59", new PartPages(40, 58).Describe(null, null));
        Assert.Equal(19, new PartPages(40, 58).Count);
    }

    [Fact]
    public void An_adventure_has_one_run_of_pages_in_the_file_shared_with_no_other()
    {
        (string, PartPages)[] others = [("Warehouse heist", new PartPages(10, 19)), ("Sewer chase", new PartPages(30, 39))];
        Assert.Null(PartRanges.Check(new PartPages(20, 29), 50, others));
        Assert.Null(PartRanges.Check(new PartPages(0, 0), 50, others));
        Assert.Equal("The last page comes before the first.", PartRanges.Check(new PartPages(25, 24), 50, others));
        Assert.Equal("Those pages aren't all in this file.", PartRanges.Check(new PartPages(45, 50), 50, others));
        Assert.Equal("Those pages aren't all in this file.", PartRanges.Check(new PartPages(-1, 3), 50, others));
        Assert.Equal("Those pages overlap “Warehouse heist”. Adventures in one file can't share pages.", PartRanges.Check(new PartPages(19, 25), 50, others));
        Assert.Contains("Sewer chase", PartRanges.Check(new PartPages(25, 45), 50, others), StringComparison.Ordinal);
    }

    [Fact]
    public void A_bookmark_runs_to_the_next_at_its_depth_or_above()
    {
        // Credits, then two adventures with chapters, then an appendix.
        (int Page, int Depth)[] outline = [(0, 0), (2, 0), (2, 1), (6, 1), (12, 0), (13, 1), (12, 1), (20, 0)];
        Assert.Equal(new PartPages(2, 11), PartRanges.FromBookmark(outline, 1, 24));
        Assert.Equal(new PartPages(2, 5), PartRanges.FromBookmark(outline, 2, 24));
        // A chapter bookmark pointing back before its own doesn't end it.
        Assert.Equal(new PartPages(13, 19), PartRanges.FromBookmark(outline, 5, 24));
        Assert.Equal(new PartPages(20, 23), PartRanges.FromBookmark(outline, 7, 24));
    }

    static MetadataClaim Claim(long id, MetadataField field, string value, AssertionOrigin origin, AssertionState state) =>
        new(id, field.Key, value, MetadataValues.Normalize(field, value), origin, state, Now, DecidedUtc: state == AssertionState.Confirmed ? Now : null);

    [Fact]
    public void An_adventure_takes_its_system_publisher_and_setting_from_its_file()
    {
        var file = EffectiveMetadata.Compute(
            [
                Claim(1, MetadataFields.System, "dnd", AssertionOrigin.User, AssertionState.Confirmed),
                Claim(2, MetadataFields.Publisher, "Made Up Press", AssertionOrigin.Embedded, AssertionState.Provisional),
                Claim(3, MetadataFields.Settings, "greyhawk", AssertionOrigin.Ai, AssertionState.Provisional),
                Claim(4, MetadataFields.Title, "The Big Compilation", AssertionOrigin.User, AssertionState.Confirmed),
                Claim(5, MetadataFields.Levels, "1-10", AssertionOrigin.Ai, AssertionState.Provisional),
            ], []);
        IReadOnlyList<MetadataClaim> own =
            [
                Claim(10, MetadataFields.Title, "Warehouse heist", AssertionOrigin.User, AssertionState.Confirmed),
                Claim(11, MetadataFields.Settings, "eberron", AssertionOrigin.User, AssertionState.Confirmed),
            ];

        var part = EffectiveMetadata.Compute(PartMetadata.Inherit(own, file), []);

        Assert.Equal("Warehouse heist", part[MetadataFields.Title].First!.Value);
        // Confirmed on the file, so confirmed on the part; only suggested for the file, so only suggested for the part.
        Assert.True(part[MetadataFields.System].First!.Confirmed);
        Assert.Equal("dnd", part[MetadataFields.System].First!.Value);
        Assert.True(PartMetadata.IsInherited(part[MetadataFields.System].First!.Source));
        Assert.False(part[MetadataFields.Publisher].First!.Confirmed);
        // Its own setting wins; levels are its own, never the file's.
        Assert.Equal(["eberron"], part[MetadataFields.Settings].Values.Select(v => v.Value));
        Assert.Empty(part[MetadataFields.Levels].Values);
    }

    [Fact]
    public void A_value_rejected_for_an_adventure_isnt_taken_from_its_file()
    {
        var file = EffectiveMetadata.Compute([Claim(1, MetadataFields.Publisher, "Made Up Press", AssertionOrigin.Embedded, AssertionState.Provisional)], []);
        var part = EffectiveMetadata.Compute(PartMetadata.Inherit([], file), [("publisher", "made up press")]);
        Assert.Empty(part[MetadataFields.Publisher].Values);
    }

    [Fact]
    public void Values_an_adventure_takes_from_its_file_dont_ask_for_review_on_it()
    {
        var file = EffectiveMetadata.Compute([Claim(1, MetadataFields.Publisher, "Made Up Press", AssertionOrigin.Embedded, AssertionState.Provisional)], []);
        var part = EffectiveMetadata.Compute(PartMetadata.Inherit([Claim(10, MetadataFields.Title, "Sewer chase", AssertionOrigin.User, AssertionState.Confirmed)], file), []);
        Assert.Empty(MetadataReview.Find(part, reviewAll: true));
    }
}
