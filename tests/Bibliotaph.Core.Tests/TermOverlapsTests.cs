using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Core.Tests;

public class TermOverlapsTests
{
    [Theory]
    [InlineData("The Forgotten Realms (5e)", "forgotten realm")]
    [InlineData("Forgotten Realms", "forgotten realm")]
    [InlineData("Domains of Dread", "domain dread")]
    [InlineData("D&D", "d and d")]
    [InlineData("Mysteries", "mystery")]
    [InlineData("Boss", "boss")]
    [InlineData("Pathfinder 2e", "pathfinder 2e")]
    public void Folds_a_name_for_comparison(string name, string expected) => Assert.Equal(expected, TermOverlaps.Fold(name));

    static OverlapTerm Term(string key, string label, bool starter = false, string? shortLabel = null, params string[] others) =>
        new(key, label, shortLabel, others, starter);

    [Fact]
    public void Finds_the_same_name_but_for_small_words_a_near_spelling_initials_and_a_name_that_starts_the_other()
    {
        var found = TermOverlaps.Find(
        [
            Term("forgotten-realms", "Forgotten Realms", starter: true, others: ["FR", "Faerûn"]),
            Term("the-forgotten-realms", "The Forgotten Realms"),
            Term("greyhawk", "Greyhawk", starter: true),
            Term("grayhawk", "Grayhawk"),
            Term("eberron", "Eberron", starter: true),
            Term("wotc", "WotC"),
            Term("wizards-of-the-coast", "Wizards of the Coast"),
            Term("ravenloft", "Ravenloft", starter: true),
            Term("ravenloft-5e", "Ravenloft 5e"),
            Term("pathfinder-1e", "Pathfinder 1e"),
            Term("pathfinder-2e", "Pathfinder 2e"),
            Term("dark-sun", "Dark Sun", starter: true),
            Term("sun", "Sun"),
        ]);

        Assert.Equal(
        [
            (["forgotten-realms", "the-forgotten-realms"], true),
            (["greyhawk", "grayhawk"], true),
            (["wotc", "wizards-of-the-coast"], false),
            (["ravenloft", "ravenloft-5e"], false),
        ], found.Select(f => (f.Keys.ToArray(), f.Sure)));
        Assert.Contains("small words", found[0].Reason, StringComparison.Ordinal);
        Assert.Contains("Spelt", found[1].Reason, StringComparison.Ordinal);
        Assert.Contains("initials", found[2].Reason, StringComparison.Ordinal);
        Assert.Contains("starts or ends", found[3].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_starter_terms_and_a_pair_kept_apart_are_never_paired_and_groups_join()
    {
        var terms = new[]
        {
            Term("horror", "Horror", starter: true), Term("cosmic-horror", "Cosmic Horror", starter: true),
            Term("realms", "Realms"), Term("the-realms", "The Realms"), Term("realm", "Realm"),
            Term("war", "War"), Term("wars", "Wars"),
        };
        var found = TermOverlaps.Find(terms, [("wars", "war")]);

        Assert.Equal([["realms", "the-realms", "realm"]], found.Select(f => f.Keys.ToArray()));
    }
}
