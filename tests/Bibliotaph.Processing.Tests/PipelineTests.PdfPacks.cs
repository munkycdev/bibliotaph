using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;
using Bibliotaph.Pdf.Host.Tests;

namespace Bibliotaph.Processing.Tests;

/// <summary>
/// Packs of files made by hand: PDFs packed with an image, as a party's character sheets are, whose pages are still
/// found, under the pack, and which a split gives back their own cards.
/// </summary>
public sealed partial class PipelineTests
{
    [Fact]
    public async Task Two_pdfs_and_an_image_packed_together_are_one_card_whose_pdfs_pages_are_found_under_it_until_it_is_split()
    {
        Copy(pdfs.KnownText, "Party/Fighter Sheet.pdf");
        Copy(pdfs.WatermarkedForAna, "Party/Wizard Sheet.pdf");
        File.WriteAllBytes(Path.Combine(_library, "Party", "Party Portrait.png"), FakeCodec.Png(300, 400));
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();

        var search = new LibraryQueries(_index);
        // Three files in a folder are never packed by themselves.
        Assert.Equal(3, (await search.ListAsync(new LibraryFilter(), ct: Ct)).Count(c => !c.IsPack));
        var paths = await _libraryStore.GetRelativePathsAsync(Ct);
        long Document(string name) => paths.Single(p => p.RelativePath.EndsWith(name, StringComparison.Ordinal)).DocumentId;
        var (fighter, wizard, portrait) = (Document("Fighter Sheet.pdf"), Document("Wizard Sheet.pdf"), Document("Party Portrait.png"));
        var own = await _entries.GetEntriesAsync([fighter, wizard, portrait], Ct);

        Assert.NotNull(await _packs.PackTogetherAsync([.. own.Values.Select(e => e.EntryId)], "The Party", Ct));
        await _packs.PlanAsync(Ct);

        var pack = Assert.Single(await search.ListAsync(new LibraryFilter(), ct: Ct));
        Assert.Equal((true, 3, 2), (pack.IsPack, pack.Members, pack.PdfMembers));
        var files = await search.GetPackImagesAsync(pack.EntryId, Ct);
        Assert.Equal([("Fighter Sheet.pdf", "pdf"), ("Party Portrait.png", "png"), ("Wizard Sheet.pdf", "pdf")], files.Select(f => (f.Name, f.Format)));
        Assert.Equal(fighter, pack.DocumentId);

        // Each PDF's pages are found under the pack, its card, and open in the PDF they are in.
        var owlbear = SearchPlan.From(SearchQuery.Parse("owlbear"));
        var found = Assert.Single((await search.SearchPagesAsync(owlbear, new LibraryFilter(), ct: Ct)).Entries);
        Assert.Equal(pack.EntryId, found.Entry.EntryId);
        Assert.Equal((pack.EntryId, fighter, SyntheticPdfs.KnownPhrasePage), (found.Pages[0].EntryId, found.Pages[0].DocumentId, found.Pages[0].PdfPage));
        found = Assert.Single((await search.SearchPagesAsync(SearchPlan.From(SearchQuery.Parse("abbey")), new LibraryFilter(), ct: Ct)).Entries);
        Assert.Equal(pack.EntryId, found.Entry.EntryId);
        Assert.All(found.Pages, p => Assert.Equal(wizard, p.DocumentId));
        // Packs are found with Packs chosen, not with Books, which are the PDFs on cards of their own.
        Assert.Single((await search.SearchPagesAsync(owlbear, new LibraryFilter(Kind: KindFilter.Packs), ct: Ct)).Entries);
        Assert.Empty((await search.SearchPagesAsync(owlbear, new LibraryFilter(Kind: KindFilter.Books), ct: Ct)).Entries);
        // A PDF's name finds the pack, as an image's does.
        var named = Assert.Single(await search.SearchDocumentsAsync(SearchPlan.From(SearchQuery.Parse("wizard")), new LibraryFilter(), ct: Ct));
        Assert.Equal((pack.EntryId, "Wizard Sheet.pdf", wizard), (named.EntryId, named.MatchedMember, named.MatchedDocumentId));

        Assert.NotNull(await _packs.SplitAsync(pack.EntryId, Ct));

        var cards = await search.ListAsync(new LibraryFilter(), ct: Ct);
        Assert.Equal(3, cards.Count);
        Assert.DoesNotContain(cards, c => c.IsPack);
        Assert.Equal(own.Keys.Order(), cards.Select(c => c.DocumentId).Order());
        found = Assert.Single((await search.SearchPagesAsync(owlbear, new LibraryFilter(), ct: Ct)).Entries);
        Assert.Equal((own[fighter].EntryId, fighter), (found.Entry.EntryId, found.Entry.DocumentId));
    }

    [Fact]
    public async Task A_folder_of_images_with_a_pdf_beside_them_is_packed_of_its_images_only()
    {
        var tokens = Path.Combine(_library, "Tokens", "Undead");
        Directory.CreateDirectory(tokens);
        for (var i = 1; i <= PackStore.AutomaticMinimum; i++)
            File.WriteAllBytes(Path.Combine(tokens, $"Zombie {i}.png"), FakeCodec.Png(100 + i, 100));
        Copy(pdfs.KnownText, "Tokens/Undead/Token Guide.pdf");
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();

        var search = new LibraryQueries(_index);
        // The PDF keeps the folder from packing by itself: Needs review asks about its images.
        Assert.DoesNotContain(await search.ListAsync(new LibraryFilter(), ct: Ct), c => c.IsPack);
        var proposal = Assert.Single(await _packs.GetProposalsAsync(Ct));
        Assert.Equal((true, PackStore.AutomaticMinimum), (proposal.BesidePdfs, proposal.Images.Count));

        Assert.NotNull(await _packs.AnswerAsync(proposal.PackId, PackAnswer.Packed, Ct));

        var cards = await search.ListAsync(new LibraryFilter(), ct: Ct);
        var pack = Assert.Single(cards, c => c.IsPack);
        Assert.Equal((PackStore.AutomaticMinimum, 0), (pack.Members, pack.PdfMembers));
        // The guide's pages are its own card's.
        var guide = Assert.Single(cards, c => c is { IsPack: false, Format: "pdf" });
        var found = Assert.Single((await search.SearchPagesAsync(SearchPlan.From(SearchQuery.Parse("owlbear")), new LibraryFilter(), ct: Ct)).Entries);
        Assert.Equal(guide.EntryId, found.Entry.EntryId);
    }
}
