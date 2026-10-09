using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Microsoft.Data.Sqlite;

namespace Bibliotaph.Catalog.Tests;

/// <summary>Needs review's decisions and Settings > Vocabulary's edits in a real catalog.db.</summary>
public sealed class ReviewStoreTests : IAsyncLifetime
{
    readonly string _dir = Directory.CreateTempSubdirectory("bibliotaph-review-").FullName;
    readonly MetadataStoreTests.SteppingClock _clock = new(new DateTimeOffset(2026, 10, 9, 9, 0, 0, TimeSpan.Zero));
    CatalogDatabase _database = null!;
    MetadataStore _metadata = null!;
    VocabularyStore _vocabulary = null!;
    EntryId _document;
    EntryId _other;

    /// <summary>The content hash suggestions are read from; one copy per entry here.</summary>
    static readonly string Hash = new('a', 64);

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = new CatalogDatabase(Path.Combine(_dir, "catalog.db"), Path.Combine(_dir, "backups"));
        await _database.MigrateAsync();
        var contexts = new Factory(_database);
        _metadata = new MetadataStore(contexts, _clock);
        _vocabulary = new VocabularyStore(contexts, _clock);
        await _vocabulary.SeedAsync(Ct);
        (_document, _other) = (await TestEntries.AddAsync(_database, 'a'), await TestEntries.AddAsync(_database, 'b'));
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
        return ValueTask.CompletedTask;
    }

    static MetadataProposal Hint(MetadataField field, string value, AssertionOrigin origin = AssertionOrigin.Folder) => new(field, value, origin);

    async Task<EffectiveMetadata> EffectiveAsync(EntryId? document = null) => (await _metadata.GetAsync(document ?? _document, Ct)).Compute();

    [Fact]
    public async Task Rejecting_a_conflict_keeps_what_the_field_showed_and_settles_it()
    {
        await _metadata.ReplaceHintsAsync(_document, Hash, [Hint(MetadataFields.Edition, "dnd-5e"), Hint(MetadataFields.Edition, "dnd-35", AssertionOrigin.Embedded)], Ct);
        var issue = Assert.Single(MetadataReview.Find(await EffectiveAsync(), reviewAll: false), i => i.Kind == ReviewKind.Conflict);

        await _metadata.RejectAndKeepAsync(_document, issue.Field, [.. issue.Proposed.Select(p => p.Normalized)], [.. issue.Current.Select(c => c.Value)], Ct);
        _clock.Step();
        await _metadata.ReplaceHintsAsync(_document, Hash, [Hint(MetadataFields.Edition, "dnd-5e"), Hint(MetadataFields.Edition, "dnd-35", AssertionOrigin.Embedded)], Ct);

        var edition = (await EffectiveAsync())[MetadataFields.Edition];
        Assert.Equal("dnd-35", edition.First!.Value);
        Assert.True(edition.First.Confirmed);
        Assert.False(edition.NeedsReview);
        Assert.Contains(("edition", "dnd-5e"), (await _metadata.GetAsync(_document, Ct)).Rejections);
    }

    [Fact]
    public async Task Undo_puts_a_field_back_exactly_as_it_was()
    {
        await _metadata.ReplaceHintsAsync(_document, Hash, [Hint(MetadataFields.Themes, "horror"), Hint(MetadataFields.Themes, "mystery")], Ct);
        await _metadata.RejectAsync(_document, MetadataFields.Themes, "mystery", Ct);
        var before = await _metadata.SnapshotAsync(_document, MetadataFields.Themes, Ct);

        await _metadata.SetValuesAsync(_document, MetadataFields.Themes, ["heist", "mystery"], Ct);
        await _metadata.RestoreAsync(before, Ct);

        var themes = (await EffectiveAsync())[MetadataFields.Themes];
        Assert.Equal("horror", Assert.Single(themes.Values).Value);
        Assert.False(themes.Values[0].Confirmed);
        Assert.Equal([("theme", "mystery")], (await _metadata.GetAsync(_document, Ct)).Rejections);
        Assert.Equal(before, await _metadata.SnapshotAsync(_document, MetadataFields.Themes, Ct), SnapshotComparer.Instance);
    }

    [Fact]
    public async Task A_proposed_name_the_vocabulary_knows_resolves_to_its_term()
    {
        var (term, state) = await _vocabulary.ProposeTermAsync("type", "One shot", Ct);

        Assert.Equal(("adventure", TermState.Active), (term.Key, state));
    }

    [Fact]
    public async Task Values_of_a_new_term_wait_until_the_term_is_decided()
    {
        var (term, state) = await _vocabulary.ProposeTermAsync("type", "Heist Kit", Ct);
        Assert.Equal(TermState.Pending, state);
        Assert.Equal((term.Key, TermState.Pending), ((await _vocabulary.ProposeTermAsync("type", "heist kit", Ct)).Term.Key, TermState.Pending));

        Assert.Equal(1, await _metadata.AddSuggestionsAsync(_document, Hash, [new MetadataProposal(MetadataFields.Types, term.Key, AssertionOrigin.Ai, "a heist kit")], Ct));
        await _metadata.AddSuggestionsAsync(_other, Hash, [new MetadataProposal(MetadataFields.Types, term.Key, AssertionOrigin.Ai)], Ct);

        Assert.False((await EffectiveAsync())[MetadataFields.Types].IsKnown);
        var pending = Assert.Single(await _vocabulary.GetPendingAsync(Ct));
        Assert.Equal(("Heist Kit", 2, "a heist kit"), (pending.Term.Label, pending.Documents, pending.Example));
        Assert.Equal(MetadataFields.Types, pending.Field);
    }

    [Fact]
    public async Task Adding_a_new_term_turns_its_values_into_suggestions_and_undo_holds_them_again()
    {
        var pending = await PendingAsync("Heist Kit");

        var decision = await _vocabulary.AcceptPendingAsync(pending.Id, Ct);

        Assert.Equal("heist-kit", (await EffectiveAsync())[MetadataFields.Types].First?.Value);
        Assert.Equal("Heist Kit", (await _vocabulary.GetAsync(Ct)).Resolve("type", "heist kit")?.Label);
        Assert.Empty(await _vocabulary.GetPendingAsync(Ct));
        Assert.Equal([_document], decision.EntryIds);

        await _vocabulary.UndoAsync(decision, Ct);

        Assert.False((await EffectiveAsync())[MetadataFields.Types].IsKnown);
        Assert.Null((await _vocabulary.GetAsync(Ct)).Resolve("type", "heist kit"));
        Assert.Single(await _vocabulary.GetPendingAsync(Ct));
    }

    [Fact]
    public async Task Mapping_a_new_term_files_its_values_under_an_existing_one_and_learns_the_name()
    {
        var pending = await PendingAsync("Scenario Pack");

        var decision = await _vocabulary.MapPendingAsync(pending.Id, "adventure", Ct);

        Assert.Equal("adventure", (await EffectiveAsync())[MetadataFields.Types].First?.Value);
        Assert.Equal("adventure", (await _vocabulary.GetAsync(Ct)).Resolve("type", "scenario pack")?.Key);
        var (again, state) = await _vocabulary.ProposeTermAsync("type", "Scenario Pack", Ct);
        Assert.Equal(("adventure", TermState.Active), (again.Key, state));

        await _vocabulary.UndoAsync(decision, Ct);

        Assert.False((await EffectiveAsync())[MetadataFields.Types].IsKnown);
        Assert.Null((await _vocabulary.GetAsync(Ct)).Resolve("type", "scenario pack"));
    }

    [Fact]
    public async Task A_rejected_new_term_takes_its_values_with_it_and_is_not_proposed_again()
    {
        var pending = await PendingAsync("Nonsense");

        await _vocabulary.RejectPendingAsync(pending.Id, Ct);

        Assert.Empty(await _vocabulary.GetPendingAsync(Ct));
        var (term, state) = await _vocabulary.ProposeTermAsync("type", "nonsense", Ct);
        Assert.Equal(TermState.Rejected, state);
        Assert.Equal(0, await _metadata.AddSuggestionsAsync(_other, Hash, [new MetadataProposal(MetadataFields.Types, term.Key, AssertionOrigin.Ai)], Ct));
        Assert.False((await EffectiveAsync())[MetadataFields.Types].IsKnown);
    }

    async Task<PendingTerm> PendingAsync(string label)
    {
        var (term, _) = await _vocabulary.ProposeTermAsync("type", label, Ct);
        await _metadata.AddSuggestionsAsync(_document, Hash, [new MetadataProposal(MetadataFields.Types, term.Key, AssertionOrigin.Ai)], Ct);
        return Assert.Single(await _vocabulary.GetPendingAsync(Ct));
    }

    [Fact]
    public async Task A_new_term_named_like_another_terms_alias_takes_that_name_over()
    {
        // The starter vocabulary files "one shot" under Adventure; the user wants One-shot as a type of its own.
        var edit = await _vocabulary.AddTermAsync("type", "One-shot", Ct);

        Assert.Null(edit.Problem);
        Assert.Contains("Adventure", edit.Note, StringComparison.Ordinal);
        var vocabulary = await _vocabulary.GetAsync(Ct);
        Assert.Equal("one-shot", vocabulary.Resolve("type", "One Shot")?.Key);
        Assert.Equal("adventure", vocabulary.Resolve("type", "module")?.Key);
        Assert.DoesNotContain((await _vocabulary.ListAsync("type", Ct)).Single(e => e.Key == "adventure").Aliases, a => a.Text == "one shot");
    }

    [Fact]
    public async Task Another_terms_own_name_is_refused()
    {
        var edit = await _vocabulary.AddTermAsync("type", "adventure", Ct);

        Assert.NotNull(edit.Problem);
        Assert.Null(edit.TermId);
    }

    [Fact]
    public async Task Renaming_keeps_the_key_and_the_old_name()
    {
        var adventure = (await _vocabulary.ListAsync("type", Ct)).Single(e => e.Key == "adventure");

        var edit = await _vocabulary.RenameAsync(adventure.Id, "Adventure module", "Adv", Ct);

        Assert.Null(edit.Problem);
        var vocabulary = await _vocabulary.GetAsync(Ct);
        Assert.Equal("Adventure module", vocabulary.Label("type", "adventure"));
        Assert.Equal("Adv", vocabulary.ShortLabel("type", "adventure"));
        Assert.Equal("adventure", vocabulary.Resolve("type", "Adventure")?.Key);
    }

    [Fact]
    public async Task An_alias_moves_from_the_term_that_had_it_and_can_be_removed()
    {
        var terms = await _vocabulary.ListAsync("type", Ct);
        var adventure = terms.Single(e => e.Key == "adventure");
        var bestiary = terms.First(e => e.Key != "adventure");

        Assert.NotNull((await _vocabulary.AddAliasAsync(adventure.Id, "adventure", Ct)).Problem);
        Assert.NotNull((await _vocabulary.AddAliasAsync(adventure.Id, bestiary.Label, Ct)).Problem);

        var moved = await _vocabulary.AddAliasAsync(bestiary.Id, "module", Ct);
        Assert.Contains("Adventure", moved.Note, StringComparison.Ordinal);
        Assert.Equal(bestiary.Key, (await _vocabulary.GetAsync(Ct)).Resolve("type", "module")?.Key);

        var alias = (await _vocabulary.ListAsync("type", Ct)).Single(e => e.Id == bestiary.Id).Aliases.Single(a => a.Text == "module");
        await _vocabulary.RemoveAliasAsync(alias.Id, Ct);
        Assert.Null((await _vocabulary.GetAsync(Ct)).Resolve("type", "module"));
    }

    sealed class SnapshotComparer : IEqualityComparer<FieldSnapshot>
    {
        public static readonly SnapshotComparer Instance = new();

        public bool Equals(FieldSnapshot? x, FieldSnapshot? y) =>
            x is not null && y is not null && x.EntryId == y.EntryId && x.Field == y.Field
            && x.Rows.SequenceEqual(y.Rows) && x.Rejections.Select(r => r.Normalized).SequenceEqual(y.Rejections.Select(r => r.Normalized));

        public int GetHashCode(FieldSnapshot obj) => obj.EntryId.GetHashCode();
    }
}
