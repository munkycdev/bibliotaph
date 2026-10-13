using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;
using Bibliotaph.Pdf.Host.Tests;

namespace Bibliotaph.Processing.Tests;

/// <summary>
/// The user's own tags through the real pipeline: a tag found in the books' indexed pages by its words, guessed only
/// where the words are used often enough, a rejection that sticks, a deleted tag taking its guesses away, and a book
/// read later checked as it is indexed.
/// </summary>
public sealed partial class PipelineTests
{
    const string Undead = "undead, zombie, lich";

    /// <summary>Six pages, four of them about the dead walking: well over the line.</summary>
    static readonly string[][] BarrowPages =
    [
        ["The Barrow of Bones", "A short crawl under the hill"],
        ["A zombie shambles out of the first crypt."],
        ["The lich keeps its heart in a jar of brine."],
        ["Undead guards stand in rows along the hall."],
        ["The miller's daughter waits by the gate."],
        ["More zombies rise when the bell is rung."],
    ];

    /// <summary>Four pages, three about the dead.</summary>
    static readonly string[][] CryptPages =
    [
        ["Crypt Notes", "Jottings from the catacombs"],
        ["The ghoul and the lich argue over the stairs."],
        ["A zombie choir sings below the chapel."],
        ["Undead in every alcove, waiting."],
    ];

    /// <summary>Five pages, three about the dead.</summary>
    static readonly string[][] OssuaryPages =
    [
        ["Ossuary Guide", "Visiting hours and rules"],
        ["Bones stacked to the ceiling, and an undead keeper."],
        ["The keeper is a lich of the old order."],
        ["Visitors who linger may meet a zombie."],
        ["The gift shop sells candles."],
    ];

    /// <summary>Six pages and a single passing mention: too few pages.</summary>
    static readonly string[][] LedgerPages =
    [
        ["Harbour Ledger", "Trade and tariffs of the river towns"],
        ["Salt fish sells for two coppers a barrel."],
        ["A sailor swears he saw a zombie on the pier."],
        ["The guild meets on the first day of each month."],
        ["Tariffs rise in winter when the river freezes."],
        ["Merchants of the lower quay, by name."],
    ];

    /// <summary>Eighty pages with the words on three: enough pages, but too small a share of the book.</summary>
    static string[][] RulebookPages() =>
        [.. Enumerable.Range(0, 80).Select(i => i is 10 or 40 or 70
            ? new[] { $"Chapter {i}", "Turning the undead with a holy symbol." }
            : [$"Chapter {i}", $"Rules for travel by road and river, part {i}."])];

    void Book(string relative, string[][] pages)
    {
        var target = Path.Combine(_library, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, SyntheticPdfs.BuildPages(pages));
    }

    async Task<EffectiveField> TagsAsync(EntryId entry) => (await _metadata.GetAsync(entry, Ct)).Metadata[MetadataFields.Tags];

    [Fact]
    public async Task A_tag_is_guessed_for_the_books_whose_pages_use_its_words_often_enough()
    {
        Book("Barrow of Bones.pdf", BarrowPages);
        Book("Harbour Ledger.pdf", LedgerPages);
        Book("Thick Rulebook.pdf", RulebookPages());
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();
        var cards = await CardsAsync();
        var (barrow, ledger, rulebook) = (cards["Barrow of Bones.pdf"].Entry, cards["Harbour Ledger.pdf"].Entry, cards["Thick Rulebook.pdf"].Entry);
        Assert.Empty((await TagsAsync(barrow)).Values); // nothing proposes a tag until the user defines one

        var edit = await _userTags.SaveAsync(null, "Undead", Undead, Ct);

        Assert.Null(edit.Problem);
        Assert.Equal(1, edit.Tag!.Books);
        // A guess, with the words as its evidence and the pages that use them, zombies included.
        var guess = Assert.Single((await TagsAsync(barrow)).Values);
        Assert.Equal(("Undead", false, AssertionOrigin.UserTag, Undead), (guess.Value, guess.Confirmed, guess.Origin, guess.Source.Quote));
        Assert.Equal([1, 2, 3, 5], guess.Source.Pages);
        // One page in six, and three pages in eighty, are below the line.
        Assert.Empty((await TagsAsync(ledger)).Values);
        Assert.Empty((await TagsAsync(rulebook)).Values);
        // The library finds it by the tag.
        var found = await new LibraryQueries(_index).SearchDocumentsAsync(SearchPlan.From(SearchQuery.Parse("tag:undead")), new LibraryFilter(), ct: Ct);
        Assert.Equal([barrow], found.Select(e => e.EntryId));

        // A name or words that can't make a tag are refused, and so is a second tag with the same name.
        Assert.NotNull((await _userTags.SaveAsync(null, "Dead, walking", Undead, Ct)).Problem);
        Assert.NotNull((await _userTags.SaveAsync(null, "Ghosts", " , ", Ct)).Problem);
        Assert.NotNull((await _userTags.SaveAsync(null, "undead", "ghost", Ct)).Problem);
    }

