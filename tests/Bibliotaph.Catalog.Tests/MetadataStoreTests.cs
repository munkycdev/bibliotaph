using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog.Tests;

/// <summary>Assertions, decisions and the starter vocabulary in a real catalog.db.</summary>
public sealed class MetadataStoreTests : IAsyncLifetime
{
    readonly string _dir = Directory.CreateTempSubdirectory("bibliotaph-metadata-").FullName;
    readonly SteppingClock _clock = new(new DateTimeOffset(2026, 10, 9, 9, 0, 0, TimeSpan.Zero));
    CatalogDatabase _database = null!;
    Factory _contexts = null!;
    MetadataStore _metadata = null!;
    VocabularyStore _vocabulary = null!;
    long _document;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = new CatalogDatabase(Path.Combine(_dir, "catalog.db"), Path.Combine(_dir, "backups"));
        await _database.MigrateAsync();
        _contexts = new Factory(_database);
        _metadata = new MetadataStore(_contexts, _clock);
        _vocabulary = new VocabularyStore(_contexts, _clock);
        await using var db = _database.CreateContext();
        var document = db.Documents.Add(new Document { ContentHash = new string('a', 64), Format = "pdf", CreatedUtc = DateTime.UtcNow }).Entity;
        await db.SaveChangesAsync();
        _document = document.Id;
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
        return ValueTask.CompletedTask;
    }

    static MetadataProposal Hint(MetadataField field, string value, AssertionOrigin origin = AssertionOrigin.Folder, string? quote = null) =>
        new(field, value, origin, quote);

    async Task<EffectiveMetadata> EffectiveAsync() => (await _metadata.GetAsync(_document, Ct)).Compute();

    async Task<List<Assertion>> RowsAsync()
    {
        await using var db = _database.CreateContext();
        return await db.Assertions.AsNoTracking().Where(a => a.DocumentId == _document).OrderBy(a => a.Id).ToListAsync(Ct);
    }

    [Fact]
    public async Task Rule_hints_are_stored_as_provisional_suggestions_with_their_evidence()
    {
        Assert.True(await _metadata.ReplaceHintsAsync(_document,
            [Hint(MetadataFields.Edition, "dnd-5e", quote: "D&D 5e"), Hint(MetadataFields.Title, "Tomb", AssertionOrigin.Filename, "Tomb.pdf")], Ct));

        var edition = (await EffectiveAsync())[MetadataFields.Edition].First!;
        Assert.Equal("dnd-5e", edition.Value);
        Assert.False(edition.Confirmed);
        Assert.Equal(AssertionOrigin.Folder, edition.Origin);
        Assert.Equal("D&D 5e", edition.Source.Quote);
    }

    [Fact]
    public async Task Rerunning_hints_keeps_unchanged_rows_and_replaces_only_provisional_ones()
    {
        await _metadata.ReplaceHintsAsync(_document, [Hint(MetadataFields.Edition, "dnd-5e"), Hint(MetadataFields.Types, "adventure")], Ct);
        var before = await RowsAsync();
        _clock.Step();

        Assert.False(await _metadata.ReplaceHintsAsync(_document, [Hint(MetadataFields.Edition, "dnd-5e"), Hint(MetadataFields.Types, "adventure")], Ct));
        Assert.Equal(before.Select(r => (r.Id, r.CreatedUtc)), (await RowsAsync()).Select(r => (r.Id, r.CreatedUtc)));

        Assert.True(await _metadata.ReplaceHintsAsync(_document, [Hint(MetadataFields.Edition, "dnd-5e")], Ct));
        Assert.Equal(["edition"], (await RowsAsync()).Select(r => r.Field));
    }

    [Fact]
    public async Task Hints_cannot_touch_other_origins()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _metadata.ReplaceHintsAsync(_document, [Hint(MetadataFields.Title, "x", AssertionOrigin.Ai)], Ct));
    }

    [Fact]
    public async Task A04_a_corrected_field_and_a_rejected_value_survive_the_hints_running_again()
    {
        // Folder names said 3.5 and an Adventure; the user corrects the edition to 5e and removes the type.
        await _metadata.ReplaceHintsAsync(_document, [Hint(MetadataFields.Edition, "dnd-35"), Hint(MetadataFields.Types, "adventure")], Ct);
        await _metadata.SetValuesAsync(_document, MetadataFields.Edition, ["dnd-5e"], Ct);
        await _metadata.SetValuesAsync(_document, MetadataFields.Types, [], Ct);

        // A reindex proposes the same things again, and more.
        _clock.Step();
        await _metadata.ReplaceHintsAsync(_document,
            [Hint(MetadataFields.Edition, "dnd-35"), Hint(MetadataFields.Types, "adventure"), Hint(MetadataFields.Themes, "horror")], Ct);

        var metadata = await EffectiveAsync();
        Assert.Equal("dnd-5e", metadata[MetadataFields.Edition].First!.Value);
        Assert.True(metadata[MetadataFields.Edition].First!.Confirmed);
        Assert.False(metadata[MetadataFields.Types].IsKnown);
        Assert.Equal("horror", metadata[MetadataFields.Themes].First!.Value);
        Assert.False(metadata.NeedsReview);
    }

    [Fact]
    public async Task Typing_a_suggested_value_confirms_that_suggestion_and_keeps_its_evidence()
    {
        await _metadata.ReplaceHintsAsync(_document, [Hint(MetadataFields.System, "dnd", quote: "D&D")], Ct);

        await _metadata.SetValuesAsync(_document, MetadataFields.System, ["dnd"], Ct);

        var row = Assert.Single(await RowsAsync());
        Assert.Equal((AssertionState.Confirmed, AssertionOrigin.Folder, "D&D"), (row.State, row.Origin, row.EvidenceQuote));
        Assert.NotNull(row.DecidedUtc);
    }

    [Fact]
    public async Task A_new_value_for_a_single_field_supersedes_the_old_confirmation()
    {
        await _metadata.SetValuesAsync(_document, MetadataFields.Title, ["First"], Ct);
        _clock.Step();
        await _metadata.SetValuesAsync(_document, MetadataFields.Title, ["Second"], Ct);

        Assert.Equal([AssertionState.Superseded, AssertionState.Confirmed], (await RowsAsync()).Select(r => r.State));
        Assert.Equal("Second", (await EffectiveAsync())[MetadataFields.Title].First!.Value);
    }

    [Fact]
    public async Task Clearing_a_single_field_rejects_its_value_and_shows_the_next_suggestion()
    {
        await _metadata.ReplaceHintsAsync(_document,
            [Hint(MetadataFields.Title, "Junk Title", AssertionOrigin.Embedded), Hint(MetadataFields.Title, "Tomb", AssertionOrigin.Filename)], Ct);

        await _metadata.SetValuesAsync(_document, MetadataFields.Title, [], Ct);

        Assert.Equal("Tomb", (await EffectiveAsync())[MetadataFields.Title].First!.Value);
    }

    [Fact]
    public async Task Editing_a_multi_value_field_confirms_kept_values_rejects_removed_ones_and_adds_new_ones()
    {
        await _metadata.ReplaceHintsAsync(_document, [Hint(MetadataFields.Themes, "horror"), Hint(MetadataFields.Themes, "mystery")], Ct);

        await _metadata.SetValuesAsync(_document, MetadataFields.Themes, ["horror", "heist"], Ct);

        var themes = (await EffectiveAsync())[MetadataFields.Themes];
        Assert.Equal(["heist", "horror"], themes.Values.Select(v => v.Value).Order());
        Assert.All(themes.Values, v => Assert.True(v.Confirmed));
        var rejection = Assert.Single((await _metadata.GetAsync(_document, Ct)).Rejections);
        Assert.Equal(("theme", "mystery"), rejection);
    }

    [Fact]
    public async Task Typing_a_rejected_value_again_lifts_the_rejection()
    {
        await _metadata.ReplaceHintsAsync(_document, [Hint(MetadataFields.Themes, "horror")], Ct);
        await _metadata.RejectAsync(_document, MetadataFields.Themes, "horror", Ct);
        Assert.False((await EffectiveAsync())[MetadataFields.Themes].IsKnown);

        await _metadata.SetValuesAsync(_document, MetadataFields.Themes, ["horror"], Ct);

        Assert.True((await EffectiveAsync())[MetadataFields.Themes].First!.Confirmed);
        Assert.Empty((await _metadata.GetAsync(_document, Ct)).Rejections);
    }

    [Fact]
    public async Task Keep_this_confirms_what_the_field_shows()
    {
        await _metadata.ReplaceHintsAsync(_document, [Hint(MetadataFields.Edition, "dnd-5e"), Hint(MetadataFields.Edition, "dnd-35", AssertionOrigin.Embedded)], Ct);
        Assert.True((await EffectiveAsync()).NeedsReview);

        await _metadata.ConfirmAsync(_document, MetadataFields.Edition, Ct);

        var metadata = await EffectiveAsync();
        Assert.Equal("dnd-35", metadata[MetadataFields.Edition].First!.Value);
        Assert.True(metadata[MetadataFields.Edition].First!.Confirmed);
        Assert.False(metadata.NeedsReview);
    }

    [Fact]
    public async Task Reset_forgets_the_users_decisions_about_a_field()
    {
        await _metadata.ReplaceHintsAsync(_document, [Hint(MetadataFields.Themes, "horror"), Hint(MetadataFields.Themes, "mystery")], Ct);
        await _metadata.SetValuesAsync(_document, MetadataFields.Themes, ["heist"], Ct);

        await _metadata.ResetAsync(_document, MetadataFields.Themes, Ct);

        var themes = (await EffectiveAsync())[MetadataFields.Themes];
        Assert.Equal(["horror", "mystery"], themes.Values.Select(v => v.Value).Order());
        Assert.All(themes.Values, v => Assert.False(v.Confirmed));
    }

    [Fact]
    public async Task The_starter_vocabulary_is_seeded_once_and_never_overwrites_a_users_change()
    {
        Assert.True(await _vocabulary.SeedAsync(Ct) > 100);
        await using (var db = _database.CreateContext())
        {
            var dnd = await db.VocabularyTerms.SingleAsync(t => t.Vocabulary == "system" && t.Key == "dnd", Ct);
            dnd.Label = "My D&D";
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(0, await new VocabularyStore(_contexts).SeedAsync(Ct));

        var vocabulary = await new VocabularyStore(_contexts).GetAsync(Ct);
        Assert.Equal("My D&D", vocabulary.Label("system", "dnd"));
        Assert.Equal("dnd-5e", vocabulary.Resolve("edition", "D&D 5e")?.Key);
        Assert.Equal("dnd", vocabulary.Find("edition", "dnd-5e")?.ParentKey);
    }

    [Fact]
    public async Task A_term_the_user_types_is_added_once_with_a_stable_key()
    {
        await _vocabulary.SeedAsync(Ct);

        var first = await _vocabulary.ResolveOrAddAsync("setting", "The Witchlight Marches", Ct);
        var again = await _vocabulary.ResolveOrAddAsync("setting", "the witchlight marches", Ct);
        var known = await _vocabulary.ResolveOrAddAsync("system", "DnD", Ct);

        Assert.Equal("the-witchlight-marches", first.Key);
        Assert.Equal(first.Key, again.Key);
        Assert.Equal("dnd", known.Key);
    }

    [Fact]
    public async Task Folder_labels_switch_off_and_on()
    {
        Assert.True(await _vocabulary.SetFolderLabelEnabledAsync("D&D 5e", "edition", "dnd-5e", enabled: false, Ct));
        Assert.False(await _vocabulary.SetFolderLabelEnabledAsync("d&d  5E", "edition", "dnd-5e", enabled: false, Ct));
        Assert.Contains(("d&d 5e", "edition", "dnd-5e"), await _vocabulary.GetIgnoredFolderLabelsAsync(Ct));

        Assert.True(await _vocabulary.SetFolderLabelEnabledAsync("D&D 5e", "edition", "dnd-5e", enabled: true, Ct));
        Assert.Empty(await _vocabulary.GetIgnoredFolderLabelsAsync(Ct));
    }

    [Fact]
    public async Task Typing_a_different_type_replaces_the_suggested_one()
    {
        await _metadata.ReplaceHintsAsync(_document, [Hint(MetadataFields.Types, "adventure")], Ct);
        _clock.Step();

        await _metadata.SetValuesAsync(_document, MetadataFields.Types, ["bestiary"], Ct);

        var types = (await EffectiveAsync())[MetadataFields.Types];
        Assert.Equal("bestiary", Assert.Single(types.Values).Value);
        Assert.True(types.Values[0].Confirmed);
    }

    /// <summary>A clock that moves on a minute each time it is read, so every write is later than the last.</summary>
    static MetadataProposal Ai(MetadataField field, string value, string quote = "a quote from the page", int page = 3) =>
        new(field, value, AssertionOrigin.Ai, quote, [page]);

    [Fact]
    public async Task A_run_replaces_its_origins_undecided_suggestions_and_leaves_decisions_alone()
    {
        await _metadata.ApplyRunAsync(_document, "run1", AssertionOrigin.Ai,
            [Ai(MetadataFields.Title, "The Sunken Lantern"), Ai(MetadataFields.Year, "2019"), Ai(MetadataFields.Types, "adventure"), Ai(MetadataFields.Authors, "Ana Ruiz")], Ct);
        var first = await RowsAsync();
        await _metadata.ConfirmAsync(_document, MetadataFields.Authors, Ct);
        await _metadata.RejectAsync(_document, MetadataFields.Year, "2019", Ct);

        // A later run: the title again with new evidence, the year again, a new type, and no author.
        await _metadata.ApplyRunAsync(_document, "run2", AssertionOrigin.Ai,
            [Ai(MetadataFields.Title, "The Sunken Lantern", "THE SUNKEN LANTERN", 0), Ai(MetadataFields.Year, "2019"), Ai(MetadataFields.Types, "bestiary")], Ct);

        var rows = await RowsAsync();
        var title = Assert.Single(rows, r => r.Field == "title");
        Assert.Equal(("run2", "THE SUNKEN LANTERN", "[0]"), (title.RunId, title.EvidenceQuote, title.EvidencePagesJson));
        Assert.Equal(first.Single(r => r.Field == "title").CreatedUtc, title.CreatedUtc); // same suggestion, same age
        Assert.Equal(AssertionState.Confirmed, Assert.Single(rows, r => r.Field == "authors").State); // decided, so kept
        Assert.Equal(["bestiary"], rows.Where(r => r.Field == "type").Select(r => r.NormalizedValue));
        Assert.False((await EffectiveAsync())[MetadataFields.Year].IsKnown); // rejected stays rejected
    }

    [Fact]
    public async Task A_run_never_writes_hints_or_user_values()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _metadata.ApplyRunAsync(_document, "run", AssertionOrigin.Ai, [Hint(MetadataFields.Title, "Tomb", AssertionOrigin.User)], Ct));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _metadata.ApplyRunAsync(_document, "run", AssertionOrigin.Folder, [Hint(MetadataFields.Title, "Tomb")], Ct));
    }

    [Fact]
    public async Task Classification_runs_say_what_has_been_read_and_by_which_model()
    {
        var runs = new ClassificationStore(_contexts, _clock);
        var hash = new string('a', 64);
        await runs.RecordAsync(new RunRecord(_document, hash, "ollama", "model-a", 1, 1, [0, 1, 2], _clock.GetUtcNow().UtcDateTime, "The answer wasn't JSON."), Ct);
        Assert.False(await runs.HasRunAsync(hash, "model-a", 1, Ct)); // a failed run doesn't count

        await runs.RecordAsync(new RunRecord(_document, hash, "ollama", "model-a", 1, 1, [0, 1, 2], _clock.GetUtcNow().UtcDateTime, ClassificationStore.Complete), Ct);

        Assert.True(await runs.HasRunAsync(hash, "model-a", 1, Ct));
        Assert.False(await runs.HasRunAsync(hash, "model-a", 2, Ct));
        Assert.False(await runs.HasRunAsync(hash, "model-b", 1, Ct));
        Assert.Equal(new ClassificationSummary(1, 0), await runs.SummarizeAsync("model-a", 1, Ct));
        Assert.Equal(new ClassificationSummary(0, 1), await runs.SummarizeAsync("model-b", 1, Ct));
    }

    internal sealed class SteppingClock(DateTimeOffset start) : TimeProvider
    {
        DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now = _now.AddMinutes(1);

        public void Step() => _now = _now.AddHours(1);
    }
}
