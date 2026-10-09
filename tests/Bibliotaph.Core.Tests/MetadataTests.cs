using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Core.Tests;

public class MetadataTextTests
{
    [Theory]
    [InlineData("Dungeons & Dragons", "dungeons & dragons")]
    [InlineData("  Dungeons  &  Dragons! ", "dungeons & dragons")]
    [InlineData("Ravenloft: Héroes", "ravenloft heroes")]
    [InlineData("D&D 5e", "d&d 5e")]
    [InlineData("“Quoted”—dash", "quoted dash")]
    [InlineData("---", "")]
    public void Normalizes_for_comparison(string text, string expected) => Assert.Equal(expected, MetadataText.Normalize(text));

    [Theory]
    [InlineData("3", 3, 3, false)]
    [InlineData("1-5", 1, 5, false)]
    [InlineData("1–5", 1, 5, false)]
    [InlineData("Levels 1 to 5", 1, 5, false)]
    [InlineData("lvl 0", 0, 0, false)]
    [InlineData("n/a", 0, 0, true)]
    [InlineData("None", 0, 0, true)]
    public void Parses_level_ranges(string text, int min, int max, bool notApplicable)
    {
        Assert.True(LevelRange.TryParse(text, out var range));
        Assert.Equal(new LevelRange(min, max, notApplicable), range);
    }

    [Theory]
    [InlineData("")]
    [InlineData("high")]
    [InlineData("5-1")]
    [InlineData("31")]
    [InlineData("1-2-3")]
    public void Rejects_what_isnt_a_level_range(string text) => Assert.False(LevelRange.TryParse(text, out _));

    [Fact]
    public void Level_ranges_read_and_store_plainly()
    {
        Assert.Equal("1-5", new LevelRange(1, 5).ToString());
        Assert.Equal("3", new LevelRange(3, 3).ToString());
        Assert.Equal("n/a", LevelRange.None.ToString());
        Assert.Equal("Levels 1–5", new LevelRange(1, 5).Describe());
        Assert.True(new LevelRange(1, 5).Contains(3));
        Assert.False(LevelRange.None.Contains(0));
        Assert.True(new LevelRange(3, 6).Overlaps(5, 9));
        Assert.False(new LevelRange(3, 6).Overlaps(7, 9));
    }

    [Theory]
    [InlineData("levels", "Levels 1 to 4", "1-4", null)]
    [InlineData("levels", "eleven", null, "Levels look like")]
    [InlineData("year", "2019", "2019", null)]
    [InlineData("year", "19", null, "A year looks like")]
    [InlineData("title", "  The   Sunless Citadel ", "The Sunless Citadel", null)]
    [InlineData("title", "!!", null, "needs some letters")]
    public void Parses_what_was_typed(string field, string typed, string? value, string? problem)
    {
        var (parsed, issue) = MetadataValues.Parse(MetadataFields.Get(field), typed);

        Assert.Equal(value, parsed);
        if (problem is null) Assert.Null(issue);
        else Assert.Contains(problem, issue, StringComparison.Ordinal);
    }

    [Fact]
    public void Multi_value_fields_split_on_commas_and_semicolons()
    {
        Assert.Equal(["horror", "mystery"], MetadataValues.Split(MetadataFields.Themes, "horror, mystery;"));
        Assert.Equal(["Smith, Jane"], MetadataValues.Split(MetadataFields.Title, "Smith, Jane"));
        Assert.Empty(MetadataValues.Split(MetadataFields.Title, "  "));
    }
}

public class VocabularyTests
{
    static readonly Vocabulary Vocabulary = new(
        [
            new Term("system", "dnd", "Dungeons & Dragons", "D&D"),
            new Term("edition", "dnd-5e", "5th edition", "5e", "dnd"),
            new Term("type", "adventure", "Adventure"),
            new Term("type", "map-pack", "Map pack"),
            new Term("theme", "dragons", "Dragons"),
        ],
        [("edition", "dnd-5e", "D&D 5e"), ("type", "adventure", "adventures"), ("type", "map-pack", "battle maps"), ("type", "adventure", "module")]);

