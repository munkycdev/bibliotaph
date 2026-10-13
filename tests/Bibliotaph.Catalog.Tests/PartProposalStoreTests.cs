using Bibliotaph.Core;

namespace Bibliotaph.Catalog.Tests;

/// <summary>The adventures a file seems to hold, one Needs review card per file, answered by content hash.</summary>
public sealed class PartProposalStoreTests : IAsyncLifetime
{
    static readonly DateTime Monday = new(2026, 10, 5, 9, 30, 0, DateTimeKind.Utc);
    readonly string _dir = Directory.CreateTempSubdirectory("bibliotaph-proposals-").FullName;
    readonly List<ScannedFile> _files = [];
    CatalogDatabase _database = null!;
    Factory _contexts = null!;
    LibraryStore _library = null!;
    EntryStore _entries = null!;
    PartStore _parts = null!;
    PartProposalStore _proposals = null!;
    long _root;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly ProposedPart[] Three =
    [
        new("The Salt Mine", new PartPages(2, 3), "1", "for four characters of 1st level"),
        new("Lantern Night", new PartPages(4, 6), "3-5", "for characters of 3rd to 5th level"),
        new("The Glass Tower", new PartPages(7, 8), "7-9", "levels 7-9"),
    ];

    public async ValueTask InitializeAsync()
    {
        _database = new CatalogDatabase(Path.Combine(_dir, "catalog.db"), Path.Combine(_dir, "backups"));
        await _database.MigrateFromTemplateAsync();
        _contexts = new Factory(_database);
        _library = new LibraryStore(_contexts);
        _entries = new EntryStore(_contexts);
        _parts = new PartStore(_contexts);
        _proposals = new PartProposalStore(_contexts);
        _root = (await new SourceRootStore(_contexts).AddAsync(Path.Combine(_dir, "Library"), Ct)).Id;
    }

    public ValueTask DisposeAsync()
    {
        _database.ClearPool();
        Directory.Delete(_dir, recursive: true);
        return ValueTask.CompletedTask;
    }

    static string Hex(char c) => new(c, 64);

