using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Core.Tests;

public class StoreLinkTests
{
    static readonly Vocabulary Stores = new(
        [
            new Term("store", "drivethrurpg", "DriveThruRPG"),
            new Term("store", "itch-io", "itch.io"),
            new Term("store", "kickstarter", "Kickstarter"),
            new Term("store", StoreLink.OtherStore, "Other store"),
        ],
        [("store", "drivethrurpg", "DTRPG"), ("store", "drivethrurpg", "drivethrurpg.com"), ("store", "kickstarter", "kickstarter.com")]);

    [Theory]
    [InlineData("kickstarter", "kickstarter", null)]
    [InlineData("drivethrurpg https://www.drivethrurpg.com/en/product/1/vault-job", "drivethrurpg", "https://www.drivethrurpg.com/en/product/1/vault-job")]
    public void A_stored_value_is_a_store_and_maybe_a_link(string stored, string store, string? url)
    {
        var link = StoreLink.Parse(stored);
        Assert.Equal((store, url), (link.Store, link.Url));
        Assert.Equal(stored, link.ToString());
    }

    [Theory]
    [InlineData("DriveThruRPG https://www.drivethrurpg.com/en/product/1", "DriveThruRPG", "https://www.drivethrurpg.com/en/product/1")]
    [InlineData("DriveThruRPG - (https://www.drivethrurpg.com/x)", "DriveThruRPG", "https://www.drivethrurpg.com/x")]
    [InlineData("www.example.com/vault-job", "", "https://www.example.com/vault-job")]
    [InlineData("Kickstarter", "Kickstarter", null)]
    public void What_was_typed_splits_into_a_name_and_a_link(string typed, string name, string? url) =>
        Assert.Equal((name, url, (string?)null), StoreLink.Split(typed));

    [Theory]
    [InlineData("javascript://alert(1)")]
    [InlineData("file:///C:/Books/vault.pdf")]
    [InlineData("https://ana:secret@example.com/vault")]
    [InlineData("Kickstarter https://a.example.com https://b.example.com")]
    public void Only_one_plain_web_link_is_kept(string typed) => Assert.NotNull(StoreLink.Split(typed).Problem);

    [Fact]
    public void A_list_splits_at_commas_but_not_inside_links()
    {
        Assert.Equal(
            ["Kickstarter", "itch.io https://example.itch.io/vault?a=1,2;b=3", "DTRPG"],
            StoreLink.SplitList("Kickstarter, itch.io https://example.itch.io/vault?a=1,2;b=3, DTRPG"));
        Assert.Equal(["Kickstarter", "DTRPG"], MetadataValues.Split(MetadataFields.GetItFrom, "Kickstarter;DTRPG"));
    }

    [Theory]
    [InlineData("DTRPG https://www.drivethrurpg.com/en/product/1", "drivethrurpg")]
    [InlineData("https://www.drivethrurpg.com/en/product/1", "drivethrurpg")]
    [InlineData("https://example-press.itch.io/vault-job", "itch-io")]
    [InlineData("https://www.examplepress.example/vault-job", StoreLink.OtherStore)]
    public void A_store_is_found_by_name_or_by_its_links_site(string typed, string store) =>
        Assert.Equal(store, StoreLink.Read(typed, Stores).Term?.Key);

    [Fact]
    public void A_name_the_vocabulary_doesnt_know_is_a_new_store_with_its_link()
    {
        var (term, name, url) = StoreLink.Read("Lantern Works shop https://lanternworks.example/vault", Stores);

        Assert.Null(term);
        Assert.Equal(("Lantern Works shop", "https://lanternworks.example/vault"), (name, url));
    }

    [Fact]
    public void A_link_reads_short_on_its_button() =>
        Assert.Equal("drivethrurpg.com/en/product/1/vault-job",
            new StoreLink("drivethrurpg", "https://www.drivethrurpg.com/en/product/1/vault-job/").ShortUrl);

    [Theory]
    [InlineData("Kickstarter https://www.kickstarter.com/projects/x", null)]
    [InlineData("https://www.kickstarter.com/projects/x", null)]
    [InlineData(" - ", "Get it from needs a store's name or a web link.")]
    [InlineData("Kickstarter ftp://example.com/x", "“ftp://example.com/x” isn't a web link a browser can open (http or https).")]
    public void Typed_values_are_checked(string typed, string? problem) =>
        Assert.Equal(problem, MetadataValues.Parse(MetadataFields.GetItFrom, typed).Problem);
}

public class WatermarksTests
{
    static readonly string[] Body =
    [
        "Chapter One\nThe owlbear waits beneath the old mill, guarding a clutch of eggs.",
        "The miller's daughter knows a back way in through the flooded cellar.",
        "Goblins raid the tavern at midnight and steal the ledger from the bar.",
        "A lich keeps the ledger now, in a crypt below the chapel on the hill.",
        "Appendix\nNew monsters: the mill wight, the cellar eel and the ledger mimic.",
    ];

