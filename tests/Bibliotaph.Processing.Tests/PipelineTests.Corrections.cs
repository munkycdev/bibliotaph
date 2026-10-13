using Bibliotaph.Catalog;
using Bibliotaph.Classification;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;

namespace Bibliotaph.Processing.Tests;

/// <summary>
/// Easier corrections through the real pipeline: a correction offered to look-alikes, folder rules proposed and used,
/// the Cataloged wrong card in Needs review, and worked examples for the classifier.
/// </summary>
public sealed partial class PipelineTests
{
    void Image(string relative, int width)
    {
        var target = Path.Combine(_library, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, FakeCodec.Png(width, 400));
    }

    /// <summary>Each file's document and card, by its path under its library folder with forward slashes.</summary>
    async Task<Dictionary<string, (long Document, EntryId Entry)>> CardsAsync()
    {
        var cards = new Dictionary<string, (long, EntryId)>();
        foreach (var (documentId, relativePath) in await _libraryStore.GetRelativePathsAsync(Ct))
            cards[relativePath.Replace('\\', '/')] = (documentId, (await _entries.GetEntryAsync(documentId, Ct))!.EntryId);
        return cards;
    }

    async Task<EffectiveField> SystemAsync(EntryId entry) => (await _metadata.GetAsync(entry, Ct)).Metadata[MetadataFields.System];

    CorrectionService Corrections() => new(_correctionStore, _metadataStore, _metadata, _libraryStore, new LibraryQueries(_index));

    ReviewService Review(CorrectionService corrections) => new(_metadataStore, _vocabulary, _metadata, _projector, _queries, _settings,
        new VocabularyService(_vocabulary, _projector, _service, _hints), _copies, _packs, corrections: corrections, rules: _folderRules);

    [Fact]
    public async Task A_correction_is_offered_to_the_books_from_the_same_folder_that_showed_the_same_guess_and_undone_in_one_go()
    {
        Image("Fey Court/Primer.png", 600);
        Image("Fey Court/Masks.png", 601);
        Image("Fey Court/Gate.png", 602);
        Image("Fey Court/Maps/Glade.png", 603);
        Image("Elsewhere/Lantern.png", 604);
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();
        var cards = await CardsAsync();
        foreach (var (_, entry) in cards.Values)
            await _metadataStore.AddSuggestionsAsync(entry, null, [new MetadataProposal(MetadataFields.System, "dnd", AssertionOrigin.Ai, "the court of the fey")], Ct);
        // The user kept the guess on one book, so it is theirs and not offered.
        await _metadata.ConfirmAsync(cards["Fey Court/Gate.png"].Entry, MetadataFields.System, Ct);
        var corrections = Corrections();
        var (primer, masks, glade) = (cards["Fey Court/Primer.png"].Entry, cards["Fey Court/Masks.png"].Entry, cards["Fey Court/Maps/Glade.png"].Entry);

        var before = await SystemAsync(primer);
        Assert.Null(await _metadata.SetAsync(primer, MetadataFields.System, "Pathfinder", Ct));
        var offer = await corrections.FindLookAlikesAsync(primer, before, await SystemAsync(primer), Ct);

        // Only the other book directly in the folder that still shows the same guess: not a subfolder's, not one the user kept.
        Assert.NotNull(offer);
        Assert.Equal([masks], offer.Books);
        Assert.Equal(("Fey Court", MetadataFields.System), (offer.FolderName, offer.Field));
        Assert.Equal(["dnd"], offer.Before);
        Assert.Equal(["pathfinder"], offer.After);
        Assert.Equal([new BulkChange(MetadataFields.System, BulkAction.Set, "pathfinder")], offer.Changes);

        var (problem, undo) = await corrections.ApplyAsync(offer, ct: Ct);
        Assert.Null(problem);
        Assert.Equal(("pathfinder", true), ((await SystemAsync(masks)).First!.Value, (await SystemAsync(masks)).First!.Confirmed));
        Assert.Equal("dnd", (await SystemAsync(glade)).First!.Value);

        // One Undo takes the whole change back.
        await _metadata.UndoManyAsync(undo!, ct: Ct);
        Assert.Equal(("dnd", false), ((await SystemAsync(masks)).First!.Value, (await SystemAsync(masks)).First!.Confirmed));

        // A book alone in its folder has nobody to offer the correction to.
        var lantern = cards["Elsewhere/Lantern.png"].Entry;
        before = await SystemAsync(lantern);
        await _metadata.RejectAsync(lantern, MetadataFields.System, "dnd", Ct);
        Assert.Null(await corrections.FindLookAlikesAsync(lantern, before, await SystemAsync(lantern), Ct));
    }