    [Fact]
    public void Matches_the_longest_phrase_first_and_uses_each_word_once()
    {
        var matches = Vocabulary.Match("D&D 5e Adventures");

        Assert.Equal(["dnd-5e", "adventure"], matches.Select(m => m.Term.Key));
        Assert.Equal("d&d 5e", matches[0].Matched);
    }

    [Fact]
    public void Matches_whole_words_only()
    {
        Assert.Empty(Vocabulary.Match("Adventurers League"));
        Assert.Equal(["dragons"], Vocabulary.Match("Dungeons and Dragons").Select(m => m.Term.Key));
        Assert.Equal(["dnd", "dragons"], Vocabulary.Match("Dungeons & Dragons, more dragons").Select(m => m.Term.Key));
    }

    [Fact]
    public void Resolves_by_label_short_label_key_or_alias_within_one_vocabulary()
    {
        Assert.Equal("dnd", Vocabulary.Resolve("system", "d&d")?.Key);
        Assert.Equal("dnd", Vocabulary.Resolve("system", "Dungeons  &  Dragons")?.Key);
        Assert.Equal("dnd-5e", Vocabulary.Resolve("edition", "5E")?.Key);
        Assert.Equal("adventure", Vocabulary.Resolve("type", "Module")?.Key);
        Assert.Null(Vocabulary.Resolve("system", "module"));
    }

    [Fact]
    public void The_first_term_to_claim_an_alias_keeps_it()
    {
        var vocabulary = new Vocabulary([new Term("type", "a", "Alpha"), new Term("type", "b", "Beta")], [("type", "a", "shared"), ("type", "b", "shared")]);

        Assert.Equal("a", vocabulary.Resolve("type", "shared")?.Key);
    }

    [Fact]
    public void Labels_a_key_that_has_gone_with_the_key_itself() =>
        Assert.Equal("old-term", Vocabulary.Label("type", "old-term"));
}

public class EffectiveMetadataTests
{
    static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    long _id;

    MetadataClaim Claim(MetadataField field, string value, AssertionOrigin origin, AssertionState state = AssertionState.Provisional,
        int minutes = 0, DateTime? decided = null) =>
        new(++_id, field.Key, value, MetadataValues.Normalize(field, value), origin, state, T0.AddMinutes(minutes), DecidedUtc: decided);

    [Fact]
    public void The_highest_priority_suggestion_wins_a_single_value_field()
    {
        var metadata = EffectiveMetadata.Compute(
            [
                Claim(MetadataFields.Title, "Tomb Of Horrors Final", AssertionOrigin.Filename),
                Claim(MetadataFields.Title, "Tomb of Horrors", AssertionOrigin.Embedded),
            ], []);

        var title = metadata[MetadataFields.Title];
        Assert.Equal("Tomb of Horrors", title.First!.Value);
        Assert.False(title.First.Confirmed);
        Assert.Equal("Tomb Of Horrors Final", Assert.Single(title.Alternatives).Value);
        Assert.False(title.NeedsReview); // free text never needs review by itself
    }

    [Fact]
    public void A_confirmed_value_wins_over_any_suggestion_however_new()
    {
        var metadata = EffectiveMetadata.Compute(
            [
                Claim(MetadataFields.System, "dnd", AssertionOrigin.User, AssertionState.Confirmed),
                Claim(MetadataFields.System, "pathfinder", AssertionOrigin.Ai, minutes: 60),
            ], []);

        var system = metadata[MetadataFields.System];
        Assert.Equal("dnd", system.First!.Value);
        Assert.True(system.First.Confirmed);
        Assert.Empty(system.Alternatives);
        Assert.False(metadata.NeedsReview);
    }

    [Fact]
    public void A_rejected_value_never_applies_from_any_source()
    {
        var metadata = EffectiveMetadata.Compute(
            [Claim(MetadataFields.System, "dnd", AssertionOrigin.Folder), Claim(MetadataFields.System, "dnd", AssertionOrigin.Ai, minutes: 5)],
            [("system", "dnd")]);

        Assert.False(metadata[MetadataFields.System].IsKnown);
    }