    [Theory]
    [InlineData("Prepared exclusively for Ana Example (Order #48213)", true)]
    [InlineData("This copy licensed to Ana Example", true)]
    [InlineData("Purchased by Ana Example on 2026-01-02", true)]
    [InlineData("ana.example@example.com", true)]
    [InlineData("Transaction ID: 99812345", true)]
    [InlineData("Downloaded from Bundle of Holding", true)]
    [InlineData("The goblins attack at dawn, and the gate falls in 3 rounds.", false)]
    [InlineData("Order of the Silver Flame", false)]
    public void Recognizes_a_watermarks_wording(string line, bool expected) => Assert.Equal(expected, Watermarks.ReadsLikeOne(line));

    [Fact]
    public void A_watermark_with_only_a_name_is_found_because_it_repeats()
    {
        string?[] pages = [.. Body.Select((page, i) => $"{page}\nAna Example\n{i + 1}")];

        Assert.Contains("Ana Example", Watermarks.Find(pages));
        Assert.All(Watermarks.Strip(pages), page => Assert.DoesNotContain("Ana Example", page, StringComparison.Ordinal));
    }

    [Fact]
    public void Stripping_keeps_the_pages_own_text()
    {
        string?[] pages = [.. Body.Select((page, i) => $"{page}\nPrepared exclusively for Ana Example (Order #48213)\n{i + 1}")];

        var stripped = Watermarks.Strip(pages);

        Assert.Equal(pages.Length, stripped.Count);
        Assert.Contains("owlbear waits beneath the old mill", stripped[0], StringComparison.Ordinal);
        Assert.All(stripped, page => Assert.DoesNotContain("48213", page, StringComparison.Ordinal));
    }

    [Fact]
    public void A_short_book_still_loses_a_watermark_that_reads_like_one()
    {
        string?[] pages = ["The Vault Job\nPrepared exclusively for Ana Example", "A heist for 4-6 characters."];

        Assert.Equal(["The Vault Job", "A heist for 4-6 characters."], Watermarks.Strip(pages));
    }

    static readonly Vocabulary Places = new(
        [new Term("own", "print", "Print"), new Term("own", "dnd-beyond", "D&D Beyond"), new Term("store", StoreLink.OtherStore, "Other store")],
        [("own", "dnd-beyond", "DDB"), ("own", "dnd-beyond", "dndbeyond.com")]);

    [Theory]
    [InlineData("https://www.dndbeyond.com/sources/dnd/vault-job", "dnd-beyond", "D&D Beyond")]
    [InlineData("DDB https://www.dndbeyond.com/sources/dnd/vault-job", "dnd-beyond", "DDB")]
    [InlineData("Print", "print", "Print")]
    public void Also_own_finds_its_place_by_name_or_by_its_links_site(string typed, string key, string name)
    {
        var (term, read, _) = StoreLink.Read(typed, Places, MetadataFields.AlsoOwn);

        Assert.Equal((key, name), (term?.Key, read));
    }

    [Fact]
    public void A_link_to_a_site_Also_own_doesnt_know_names_a_new_place_after_the_site()
    {
        var (term, name, url) = StoreLink.Read("https://www.lanternworks.example/library/vault", Places, MetadataFields.AlsoOwn);

        Assert.Null(term);
        Assert.Equal(("lanternworks.example", "https://www.lanternworks.example/library/vault"), (name, url));
    }

    [Fact]
    public void A_link_is_on_its_places_own_site_only_when_the_vocabulary_says_so()
    {
        Assert.True(new StoreLink("dnd-beyond", "https://www.dndbeyond.com/sources/dnd/vault-job").IsOnOwnSite(Places, "own"));
        Assert.False(new StoreLink("dnd-beyond", "https://lanternworks.example/vault").IsOnOwnSite(Places, "own"));
        Assert.False(new StoreLink("print", null).IsOnOwnSite(Places, "own"));
    }

    [Theory]
    [InlineData("D&D Beyond https://www.dndbeyond.com/sources/dnd/vault-job", null)]
    [InlineData(" - ", "Also own needs a name or a web link.")]
    public void Also_own_values_are_checked_as_links_too(string typed, string? problem) =>
        Assert.Equal(problem, MetadataValues.Parse(MetadataFields.AlsoOwn, typed).Problem);

    [Fact]
    public void Also_own_splits_like_Get_it_from() =>
        Assert.Equal(["Print", "D&D Beyond https://www.dndbeyond.com/sources/dnd/a?b=1,2"],
            MetadataValues.Split(MetadataFields.AlsoOwn, "Print, D&D Beyond https://www.dndbeyond.com/sources/dnd/a?b=1,2"));
}
