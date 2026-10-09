using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Core.Tests;

public class MetadataReviewTests
{
    static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    long _id;

    MetadataClaim Claim(MetadataField field, string value, AssertionOrigin origin, AssertionState state = AssertionState.Provisional,
        int minutes = 0, DateTime? decided = null, string? quote = null) =>
        new(++_id, field.Key, value, MetadataValues.Normalize(field, value), origin, state, T0.AddMinutes(minutes), quote, DecidedUtc: decided);

    MetadataClaim Title(string value = "Tomb of Horrors") => Claim(MetadataFields.Title, value, AssertionOrigin.Filename);

    static IReadOnlyList<ReviewIssue> Find(IEnumerable<MetadataClaim> claims, bool reviewAll = false) =>
        MetadataReview.Find(EffectiveMetadata.Compute(claims, []), reviewAll);

    [Fact]
    public void Agreed_suggestions_and_a_usable_title_need_no_review_by_default() =>
        Assert.Empty(Find([Title(), Claim(MetadataFields.System, "dnd", AssertionOrigin.Folder), Claim(MetadataFields.System, "dnd", AssertionOrigin.Embedded)]));

    [Fact]
    public void A_single_field_conflict_offers_the_suggestion_that_lost()
    {
        var issue = Assert.Single(Find(
            [Title(), Claim(MetadataFields.Edition, "dnd-5e", AssertionOrigin.Folder, quote: "D&D 5e"), Claim(MetadataFields.Edition, "dnd-35", AssertionOrigin.Embedded)]));

        Assert.Equal(ReviewKind.Conflict, issue.Kind);
        Assert.Equal(MetadataFields.Edition, issue.Field);
        Assert.Equal("dnd-35", Assert.Single(issue.Current).Value);
        Assert.Equal("dnd-5e", Assert.Single(issue.Suggested).Value);
        Assert.Equal("dnd-5e", Assert.Single(issue.Proposed).Value);
        Assert.Equal("D&D 5e", issue.Evidence?.Quote);
        Assert.True(issue.CanAccept);
    }

    [Fact]
    public void A_multi_value_conflict_offers_the_other_values_as_well()
    {
        var issue = Assert.Single(Find(
            [Title(), Claim(MetadataFields.Types, "adventure", AssertionOrigin.Embedded), Claim(MetadataFields.Types, "map-pack", AssertionOrigin.Folder)]));

        Assert.Equal(["adventure"], issue.Current.Select(v => v.Value));
        Assert.Equal(["adventure", "map-pack"], issue.Suggested.Select(v => v.Value));
        Assert.Equal("map-pack", Assert.Single(issue.Proposed).Value);
    }

    [Fact]
    public void A_value_set_on_another_copy_that_disagrees_is_a_copies_disagree_card()
    {
        var mine = Claim(MetadataFields.Title, "The Drowned Abbey", AssertionOrigin.User, AssertionState.Confirmed);
        var theirs = Claim(MetadataFields.Title, "Abbey backup", AssertionOrigin.User, AssertionState.SetAside);

        var effective = EffectiveMetadata.Compute([mine, theirs], []);
        var issue = Assert.Single(MetadataReview.Find(effective, reviewAll: false));

        // The set-aside value never shows or competes; it is only offered.
        Assert.Equal("The Drowned Abbey", Assert.Single(effective[MetadataFields.Title].Values).Value);
        Assert.Empty(effective[MetadataFields.Title].Alternatives);
        Assert.Equal(ReviewKind.CopiesDisagree, issue.Kind);
        Assert.Equal("The Drowned Abbey", Assert.Single(issue.Current).Value);
        Assert.Equal("Abbey backup", Assert.Single(issue.Proposed).Value);
    }

    [Fact]
    public void A_set_aside_value_that_agrees_or_was_rejected_needs_no_card()
    {
        var mine = Claim(MetadataFields.Title, "The Drowned Abbey", AssertionOrigin.User, AssertionState.Confirmed);
        var same = Claim(MetadataFields.Title, "the drowned abbey", AssertionOrigin.User, AssertionState.SetAside);
        var other = Claim(MetadataFields.Title, "Abbey backup", AssertionOrigin.User, AssertionState.SetAside);

        Assert.Empty(Find([mine, same]));
        Assert.Empty(MetadataReview.Find(EffectiveMetadata.Compute([mine, other], [(MetadataFields.Title.Key, "abbey backup")]), reviewAll: false));
    }

    [Theory]
    [InlineData("IMG_0042")]
    [InlineData("Scan 12")]
    [InlineData("1999")]
    [InlineData("d41d8cd98f00b204e9800998ecf8427e")]
    public void A_title_nobody_would_know_the_book_by_needs_one(string title)
    {
        var issue = Assert.Single(Find([Title(title)]));

        Assert.Equal(ReviewKind.MissingTitle, issue.Kind);
        Assert.False(issue.CanAccept);
        Assert.Empty(issue.Proposed);
    }

    [Theory]
    [InlineData("Tomb of Horrors")]
    [InlineData("Map 3")]
    [InlineData("B2 Keep on the Borderlands")]
    public void Real_titles_are_usable(string title) => Assert.True(TitleQuality.IsUsable(title));

    [Fact]
    public void A_title_the_user_confirmed_is_never_missing() =>
        Assert.Empty(Find([Claim(MetadataFields.Title, "IMG 0042", AssertionOrigin.Filename, AssertionState.Confirmed)]));

    [Fact]
    public void Reviewing_all_suggestions_adds_every_unconfirmed_value()
    {
        var issues = Find(
            [
                Title(),
                Claim(MetadataFields.Types, "adventure", AssertionOrigin.User, AssertionState.Confirmed),
                Claim(MetadataFields.Types, "bestiary", AssertionOrigin.Ai, minutes: 10),
            ], reviewAll: true);

        Assert.Equal([MetadataFields.Title, MetadataFields.Types], issues.Select(i => i.Field));
        Assert.All(issues, i => Assert.Equal(ReviewKind.Suggestion, i.Kind));
        var types = issues[1];
        Assert.Equal(["adventure"], types.Current.Select(v => v.Value));
        Assert.Equal("bestiary", Assert.Single(types.Proposed).Value);
    }

    [Fact]
    public void Cards_proposing_the_same_values_for_the_same_field_group_together()
    {
        var one = Assert.Single(Find([Title("One"), Claim(MetadataFields.Types, "adventure", AssertionOrigin.Folder)], reviewAll: true), i => i.Field == MetadataFields.Types);
        var two = Assert.Single(Find([Title("Two"), Claim(MetadataFields.Types, "adventure", AssertionOrigin.Embedded)], reviewAll: true), i => i.Field == MetadataFields.Types);
        var other = Assert.Single(Find([Title("Three"), Claim(MetadataFields.Types, "bestiary", AssertionOrigin.Folder)], reviewAll: true), i => i.Field == MetadataFields.Types);

        Assert.Equal(one.GroupKey, two.GroupKey);
        Assert.NotEqual(one.GroupKey, other.GroupKey);
    }
}
