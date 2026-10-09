using Bibliotaph.Core;
using Microsoft.Data.Sqlite;

namespace Bibliotaph.Index.Tests;

/// <summary>A fresh in-memory index.db with a running writer, kept alive by one open connection.</summary>
public abstract class IndexFixture : IAsyncLifetime
{
    protected IndexDatabase Database { get; } = IndexDatabase.InMemory($"index-{Guid.NewGuid():N}");
    protected SqliteConnection Connection { get; private set; } = null!;
    protected IndexWriter Writer { get; private set; } = null!;
    internal ManualClock Clock { get; } = new(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));

    public virtual async ValueTask InitializeAsync()
    {
        Connection = new SqliteConnection(Database.ConnectionString);
        Connection.Open();
        await Database.InitializeAsync();
        Writer = new IndexWriter(Database);
        await Writer.StartAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The whole-document entry these tests give document <paramref name="documentId"/>. Its id is well above the
    /// document's, so a query that mixes the two up fails.
    /// </summary>
    internal static EntryId EntryOf(long documentId) => new(documentId + 100);

    /// <summary>Records each document's whole-document entry in entry_doc, as Probe does.</summary>
    internal static Task AddEntriesAsync(IndexStore store, params long[] documentIds) =>
        store.SetEntriesAsync([.. documentIds.Select(id => new EntryDocRow(EntryOf(id), id, EntryKind.Whole))], TestContext.Current.CancellationToken);

    public virtual async ValueTask DisposeAsync()
    {
        Writer.Dispose();
        await Connection.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
