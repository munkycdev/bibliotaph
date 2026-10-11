using Bibliotaph.Core;

namespace Bibliotaph.Catalog.Tests;

/// <summary>Easier corrections in a real catalog.db (scenario review, row 8): waiting corrections, folder rules, and the books in a folder.</summary>
public sealed class CorrectionStoreTests : IAsyncLifetime
{
    static readonly DateTime Monday = new(2026, 10, 5, 9, 30, 0, DateTimeKind.Utc);
    readonly string _dir = Directory.CreateTempSubdirectory("bibliotaph-corrections-").FullName;
    readonly MetadataStoreTests.SteppingClock _clock = new(new DateTimeOffset(2026, 10, 11, 9, 0, 0, TimeSpan.Zero));
    CatalogDatabase _database = null!;
    Factory _contexts = null!;
    CorrectionStore _store = null!;
    LibraryStore _library = null!;
    SourceRootStore _roots = null!;

    public async ValueTask InitializeAsync()
    {
        _database = new CatalogDatabase(Path.Combine(_dir, "catalog.db"), Path.Combine(_dir, "backups"));
        await _database.MigrateAsync();
        _contexts = new Factory(_database);
        _store = new CorrectionStore(_contexts, _clock);
        _library = new LibraryStore(_contexts);
        _roots = new SourceRootStore(_contexts);
    }

    public ValueTask DisposeAsync()
    {
        _database.ClearPool();
        Directory.Delete(_dir, recursive: true);
        return ValueTask.CompletedTask;
    }

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    async Task<long> RootAsync(string name = "Library") => (await _roots.AddAsync(Path.Combine(_dir, name), Ct)).Id;

    /// <summary>Files in a library folder, each hashed as a book of its own; returns each file's card by its path.</summary>
    async Task<Dictionary<string, EntryId>> BooksAsync(long root, params string[] paths)
    {
        await _library.ReconcileRootAsync(root, [.. paths.Select(p => new ScannedFile(p, 1000, Monday, false))], ct: Ct);
        var entries = new EntryStore(_contexts);
        var cards = new Dictionary<string, EntryId>();
        var hash = 'a';
        foreach (var file in await _library.NextUnhashedAsync(100, includeOnlineOnly: true, Ct))
        {
            var (documentId, _) = (await _library.AttachHashAsync(file, ContentHash.Parse(new string(hash++, 64)), Ct))!.Value;
            cards[paths.Single(p => file.FullPath.EndsWith(p, StringComparison.Ordinal))] = (await entries.GetEntryAsync(documentId, Ct))!.EntryId;
        }
        return cards;
    }

    [Fact]
    public async Task A_correction_left_unfinished_waits_once_until_it_is_cleared_and_undo_brings_it_back_as_it_was()
    {
        var book = await TestEntries.AddAsync(_database, 'a');

        Assert.True(await _store.RequestAsync(book, Ct));
        Assert.False(await _store.RequestAsync(book, Ct));
        var (entryId, created) = Assert.Single(await _store.GetRequestsAsync(Ct));
        Assert.Equal(book, entryId);

        Assert.Equal(created, await _store.ClearAsync(book, Ct));
        Assert.Empty(await _store.GetRequestsAsync(Ct));
        Assert.Null(await _store.ClearAsync(book, Ct));

        await _store.RestoreAsync(book, created, Ct);
        Assert.Equal((book, created), Assert.Single(await _store.GetRequestsAsync(Ct)));
    }

    [Fact]
    public async Task A_card_joined_into_another_has_no_waiting_correction()
    {
        var (kept, joined) = (await TestEntries.AddAsync(_database, 'a'), await TestEntries.AddAsync(_database, 'b'));
        await _store.RequestAsync(joined, Ct);
        await using (var db = _contexts.CreateDbContext())
        {
            (await db.Entries.FindAsync([joined.Value], Ct))!.MergedIntoEntryId = kept.Value;
            await db.SaveChangesAsync(Ct);
        }

        Assert.Empty(await _store.GetRequestsAsync(Ct));
    }