    [Fact]
    public async Task The_same_value_set_on_three_books_from_a_folder_proposes_a_rule_that_suggests_it_for_the_rest_and_new_files()
    {
        Image("Fey Court/A.png", 610);
        Image("Fey Court/B.png", 611);
        Image("Fey Court/C.png", 612);
        Image("Fey Court/D.png", 613);
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();
        var cards = await CardsAsync();
        var review = Review(Corrections());
        var waiting = await review.CountAsync(Ct);

        await _metadata.SetAsync(cards["Fey Court/A.png"].Entry, MetadataFields.System, "Pathfinder", Ct);
        await _metadata.EditManyAsync([cards["Fey Court/B.png"].Entry], [new BulkChange(MetadataFields.System, BulkAction.Set, "pathfinder")], ct: Ct);
        Assert.Empty(await _folderRules.GetProposedAsync(Ct));
        await _metadata.SetAsync(cards["Fey Court/C.png"].Entry, MetadataFields.System, "Pathfinder", Ct);

        var rule = Assert.Single((await review.GetQueueAsync(Ct)).Rules!);
        Assert.Equal(("Fey Court", "system", "pathfinder", 3, FolderRuleState.Proposed), (rule.Folder.Folder, rule.Field, rule.Value, rule.Books, rule.State));
        Assert.Equal(waiting + 1, await review.CountAsync(Ct));

        // Use this rule: the book nobody set gets it as a suggestion, and so does a new file further down the folder.
        var undo = await review.AnswerRuleAsync(rule, use: true, Ct);
        var d = await SystemAsync(cards["Fey Court/D.png"].Entry);
        Assert.Equal(("pathfinder", false, AssertionOrigin.FolderRule), (d.First!.Value, d.First.Confirmed, d.First.Source.Origin));
        Assert.Equal(waiting, await review.CountAsync(Ct));
        Image("Fey Court/Maps/E.png", 614);
        _service.RequestScan();
        await SettleUntilAsync(async () => (await CardsAsync()).TryGetValue("Fey Court/Maps/E.png", out var e)
            && (await SystemAsync(e.Entry)).First?.Value == "pathfinder");

        // Undo puts it back to be answered, and its suggestions go.
        await undo();
        Assert.False((await SystemAsync(cards["Fey Court/D.png"].Entry)).IsKnown);
        Assert.Single(await _folderRules.GetProposedAsync(Ct));

        // No thanks: off for good, listed in Settings as off, and not proposed again by a fourth book.
        await review.AnswerRuleAsync(rule, use: false, Ct);
        await _metadata.SetAsync(cards["Fey Court/D.png"].Entry, MetadataFields.System, "Pathfinder", Ct);
        Assert.Empty(await _folderRules.GetProposedAsync(Ct));
        Assert.Equal(FolderRuleState.Off, Assert.Single(await _folderRules.GetAnsweredAsync(Ct)).State);
    }

    [Fact]
    public async Task A_folder_whose_name_already_suggests_the_value_gets_no_rule()
    {
        Image("Pathfinder/A.png", 620);
        Image("Pathfinder/B.png", 621);
        Image("Pathfinder/C.png", 622);
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();

        foreach (var (_, entry) in (await CardsAsync()).Values) await _metadata.SetAsync(entry, MetadataFields.System, "Pathfinder", Ct);

        Assert.Empty(await _folderRules.GetProposedAsync(Ct));
    }