    [Fact]
    public async Task A_rejected_guess_stays_rejected_and_deleting_the_tag_takes_its_guesses_but_not_the_ones_kept()
    {
        Book("Barrow of Bones.pdf", BarrowPages);
        Book("Crypt Notes.pdf", CryptPages);
        Book("Ossuary Guide.pdf", OssuaryPages);
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();
        var cards = await CardsAsync();
        var (barrow, crypt, ossuary) = (cards["Barrow of Bones.pdf"].Entry, cards["Crypt Notes.pdf"].Entry, cards["Ossuary Guide.pdf"].Entry);
        var tag = (await _userTags.SaveAsync(null, "Undead", Undead, Ct)).Tag!;
        Assert.Equal(3, tag.Books);
        var first = Assert.Single((await TagsAsync(barrow)).Values).Source;

        // Not right on one book; kept on another.
        await _metadata.RejectAsync(crypt, MetadataFields.Tags, "undead", Ct);
        await _metadata.ConfirmAsync(ossuary, MetadataFields.Tags, Ct);

        // Changing the tag's words looks again: the rejection stands, the kept one stays kept, and the guess that
        // still holds keeps its age, with the new words as its evidence.
        var edited = (await _userTags.SaveAsync(tag.Id, "Undead", Undead + ", ghoul", Ct)).Tag!;
        Assert.Equal(2, edited.Books);
        Assert.Empty((await TagsAsync(crypt)).Values);
        Assert.True(Assert.Single((await TagsAsync(ossuary)).Values).Confirmed);
        var again = Assert.Single((await TagsAsync(barrow)).Values).Source;
        Assert.Equal((first.Id, first.CreatedUtc, Undead + ", ghoul"), (again.Id, again.CreatedUtc, again.Quote));

        // Deleting the tag takes its guesses away; a book where the user kept it keeps it.
        Assert.True(await _userTags.DeleteAsync(tag.Id, Ct));
        Assert.Empty(await _userTags.ListAsync(Ct));
        Assert.Empty((await TagsAsync(barrow)).Values);
        var kept = Assert.Single((await TagsAsync(ossuary)).Values);
        Assert.Equal(("Undead", true), (kept.Value, kept.Confirmed));
        var found = await new LibraryQueries(_index).SearchDocumentsAsync(SearchPlan.From(SearchQuery.Parse("tag:undead")), new LibraryFilter(), ct: Ct);
        Assert.Equal([ossuary], found.Select(e => e.EntryId));

        // Made again, it finds the book it was taken from, and still not the one the user said it was wrong for.
        await _userTags.SaveAsync(null, "Undead", Undead, Ct);
        Assert.Single((await TagsAsync(barrow)).Values);
        Assert.Empty((await TagsAsync(crypt)).Values);
    }

    [Fact]
    public async Task A_book_read_after_a_tag_was_made_is_checked_as_it_is_indexed()
    {
        Book("Harbour Ledger.pdf", LedgerPages);
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();
        Assert.Equal(0, (await _userTags.SaveAsync(null, "Undead", Undead, Ct)).Tag!.Books);

        Book("Downloads/Barrow of Bones.pdf", BarrowPages);
        _service.RequestScan();
        await SettleUntilAsync(async () => (await CardsAsync()).TryGetValue("Downloads/Barrow of Bones.pdf", out var card)
            && (await TagsAsync(card.Entry)).Values.Count > 0);

        var cards = await CardsAsync();
        var guess = Assert.Single((await TagsAsync(cards["Downloads/Barrow of Bones.pdf"].Entry)).Values);
        Assert.Equal(("Undead", false, AssertionOrigin.UserTag), (guess.Value, guess.Confirmed, guess.Origin));
        Assert.Empty((await TagsAsync(cards["Harbour Ledger.pdf"].Entry)).Values);
        Assert.Equal(1, Assert.Single(await _userTags.ListAsync(Ct)).Books);
    }
}
