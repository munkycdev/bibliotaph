using Bibliotaph.Core;
using Bibliotaph.Index;
using Microsoft.Data.Sqlite;

namespace Bibliotaph.Processing.Tests;

/// <summary>Finding the ISBNs and product codes a book prints, and what follows when it prints neither.</summary>
public sealed class IdentifyTests : IAsyncLifetime
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    readonly IndexDatabase _database = IndexDatabase.InMemory($"identify-{Guid.NewGuid():N}");
    SqliteConnection _keepAlive = null!;
    IndexWriter _writer = null!;
    IndexStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _keepAlive = new SqliteConnection(_database.ConnectionString);
        _keepAlive.Open();
        await _database.InitializeAsync();
        _writer = new IndexWriter(_database);
        await _writer.StartAsync(Ct);
        _store = new IndexStore(_writer);
    }

    public async ValueTask DisposeAsync()
    {
        _writer.Dispose();
        await _keepAlive.DisposeAsync();
    }

    async Task<StageOutcome> IdentifyAsync(long id, string text)
    {
        await _store.UpsertDocumentAsync(new DocRow { DocumentId = id, ContentHash = $"hash{id}", Format = "pdf", DisplayTitle = "The Owlbear Mill", PageCount = 1 },
            [new PageRow(0, null, 612, 792)], [], Ct);
        await _store.SetPageTextAsync(id, [new PageTextRow(0, text, "pdf", 1, false)], Ct);
        // No ISBN is printed in these books, so no other file is looked for by one.
        var stage = new IdentifyStage(_store, new IndexQueries(_database), versions: null!);
        return await stage.RunAsync(new JobRecord(id, id, $"hash{id}", Stage.Identify, Pipeline.Version(Stage.Identify), 1), Ct);
    }

    [Fact]
    public async Task A_book_printing_no_isbn_or_product_code_is_still_looked_up_by_its_title()
    {
        var outcome = await IdentifyAsync(1, "The Owlbear Mill. An adventure for four characters of 3rd level. Gull Rock Games.");

        var done = Assert.IsType<StageOutcome.Done>(outcome);
        Assert.Equal((StageStatus.Skipped, IdentifyStage.NothingPrinted), (done.Status, done.Reason));
        Assert.Equal([Stage.LookUp], done.Next);
    }
}