    [Fact]
    public async Task A_folder_rule_is_proposed_once_and_a_declined_one_is_not_proposed_again()
    {
        var root = await RootAsync();
        var folder = new FolderKey(root, "Fey Court");

        Assert.True(await _store.ProposeAsync(folder, "system", "pathfinder", 3, Ct));
        // The same folder however its path is written.
        Assert.False(await _store.ProposeAsync(new FolderKey(root, "fey court"), "system", "pathfinder", 4, Ct));
        var rule = Assert.Single(await _store.GetRulesAsync(FolderRuleState.Proposed, Ct));
        Assert.Equal((folder, "system", "pathfinder", 3), (rule.Folder, rule.Field, rule.Value, rule.Books));

        await _store.SetStateAsync(rule.Id, FolderRuleState.Off, oneValue: true, Ct);
        Assert.False(await _store.ProposeAsync(folder, "system", "pathfinder", 5, Ct));
        Assert.Empty(await _store.GetRulesAsync(FolderRuleState.Proposed, Ct));
        Assert.Equal(FolderRuleState.Off, Assert.Single(await _store.GetRulesAsync(ct: Ct)).State);
        // Another value, or another folder, is a rule of its own.
        Assert.True(await _store.ProposeAsync(folder, "system", "dnd", 3, Ct));
        Assert.True(await _store.ProposeAsync(new FolderKey(root, "Fey Court/Maps"), "system", "pathfinder", 3, Ct));
    }

    [Fact]
    public async Task Switching_on_a_rule_for_a_one_value_field_switches_off_the_folders_other_and_undo_puts_both_back()
    {
        var root = await RootAsync();
        var folder = new FolderKey(root, "Fey Court");
        await _store.ProposeAsync(folder, "system", "dnd", 3, Ct);
        await _store.ProposeAsync(folder, "system", "pathfinder", 3, Ct);
        await _store.ProposeAsync(folder, "types", "adventure", 3, Ct);
        var rules = (await _store.GetRulesAsync(ct: Ct)).ToDictionary(r => r.Value, r => r.Id);
        await _store.SetStateAsync(rules["dnd"], FolderRuleState.On, oneValue: true, Ct);
        await _store.SetStateAsync(rules["adventure"], FolderRuleState.On, oneValue: false, Ct);

        var was = await _store.SetStateAsync(rules["pathfinder"], FolderRuleState.On, oneValue: true, Ct);

        Assert.Equal(2, was.Count);
        Assert.Equal(["adventure", "pathfinder"], (await _store.GetRulesAsync(FolderRuleState.On, Ct)).Select(r => r.Value).Order());
        await _store.RestoreAsync(was, Ct);
        Assert.Equal(["adventure", "dnd"], (await _store.GetRulesAsync(FolderRuleState.On, Ct)).Select(r => r.Value).Order());
        Assert.Equal("pathfinder", Assert.Single(await _store.GetRulesAsync(FolderRuleState.Proposed, Ct)).Value);
    }

    [Fact]
    public async Task A_removed_library_folders_rules_are_not_listed()
    {
        var root = await RootAsync();
        await _store.ProposeAsync(new FolderKey(root, "Fey Court"), "system", "pathfinder", 3, Ct);

        await _roots.RemoveAsync(root, Ct);

        Assert.Empty(await _store.GetRulesAsync(ct: Ct));
    }

    [Fact]
    public async Task The_books_from_a_folder_are_those_with_a_file_directly_in_it()
    {
        var root = await RootAsync();
        var other = await RootAsync("Other");
        var cards = await BooksAsync(root, "Fey Court/Primer.pdf", "Fey Court/Masks.pdf", "Fey Court/Maps/Glade.pdf", "Fey Courtyard/Gate.pdf", "Top.pdf");
        await BooksAsync(other, "Fey Court/Elsewhere.pdf");
        var folder = new FolderKey(root, "Fey Court");

        Assert.Equal([cards["Fey Court/Primer.pdf"], cards["Fey Court/Masks.pdf"]],
            (await _library.GetEntriesInFolderAsync(folder, Ct)).OrderBy(e => e.Value == cards["Fey Court/Masks.pdf"].Value));
        Assert.Equal([cards["Top.pdf"]], await _library.GetEntriesInFolderAsync(new FolderKey(root, ""), Ct));
        // A rule's folder holds its subfolders too, but not a folder whose name only starts the same.
        Assert.Equal(3, (await _library.GetDocumentsUnderAsync(folder, Ct)).Count);
        Assert.Equal(5, (await _library.GetDocumentsUnderAsync(new FolderKey(root, ""), Ct)).Count);

        var home = Assert.Single(await _library.GetEntryFoldersAsync([cards["Fey Court/Maps/Glade.pdf"]], Ct));
        Assert.Equal((new FolderKey(root, "Fey Court/Maps"), true, true), (home.Folder, home.IsCurrent, home.Present));
        Assert.Equal("Maps", home.Folder.Name);
    }
}
