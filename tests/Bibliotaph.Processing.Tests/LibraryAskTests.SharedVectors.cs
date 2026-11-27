using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Classification;
using Bibliotaph.Index;
using Dapper;

namespace Bibliotaph.Processing.Tests;

public sealed partial class LibraryAskTests
{
    /// <summary>The web library's store of shared results, in memory: the newest for each file and kind.</summary>
    sealed class Hub : IResultChannel
    {
        readonly Dictionary<(string Hash, string Kind), SharedResult> _held = [];

        public bool IsOn => true;

        public int Sent { get; private set; }

        public Task<SharedResultPage?> ReadResultsAsync(long after, CancellationToken ct) => Task.FromResult<SharedResultPage?>(new SharedResultPage([], after, More: false));

        public Task<bool> WriteResultsAsync(IReadOnlyList<SharedResult> results, CancellationToken ct)
        {
            foreach (var result in results) _held[(result.ContentHash, result.Kind)] = result;
            Sent += results.Count;
            return Task.FromResult(true);
        }

        public Task<SharedResult?> ReadResultAsync(string kind, string contentHash, CancellationToken ct) =>
            Task.FromResult(_held.GetValueOrDefault((contentHash, kind)));
    }

    [Fact]
    public async Task A_books_vectors_made_on_one_device_are_taken_by_another_for_the_pages_whose_text_is_the_same()
    {
        await SetUpAsync("embed-a");
        var hub = new Hub();
        var contexts = new Factory(_catalog);
        var library = new LibraryStore(contexts);
        var store = new IndexStore(_writer);
        var shared = new SharedPageVectors(new AskQueries(_index), library, store, new SharedResultCache(_index, _writer), hub);
        AskService Device() => new(_ai, library, new AskQueries(_index), new IndexQueries(_index), store, new EntryStore(contexts), new AskHistoryStore(contexts),
            _use, sharedVectors: shared);
        Dictionary<int, float[]> PageVectors()
        {
            using var connection = _index.OpenRead();
            return connection.Query<(long Page, byte[] Vector)>("SELECT p.pdf_page, v.vector FROM page_vec v JOIN page p ON p.id = v.page_id WHERE p.document_id = @id",
                new { id = _lich }).ToDictionary(r => (int)r.Page, r => VectorBlob.FromBytes(r.Vector));
        }

        // Made on one device and sent once.
        using (var here = Device()) await here.FillInBackgroundAsync(_lich, () => false, Ct);
        var made = PageVectors();
        var embedded = _server.Embedded.Sum(t => t.Count);
        await shared.PushAsync(Ct);
        await shared.PushAsync(Ct);

        // Another device has the same file, with the last page read differently, and no vectors yet.
        await _writer.WriteAsync((c, t) => c.Execute("DELETE FROM page_vec", transaction: t), Ct);
        await store.SetPageTextAsync(_lich,
        [
            new PageTextRow(0, "The lich hides its phylactery in the crypt beneath the old keep.", "pdf", 1, false),
            new PageTextRow(1, "In the crypt, the lich's phylactery rests on a black altar.", "pdf", 1, false),
            new PageTextRow(2, "The lich fears sunlight, and its crypt has no windows.", "pdf", 1, false),
            new PageTextRow(3, "Crypt guards: skeletons serve the lich in the crypt.", "pdf", 1, false),
            new PageTextRow(4, "The lich keeps a phylactery spellbook in the crypt library.", "pdf", 1, false),
        ], Ct);
        using (var there = Device()) await there.FillInBackgroundAsync(_lich, () => false, Ct);
        var taken = PageVectors();

        Assert.Equal(5, embedded);
        Assert.Equal(1, hub.Sent);
        // Only the page whose text differs was sent to the model there; the rest are the first device's, to half precision.
        Assert.Equal(embedded + 1, _server.Embedded.Sum(t => t.Count));
        Assert.Equal(5, taken.Count);
        foreach (var page in Enumerable.Range(0, 4))
            Assert.All(made[page].Zip(taken[page]), pair => Assert.Equal(pair.First, pair.Second, 0.01f));
    }
}