    async Task<(long Document, EntryId Entry)> AddBookAsync(string path, char hash)
    {
        _files.Add(new ScannedFile(path, 1000, Monday, OnlineOnly: false));
        await _library.ReconcileRootAsync(_root, _files, ct: Ct);
        var file = (await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct)).Single(f => f.FullPath.EndsWith(path, StringComparison.Ordinal));
        var document = (await _library.AttachHashAsync(file, ContentHash.Parse(Hex(hash)), Ct))!.Value.DocumentId;
        return (document, (await _entries.GetEntryAsync(document, Ct))!.EntryId);
    }

    [Fact]
    public async Task A_proposal_waits_as_one_card_per_file_until_it_is_answered()
    {
        var (core, file) = await AddBookAsync("Core/compilation.pdf", 'a');
        var (backup, _) = await AddBookAsync("Backup/compilation.pdf", 'b');
        await _entries.JoinAsCopyAsync(core, backup, Ct);

        Assert.True(await _proposals.SaveAsync(Hex('a'), PartProposalSource.Rules, Three, Ct));
        Assert.False(await _proposals.SaveAsync(Hex('a'), PartProposalSource.Rules, Three, Ct));
        Assert.True(await _proposals.SaveAsync(Hex('b'), PartProposalSource.Rules, Three[..2], Ct));

        // Both copies proposed: the card is the one read from the copy the file opens.
        var pending = Assert.Single(await _proposals.GetPendingAsync(Ct));
        Assert.Equal((Hex('a'), core, file, PartProposalSource.Rules), (pending.ContentHash, pending.DocumentId, pending.EntryId, pending.Source));
        Assert.Equal(Three, pending.Parts);

        // Not split, for every copy of the file; another reading of either copy doesn't ask again. Undo puts it back.
        var before = await _proposals.AnswerAsync(file, PartProposalAnswer.NotSplit, Ct);
        Assert.Empty(await _proposals.GetPendingAsync(Ct));
        Assert.True(await _proposals.IsAnsweredAsync(Hex('b'), Ct));
        Assert.False(await _proposals.SaveAsync(Hex('b'), PartProposalSource.Rules, Three, Ct));
        await _proposals.RestoreAsync(before, Ct);
        Assert.Equal(Hex('a'), Assert.Single(await _proposals.GetPendingAsync(Ct)).ContentHash);
        Assert.False(await _proposals.IsAnsweredAsync(Hex('b'), Ct));
    }

    [Fact]
    public async Task A_file_with_adventures_marked_or_a_copy_answered_is_not_asked_about()
    {
        var (document, file) = await AddBookAsync("Core/compilation.pdf", 'a');
        await _proposals.SaveAsync(Hex('a'), PartProposalSource.Rules, Three, Ct);

        var heist = (await _parts.AddAsync(file, new PartSource(document, new PartPages(2, 3)), "The Salt Mine", Ct))!.Value;
        Assert.Empty(await _proposals.GetPendingAsync(Ct));
        await _parts.RemoveAsync(heist, Ct);
        Assert.Single(await _proposals.GetPendingAsync(Ct));

        // A copy with no proposal of its own, answered with its file, is remembered too, and its later proposal waits no more.
        var (backup, _) = await AddBookAsync("Backup/compilation.pdf", 'b');
        await _entries.JoinAsCopyAsync(document, backup, Ct);
        var before = await _proposals.AnswerAsync(file, PartProposalAnswer.Accepted, Ct);
        Assert.Equal([new PartProposalState(Hex('a'), true, null), new PartProposalState(Hex('b'), false, null)], before.OrderBy(s => s.ContentHash));
        Assert.True(await _proposals.IsAnsweredAsync(Hex('b'), Ct));
        await _proposals.RestoreAsync(before, Ct);
        Assert.False(await _proposals.IsAnsweredAsync(Hex('b'), Ct));
        Assert.Single(await _proposals.GetPendingAsync(Ct));
    }

    [Fact]
    public async Task Proposing_nothing_drops_a_waiting_proposal_and_a_model_never_replaces_the_rules()
    {
        await AddBookAsync("Core/compilation.pdf", 'a');
        await _proposals.SaveAsync(Hex('a'), PartProposalSource.Rules, Three, Ct);

        Assert.False(await _proposals.SaveAsync(Hex('a'), PartProposalSource.Ai, Three[..2], Ct));
        Assert.Equal(3, Assert.Single(await _proposals.GetPendingAsync(Ct)).Parts.Count);
        Assert.False(await _proposals.SaveAsync(Hex('a'), PartProposalSource.Ai, [], Ct));
        Assert.True(await _proposals.SaveAsync(Hex('a'), PartProposalSource.Rules, [], Ct));
        Assert.Empty(await _proposals.GetPendingAsync(Ct));

        Assert.True(await _proposals.SaveAsync(Hex('a'), PartProposalSource.Ai, Three[..2], Ct));
        Assert.True(await _proposals.SaveAsync(Hex('a'), PartProposalSource.Rules, Three, Ct));
        Assert.Equal(PartProposalSource.Rules, Assert.Single(await _proposals.GetPendingAsync(Ct)).Source);
    }

    [Fact]
    public async Task A_model_isnt_asked_about_a_copy_already_proposed_or_a_file_whose_proposal_was_answered()
    {
        var (core, file) = await AddBookAsync("Core/compilation.pdf", 'a');
        var (backup, _) = await AddBookAsync("Backup/compilation.pdf", 'b');
        await _entries.JoinAsCopyAsync(core, backup, Ct);
        Assert.False(await _proposals.IsSettledAsync(file, Hex('a'), Ct));

        await _proposals.SaveAsync(Hex('a'), PartProposalSource.Rules, Three, Ct);
        Assert.True(await _proposals.IsSettledAsync(file, Hex('a'), Ct));
        Assert.False(await _proposals.IsSettledAsync(file, Hex('b'), Ct));

        await _proposals.AnswerAsync(file, PartProposalAnswer.NotSplit, Ct);
        Assert.True(await _proposals.IsSettledAsync(file, Hex('b'), Ct));
    }

    [Fact]
    public async Task A_run_over_an_adventures_own_pages_doesnt_count_as_reading_its_file()
    {
        var (document, file) = await AddBookAsync("compilation.pdf", 'a');
        var part = (await _parts.AddAsync(file, new PartSource(document, new PartPages(2, 3)), "The Salt Mine", Ct))!.Value;
        var runs = new ClassificationStore(_contexts);
        static RunRecord Run(EntryId entry) => new(entry, Hex('a'), "ollama", "fake-8b", 1, 1, [2, 3], Monday, ClassificationStore.Complete);

        await runs.RecordAsync(Run(part), Ct);
        Assert.False(await runs.HasRunAsync(Hex('a'), "fake-8b", 1, Ct));
        Assert.True(await runs.HasRunOnAsync(part, "fake-8b", 1, Ct));
        Assert.False(await runs.HasRunOnAsync(part, "fake-14b", 1, Ct));
        Assert.Equal(new ClassificationSummary(0, 0), await runs.SummarizeAsync("fake-8b", 1, Ct));

        await runs.RecordAsync(Run(file), Ct);
        Assert.True(await runs.HasRunAsync(Hex('a'), "fake-8b", 1, Ct));
        Assert.Equal(new ClassificationSummary(1, 0), await runs.SummarizeAsync("fake-8b", 1, Ct));
    }
}