    [Fact]
    public async Task A_book_left_cataloged_wrong_waits_in_needs_review_until_it_is_fixed_or_dismissed()
    {
        Image("Fey Court/Court of Thorns Primer.png", 630);
        var kept = Path.Combine(_dir, "Other Library");
        Directory.CreateDirectory(kept);
        File.WriteAllBytes(Path.Combine(kept, "Glimmerwood Masks.png"), FakeCodec.Png(631, 400));
        await _roots.AddAsync(_library, Ct);
        var other = await _roots.AddAsync(kept, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();
        var cards = await CardsAsync();
        var (primer, masks) = (cards["Fey Court/Court of Thorns Primer.png"].Entry, cards["Glimmerwood Masks.png"].Entry);
        var corrections = Corrections();
        var review = Review(corrections);
        var waiting = await review.CountAsync(Ct);

        Assert.True(await corrections.LeaveUnfinishedAsync(primer, Ct));
        Assert.False(await corrections.LeaveUnfinishedAsync(primer, Ct));
        var item = Assert.Single((await review.GetQueueAsync(Ct)).Corrections!);
        Assert.Equal((primer, "Court of Thorns Primer"), (item.EntryId, item.Title));
        Assert.Equal(waiting + 1, await review.CountAsync(Ct));

        // It's fine now, then Undo.
        var undo = await review.DismissCorrectionAsync(item, Ct);
        Assert.Empty((await review.GetQueueAsync(Ct)).Corrections!);
        await undo();
        Assert.Equal(item.CreatedUtc, Assert.Single((await review.GetQueueAsync(Ct)).Corrections!).CreatedUtc);

        // Corrected in its details: the card goes.
        await corrections.FinishAsync(primer, Ct);
        Assert.Empty((await review.GetQueueAsync(Ct)).Corrections!);
        Assert.Equal(waiting, await review.CountAsync(Ct));

        // A book whose library folder was removed isn't shown, so neither is its card.
        await corrections.LeaveUnfinishedAsync(masks, Ct);
        await _roots.RemoveAsync(other.Id, Ct);
        Assert.Empty((await review.GetQueueAsync(Ct)).Corrections!);
    }

    [Fact]
    public async Task Worked_examples_are_the_users_most_alike_books_and_never_from_a_folder_kept_from_ai()
    {
        Copy(pdfs.KnownText, "Fey Court/Known Text.pdf");
        Image("Fey Court/Thorns.png", 640);
        Image("Other/Glimmer.png", 641);
        var keptFolder = Path.Combine(_dir, "Kept Library");
        Directory.CreateDirectory(Path.Combine(keptFolder, "Fey Court"));
        File.WriteAllBytes(Path.Combine(keptFolder, "Fey Court", "Grove.png"), FakeCodec.Png(642, 400));
        await _roots.AddAsync(_library, Ct);
        var kept = await _roots.AddAsync(keptFolder, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();
        var cards = await CardsAsync();
        async Task DecideAsync(string path, string? title)
        {
            if (title is not null) await _metadata.SetAsync(cards[path].Entry, MetadataFields.Title, title, Ct);
            await _metadata.SetAsync(cards[path].Entry, MetadataFields.System, "Pathfinder", Ct);
        }
        await DecideAsync("Fey Court/Known Text.pdf", null);
        await DecideAsync("Fey Court/Thorns.png", "Thorn Bargain");
        await DecideAsync("Other/Glimmer.png", "Glimmer Rules");
        await DecideAsync("Fey Court/Grove.png", "Hidden Grove");
        var inputs = new ClassifierInputs(_libraryStore, _queries, _vocabulary, _entries, new ExampleFinder(_metadataStore, _libraryStore));
        var book = cards["Fey Court/Known Text.pdf"].Document;

        var (input, _) = await inputs.BuildAsync(book, new HashSet<long> { kept.Id }, ct: Ct);

        // Same folder first, then the same system; the kept folder's book, though as alike, is left out.
        Assert.NotNull(input);
        Assert.Equal(["Thorn Bargain", "Glimmer Rules"], input.Examples!.Select(e => e.Values.First(v => v.Field == "title").Value));
        var user = ClassifierPrompt.UserMessage(input.Excerpt, input.FileName, input.Examples);
        Assert.Contains("- title: Thorn Bargain; system: Pathfinder", user, StringComparison.Ordinal);
        Assert.DoesNotContain("Hidden Grove", user, StringComparison.Ordinal);

        var (everyone, _) = await inputs.BuildAsync(book, ct: Ct);
        Assert.Contains(everyone!.Examples!, e => e.Values.Contains(("title", "Hidden Grove")));
        // The pilot reads the same thing whatever the user has confirmed since.
        var (pilot, _) = await inputs.BuildAsync(book, withExamples: false, ct: Ct);
        Assert.Empty(pilot!.Examples!);
    }
}