    [Fact]
    public void Sources_that_disagree_about_a_closed_field_send_it_to_review()
    {
        var metadata = EffectiveMetadata.Compute(
            [Claim(MetadataFields.Edition, "dnd-5e", AssertionOrigin.Folder), Claim(MetadataFields.Edition, "dnd-35", AssertionOrigin.Embedded)], []);

        Assert.Equal("dnd-35", metadata[MetadataFields.Edition].First!.Value);
        Assert.True(metadata[MetadataFields.Edition].NeedsReview);
        Assert.True(metadata.NeedsReview);
    }

    [Fact]
    public void Agreeing_sources_support_one_value()
    {
        var metadata = EffectiveMetadata.Compute(
            [Claim(MetadataFields.System, "dnd", AssertionOrigin.Folder), Claim(MetadataFields.System, "dnd", AssertionOrigin.Embedded)], []);

        var value = Assert.Single(metadata[MetadataFields.System].Values);
        Assert.Equal([AssertionOrigin.Embedded, AssertionOrigin.Folder], value.Support.Select(s => s.Origin));
        Assert.False(metadata.NeedsReview);
    }

    [Fact]
    public void A_multi_value_field_takes_the_top_sources_values_and_keeps_the_rest_as_alternatives()
    {
        var metadata = EffectiveMetadata.Compute(
            [
                Claim(MetadataFields.Types, "adventure", AssertionOrigin.Ai),
                Claim(MetadataFields.Types, "bestiary", AssertionOrigin.Ai),
                Claim(MetadataFields.Types, "map-pack", AssertionOrigin.Folder),
            ], []);

        var types = metadata[MetadataFields.Types];
        Assert.Equal(["adventure", "bestiary"], types.Values.Select(v => v.Value).Order());
        Assert.Equal("map-pack", Assert.Single(types.Alternatives).Value);
        Assert.True(types.NeedsReview);
    }

    [Fact]
    public void Once_the_user_decides_a_multi_value_field_older_suggestions_stay_alternatives_and_stop_asking()
    {
        var decided = T0.AddMinutes(30);
        var metadata = EffectiveMetadata.Compute(
            [
                Claim(MetadataFields.Types, "adventure", AssertionOrigin.Ai, AssertionState.Confirmed, decided: decided),
                Claim(MetadataFields.Types, "map-pack", AssertionOrigin.Folder),
            ], []);

        var types = metadata[MetadataFields.Types];
        Assert.Equal("adventure", Assert.Single(types.Values).Value);
        Assert.Equal("map-pack", Assert.Single(types.Alternatives).Value);
        Assert.False(types.NeedsReview);
    }

    [Fact]
    public void Suggestions_that_arrive_after_the_users_decision_are_added_to_a_multi_value_field()
    {
        var metadata = EffectiveMetadata.Compute(
            [
                Claim(MetadataFields.Themes, "horror", AssertionOrigin.User, AssertionState.Confirmed),
                Claim(MetadataFields.Themes, "mystery", AssertionOrigin.Ai, minutes: 10),
            ], []);

        Assert.Equal(["horror", "mystery"], metadata[MetadataFields.Themes].Values.Select(v => v.Value));
    }

    [Fact]
    public void Superseded_claims_are_ignored_and_the_latest_confirmation_stands()
    {
        var metadata = EffectiveMetadata.Compute(
            [
                Claim(MetadataFields.Title, "Old", AssertionOrigin.User, AssertionState.Superseded),
                Claim(MetadataFields.Title, "First", AssertionOrigin.User, AssertionState.Confirmed, minutes: 1),
                Claim(MetadataFields.Title, "Second", AssertionOrigin.Embedded, AssertionState.Confirmed, decided: T0.AddMinutes(5)),
            ], []);

        Assert.Equal("Second", metadata[MetadataFields.Title].First!.Value);
    }

    [Fact]
    public void Every_field_is_present_and_unknown_by_default()
    {
        Assert.Equal(MetadataFields.All, EffectiveMetadata.Empty.Fields.Select(f => f.Field));
        Assert.All(EffectiveMetadata.Empty.Fields, f => Assert.False(f.IsKnown));
    }
}
