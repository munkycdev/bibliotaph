using Bibliotaph.App.Services;
using Bibliotaph.Core;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;
using Dapper;

namespace Bibliotaph.Processing.Tests;

/// <summary>Slice 4i: locked and protected files, and forgetting a book's text.</summary>
public sealed partial class PipelineTests
{
    /// <summary>Passwords indexing may use, as the app lends it one entered this sitting or remembered.</summary>
    sealed class TestPasswords : IPasswordStore
    {
        readonly Dictionary<string, string> _passwords = [];

        public void Add(string contentHash, string password)
        {
            lock (_passwords) _passwords[contentHash] = password;
        }

        public string? Find(string contentHash)
        {
            lock (_passwords) return _passwords.GetValueOrDefault(contentHash);
        }
    }

    /// <summary>The library's one card.</summary>
    async Task<LibraryEntry> EntryAsync() => Assert.Single(await new LibraryQueries(_index).ListAsync(new LibraryFilter(), ct: Ct));

    async Task<int> PagesFoundAsync(string words)
    {
        var filter = new LibraryFilter(await _libraryStore.GetVisibleEntryIdsAsync(ct: Ct));
        var hits = await new LibraryQueries(_index).SearchPagesAsync(SearchPlan.From(SearchQuery.Parse(words)), filter, ct: Ct);
        return hits.Entries.Sum(e => e.Pages.Count);
    }

    [Fact]
    public async Task A_locked_book_says_so_until_a_password_is_lent_then_its_text_is_searchable_but_not_copyable()
    {
        Copy(pdfs.LockedNoCopying, "Locked Book.pdf");
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        var progress = await SettleAsync();

        var locked = await EntryAsync();
        Assert.Equal(TextAccess.Locked, locked.TextAccess);
        Assert.False(locked.Searchable);
        Assert.Equal(1, progress.Withheld);
        var attention = Assert.Single(await _queries.GetAttentionAsync(Ct));
        Assert.Equal(TextAccess.Locked, TextAccessReasons.Of(attention.Status, attention.Reason));

        // Unlocked in the reader with "Make its text searchable" ticked: the app lends indexing the password and retries.
        _passwords.Add((await _libraryStore.GetSourceAsync(locked.DocumentId, Ct))!.ContentHash, SmokePdfs.Password);
        await _service.RetryAsync(locked.DocumentId, Ct);
        progress = await SettleAsync();

        var unlocked = await EntryAsync();
        Assert.Equal(TextAccess.Readable, unlocked.TextAccess);
        Assert.True(unlocked.Searchable);
        Assert.Equal(0, progress.Withheld);
        Assert.Equal(1, await PagesFoundAsync("lantern locked gate"));
        // Read for search, though the file forbids copying; the reader keeps Copy off by the flag.
        var details = (await new LibraryQueries(_index).GetDetailsAsync(unlocked.EntryId, Ct))!;
        Assert.False(details.CanCopy);
        Assert.True(details.Encrypted);
        Assert.Empty(await _queries.GetAttentionAsync(Ct));
    }

    [Fact]
    public async Task A_file_with_drm_is_protected_keeps_its_card_and_is_never_called_searchable()
    {
        Copy(pdfs.Protected, "Protected Book.pdf");
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        var progress = await SettleAsync();

        var entry = await EntryAsync();
        Assert.Equal(TextAccess.Protected, entry.TextAccess);
        Assert.False(entry.Searchable);
        Assert.Equal((1, 0, 1), (progress.Documents, progress.Searchable, progress.Withheld));
        var attention = Assert.Single(await _queries.GetAttentionAsync(Ct));
        Assert.Equal(TextAccess.Protected, TextAccessReasons.Of(attention.Status, attention.Reason));

        // Its details stay editable.
        Assert.Null(await _metadata.SetAsync(entry.EntryId, Core.Metadata.MetadataFields.Publisher, "Smoke Press", Ct));
        Assert.Equal("Smoke Press", (await EntryAsync()).Publisher);

        // Trying again changes nothing: no password opens it.
        await _service.RetryAsync(entry.DocumentId, Ct);
        await SettleAsync();
        Assert.Equal(TextAccess.Protected, (await EntryAsync()).TextAccess);
    }

    [Fact]
    public async Task Forgetting_a_books_text_removes_what_reading_it_made_until_it_is_read_again()
    {
        Copy(pdfs.KnownText, "Known Text.pdf");
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();
        var entry = await EntryAsync();
        var cover = Path.Combine(_paths.Cache, "covers", entry.Cover!);
        Assert.True(File.Exists(cover));
        Assert.Equal(1, await PagesFoundAsync("owlbear"));
        var forget = new ForgetText(_entries, _libraryStore, _indexStore, _service, new CoverCache(_paths), _runs, _metadataStore, _projector);

        Assert.True(await forget.ForgetAsync(entry.EntryId, Ct));

        var forgotten = await EntryAsync();
        Assert.Equal(TextAccess.Forgotten, forgotten.TextAccess);
        Assert.Null(forgotten.Cover);
        Assert.False(File.Exists(cover));
        Assert.Equal(0, await PagesFoundAsync("owlbear"));
        Assert.True(await _libraryStore.IsTextForgottenAsync(entry.DocumentId, Ct));
        await using (var c = _index.OpenRead())
        {
            Assert.Equal(0, c.ExecuteScalar<long>("SELECT count(*) FROM page WHERE document_id = @id", new { id = entry.DocumentId }));
            Assert.Equal(0, c.ExecuteScalar<long>("SELECT count(*) FROM outline WHERE document_id = @id", new { id = entry.DocumentId }));
        }
        // The file is never touched, and the card stays with its details.
        Assert.True(File.Exists(Path.Combine(_library, "Known Text.pdf")));

        // Nothing reads it again by itself: not a rescan, not Reprocess.
        await _service.ReprocessAsync(entry.DocumentId, ct: Ct);
        _service.RequestScan();
        var progress = await SettleAsync();
        Assert.Equal(TextAccess.Forgotten, (await EntryAsync()).TextAccess);
        Assert.Equal(0, await PagesFoundAsync("owlbear"));
        Assert.Equal((0, 1), (progress.Searchable, progress.Withheld));

        await forget.ReadAgainAsync(entry.EntryId, Ct);
        await SettleAsync();

        var read = await EntryAsync();
        Assert.Equal(TextAccess.Readable, read.TextAccess);
        Assert.True(read.Searchable);
        Assert.NotNull(read.Cover);
        Assert.Equal(1, await PagesFoundAsync("owlbear"));
        Assert.False(await _libraryStore.IsTextForgottenAsync(entry.DocumentId, Ct));
    }
}
