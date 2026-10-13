using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Processing.Tests;

/// <summary>
/// Books for collections through the real pipeline: books outside a collection that share what most of its books share
/// are proposed in Needs review, accepted with Undo, put off with Not now and declined with Never.
/// </summary>
public sealed partial class PipelineTests
{
    CollectionSuggestions Suggestions() => new(_collections, _collectionsService, _metadataStore, _libraryStore, _queries);

    ReviewService SuggestionReview(CollectionSuggestions suggestions) => new(_metadataStore, _vocabulary, _metadata, _projector, _queries, _settings,
        new VocabularyService(_vocabulary, _projector, _service, _hints), _copies, _packs, suggestions: suggestions);

    static async Task<IReadOnlyList<CollectionSuggestion>> SuggestedAsync(ReviewService review) => (await review.GetQueueAsync(Ct)).Suggestions!;

    [Fact]
    public void What_most_books_share_is_a_value_held_by_three_fifths_of_them_and_by_three_at_least()
    {
        static IReadOnlyList<IEnumerable<string>> Books(params string[][] values) => values;
        // Three of five is enough; three of six isn't; two of three never is.
        Assert.Equal("ember", CollectionSuggestions.MostShared(Books(["ember"], ["ember"], ["ember"], ["ash"], [])));
        Assert.Null(CollectionSuggestions.MostShared(Books(["ember"], ["ember"], ["ember"], ["ash"], [], [])));
        Assert.Null(CollectionSuggestions.MostShared(Books(["ember"], ["ember"], ["ash"])));
        // A book counts once for a value, and the value most books hold wins.
        Assert.Equal("ash", CollectionSuggestions.MostShared(Books(["ash", "ash", "ember"], ["ash", "ember"], ["ash", "ember"], ["ash"])));
        Assert.Null(CollectionSuggestions.MostShared(Books()));
    }

    [Fact]
    public async Task Books_sharing_what_most_of_a_collections_books_share_are_suggested_for_it_and_Never_is_kept_for_those_books()
    {
        string[] names = ["A", "B", "C", "D", "E", "F", "G", "H"];
        for (var i = 0; i < names.Length; i++) Image($"Shelf/{names[i]}.png", 760 + i);
        Image("Other/Lone.png", 770);
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();
        var cards = await CardsAsync();
        EntryId Book(string name) => cards[$"Shelf/{name}.png"].Entry;
        foreach (var name in new[] { "A", "B", "C", "E", "F" }) Assert.Null(await _metadata.SetAsync(Book(name), MetadataFields.Series, "Ember Saga", Ct));
        Assert.Null(await _metadata.SetAsync(Book("G"), MetadataFields.Series, "Ash Chronicle", Ct));
        // Most of a second collection's books are set in the Forgotten Realms for Pathfinder: books with both are suggested.
        foreach (var name in new[] { "D", "G", "H" })
        {
            Assert.Null(await _metadata.SetAsync(Book(name), MetadataFields.Settings, "Forgotten Realms", Ct));
            Assert.Null(await _metadata.SetAsync(Book(name), MetadataFields.System, "Pathfinder", Ct));
        }
        var lone = cards["Other/Lone.png"].Entry;
        Assert.Null(await _metadata.SetAsync(lone, MetadataFields.Settings, "Forgotten Realms", Ct));

        var embers = await _collectionsService.CreateAsync("Embers", ct: Ct);
        // Three of four books in the series: most of them.
        await _collectionsService.AddAsync(embers.Id, [Book("A"), Book("B"), Book("C"), Book("D")], Ct);
        var realms = await _collectionsService.CreateAsync("Realms", ct: Ct);
        await _collectionsService.AddAsync(realms.Id, [Book("D"), Book("G"), Book("H"), Book("A")], Ct);
        // Two books aren't enough to say what a collection's books share.
        var pair = await _collectionsService.CreateAsync("Pair", ct: Ct);
        await _collectionsService.AddAsync(pair.Id, [Book("E"), Book("F")], Ct);
        var suggestions = Suggestions();
        var review = SuggestionReview(suggestions);

        var suggested = await SuggestedAsync(review);
        Assert.Equal(["Embers"], suggested.Select(s => s.Name));
        var ember = suggested[0];
        Assert.Equal(["E", "F"], ember.Books.Select(b => b.Title));
        Assert.Equal(("Ember Saga", null, null), (ember.Series, ember.Setting, ember.System));
        // Most of the Realms' books are in the Forgotten Realms for Pathfinder, but the lone book outside has only the setting.
        var waiting = await review.CountAsync(Ct);

        // Accept adds them, projected into its scope, and Undo takes them out again.
        var undo = await review.AnswerSuggestionAsync(ember, CollectionAnswer.Accept, Ct);
        Assert.Equal(6, Scoped(embers.Id));
        Assert.Empty(await SuggestedAsync(review));
        Assert.Equal(waiting - 1, await review.CountAsync(Ct));
        await undo!();
        Assert.Equal(4, Scoped(embers.Id));

        // Not now waits until the list is made again; Never isn't asked again for those books.
        await review.AnswerSuggestionAsync(ember, CollectionAnswer.NotNow, Ct);
        Assert.Equal(waiting - 1, await review.CountAsync(Ct));
        ember = Assert.Single(await SuggestedAsync(review));
        var never = await review.AnswerSuggestionAsync(ember, CollectionAnswer.Never, Ct);
        Assert.Empty(await SuggestedAsync(review));
        await never!();
        Assert.Single(await SuggestedAsync(review));
        await review.AnswerSuggestionAsync(ember, CollectionAnswer.Never, Ct);

        // A book that joins the series later is asked about, alone.
        Assert.Null(await _metadata.SetAsync(Book("H"), MetadataFields.Series, "Ember Saga", Ct));
        var later = Assert.Single(await SuggestedAsync(review));
        Assert.Equal([Book("H")], later.EntryIds);

        // The Realms' books gain a match once one outside shares both setting and system.
        Assert.Null(await _metadata.SetAsync(lone, MetadataFields.System, "Pathfinder", Ct));
        var realm = (await SuggestedAsync(review)).Single(s => s.Name == "Realms");
        Assert.Equal([lone], realm.EntryIds);
        Assert.Equal(("forgotten-realms", "pathfinder"), (realm.Setting, realm.System));
    }
}
