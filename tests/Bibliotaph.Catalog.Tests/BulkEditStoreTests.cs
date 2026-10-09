using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Microsoft.Data.Sqlite;

namespace Bibliotaph.Catalog.Tests;

/// <summary>Bulk metadata edits (slice 4e) and their undo in a real catalog.db.</summary>
public sealed class BulkEditStoreTests : IAsyncLifetime
{
    readonly string _dir = Directory.CreateTempSubdirectory("bibliotaph-bulk-").FullName;
    readonly MetadataStoreTests.SteppingClock _clock = new(new DateTimeOffset(2026, 10, 9, 9, 0, 0, TimeSpan.Zero));
    CatalogDatabase _database = null!;
    MetadataStore _metadata = null!;
    long _first;
    long _second;
    long _third;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = new CatalogDatabase(Path.Combine(_dir, "catalog.db"), Path.Combine(_dir, "backups"));
        await _database.MigrateAsync();
        _metadata = new MetadataStore(new Factory(_database), _clock);
        await using var db = _database.CreateContext();
        var documents = "abc".Select(c => db.Documents.Add(new Document { ContentHash = new string(c, 64), Format = "pdf", CreatedUtc = DateTime.UtcNow }).Entity).ToList();
        await db.SaveChangesAsync();
        (_first, _second, _third) = (documents[0].Id, documents[1].Id, documents[2].Id);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
        return ValueTask.CompletedTask;
    }

    long[] All => [_first, _second, _third];

    static MetadataProposal Hint(MetadataField field, string value, AssertionOrigin origin = AssertionOrigin.Folder) => new(field, value, origin);

    async Task<EffectiveField> FieldAsync(long document, MetadataField field) => (await _metadata.GetAsync(document, Ct)).Compute()[field];

    [Fact]
    public async Task Setting_a_single_field_confirms_the_value_on_every_book_and_supersedes_the_users_own()
    {
        await _metadata.ReplaceHintsAsync(_first, [Hint(MetadataFields.System, "dnd")], Ct);
        await _metadata.SetValuesAsync(_second, MetadataFields.System, ["pathfinder"], Ct);
        _clock.Step();

        var undo = await _metadata.ApplyBulkAsync(All, [new BulkChange(MetadataFields.System, BulkAction.Set, "coc")], Ct);

        Assert.Equal(3, undo.Count);
        foreach (var document in All)
        {
            var system = await FieldAsync(document, MetadataFields.System);
            Assert.Equal("coc", Assert.Single(system.Values).Value);
            Assert.True(system.Values[0].Confirmed);
        }
        // The user's own value that it replaced is superseded, so Back to suggestions won't bring it back.
        await _metadata.ResetAsync(_second, MetadataFields.System, Ct);
        Assert.False((await FieldAsync(_second, MetadataFields.System)).IsKnown);
    }

    [Fact]
    public async Task A_book_that_already_has_the_value_confirmed_is_left_alone_and_needs_no_undo()
    {
        await _metadata.SetValuesAsync(_first, MetadataFields.Year, ["2019"], Ct);

        var undo = await _metadata.ApplyBulkAsync(All, [new BulkChange(MetadataFields.Year, BulkAction.Set, "2019")], Ct);

        Assert.Equal([_second, _third], undo.Select(s => s.DocumentId).Order());
    }

    [Fact]
    public async Task Adding_a_value_puts_it_on_every_book_and_keeps_the_values_each_shows()
    {
        await _metadata.ReplaceHintsAsync(_first, [Hint(MetadataFields.Themes, "horror"), Hint(MetadataFields.Types, "adventure")], Ct);
        await _metadata.SetValuesAsync(_second, MetadataFields.Themes, ["heist"], Ct);
        _clock.Step();

        await _metadata.ApplyBulkAsync(All, [new BulkChange(MetadataFields.Themes, BulkAction.Add, "heist")], Ct);

        var first = await FieldAsync(_first, MetadataFields.Themes);
        Assert.Equal(["heist", "horror"], first.Values.Select(v => v.Value).Order());
        Assert.All(first.Values, v => Assert.True(v.Confirmed));
        Assert.Equal(["heist"], (await FieldAsync(_second, MetadataFields.Themes)).Values.Select(v => v.Value));
        Assert.Equal(["heist"], (await FieldAsync(_third, MetadataFields.Themes)).Values.Select(v => v.Value));
        // A field the edit didn't name keeps its suggestion as it was.
        Assert.False((await FieldAsync(_first, MetadataFields.Types)).First!.Confirmed);
    }

    [Fact]
    public async Task A_removed_value_is_rejected_on_every_book_and_survives_the_next_hint_run()
    {
        foreach (var document in All)
            await _metadata.ReplaceHintsAsync(document, [Hint(MetadataFields.Types, "adventure"), Hint(MetadataFields.Types, "map", AssertionOrigin.Folder)], Ct);
        await _metadata.SetValuesAsync(_second, MetadataFields.Types, ["adventure", "map"], Ct);
        _clock.Step();

        var undo = await _metadata.ApplyBulkAsync(All, [new BulkChange(MetadataFields.Types, BulkAction.Remove, "adventure")], Ct);
        Assert.Equal(3, undo.Count);

        // The hints from names run again, as on a reindex, and propose the same.
        _clock.Step();
        foreach (var document in All)
            await _metadata.ReplaceHintsAsync(document, [Hint(MetadataFields.Types, "adventure"), Hint(MetadataFields.Types, "map", AssertionOrigin.Folder)], Ct);

        foreach (var document in All)
        {
            var types = await FieldAsync(document, MetadataFields.Types);
            Assert.Equal(["map"], types.Values.Select(v => v.Value));
            Assert.DoesNotContain(types.Alternatives, a => a.Normalized == "adventure");
            Assert.Contains(("type", "adventure"), (await _metadata.GetAsync(document, Ct)).Rejections);
        }
        // Removing only rejects: the map the folder suggested is still only a suggestion where nobody confirmed it.
        Assert.False((await FieldAsync(_first, MetadataFields.Types)).First!.Confirmed);
        Assert.True((await FieldAsync(_second, MetadataFields.Types)).First!.Confirmed);
    }

    [Fact]
    public async Task Adding_and_removing_in_one_field_together()
    {
        await _metadata.SetValuesAsync(_first, MetadataFields.Tags, ["Friday game", "Spare"], Ct);
        _clock.Step();

        await _metadata.ApplyBulkAsync([_first, _second],
            [new BulkChange(MetadataFields.Tags, BulkAction.Add, "Shelved"), new BulkChange(MetadataFields.Tags, BulkAction.Remove, "spare")], Ct);

        Assert.Equal(["Friday game", "Shelved"], (await FieldAsync(_first, MetadataFields.Tags)).Values.Select(v => v.Value).Order());
        Assert.Equal(["Shelved"], (await FieldAsync(_second, MetadataFields.Tags)).Values.Select(v => v.Value));
        Assert.False((await FieldAsync(_third, MetadataFields.Tags)).IsKnown);
    }

    [Fact]
    public async Task Back_to_suggestions_forgets_the_users_own_value_on_every_book()
    {
        foreach (var document in All) await _metadata.ReplaceHintsAsync(document, [Hint(MetadataFields.Edition, "dnd-5e")], Ct);
        await _metadata.SetValuesAsync(_first, MetadataFields.Edition, ["dnd-35"], Ct);
        await _metadata.ConfirmAsync(_second, MetadataFields.Edition, Ct);

        var undo = await _metadata.ApplyBulkAsync(All, [new BulkChange(MetadataFields.Edition, BulkAction.Reset)], Ct);

        Assert.Equal([_first, _second], undo.Select(s => s.DocumentId).Order());
        foreach (var document in All)
        {
            var edition = await FieldAsync(document, MetadataFields.Edition);
            Assert.Equal("dnd-5e", edition.First!.Value);
            Assert.False(edition.First.Confirmed);
        }
    }

    [Fact]
    public async Task Undo_puts_every_affected_field_back_exactly()
    {
        await _metadata.ReplaceHintsAsync(_first, [Hint(MetadataFields.System, "dnd"), Hint(MetadataFields.Types, "adventure"), Hint(MetadataFields.Themes, "horror")], Ct);
        await _metadata.SetValuesAsync(_second, MetadataFields.System, ["pathfinder"], Ct);
        await _metadata.RejectAsync(_second, MetadataFields.Types, "bestiary", Ct);
        await _metadata.SetValuesAsync(_second, MetadataFields.Tags, ["Friday game"], Ct);
        await _metadata.ReplaceHintsAsync(_third, [Hint(MetadataFields.Types, "adventure"), Hint(MetadataFields.Edition, "dnd-5e")], Ct);
        await _metadata.ConfirmAsync(_third, MetadataFields.Edition, Ct);
        MetadataField[] fields = [MetadataFields.System, MetadataFields.Edition, MetadataFields.Types, MetadataFields.Themes, MetadataFields.Tags];
        var before = new List<FieldSnapshot>();
        foreach (var document in All)
            foreach (var field in fields)
                before.Add(await _metadata.SnapshotAsync(document, field, Ct));
        var effective = await Task.WhenAll(All.Select(d => _metadata.GetAsync(d, Ct)));
        _clock.Step();

        var undo = await _metadata.ApplyBulkAsync(All,
        [
            new BulkChange(MetadataFields.System, BulkAction.Set, "coc"),
            new BulkChange(MetadataFields.Edition, BulkAction.Reset),
            new BulkChange(MetadataFields.Types, BulkAction.Add, "bestiary"),
            new BulkChange(MetadataFields.Types, BulkAction.Remove, "adventure"),
            new BulkChange(MetadataFields.Themes, BulkAction.Remove, "horror"),
            new BulkChange(MetadataFields.Tags, BulkAction.Add, "Shelved"),
        ], Ct);
        Assert.NotEmpty(undo);
        await _metadata.RestoreAsync(undo, Ct);

        var after = new List<FieldSnapshot>();
        foreach (var document in All)
            foreach (var field in fields)
                after.Add(await _metadata.SnapshotAsync(document, field, Ct));
        Assert.Equal(before, after, SnapshotComparer.Instance);
        var restored = await Task.WhenAll(All.Select(d => _metadata.GetAsync(d, Ct)));
        for (var i = 0; i < All.Length; i++)
            Assert.Equal(Describe(effective[i].Compute()), Describe(restored[i].Compute()));
    }

    [Fact]
    public async Task A_change_that_does_not_fit_its_field_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _metadata.ApplyBulkAsync(All, [new BulkChange(MetadataFields.Tags, BulkAction.Set, "x")], Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _metadata.ApplyBulkAsync(All, [new BulkChange(MetadataFields.System, BulkAction.Add, "dnd")], Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _metadata.ApplyBulkAsync(All, [new BulkChange(MetadataFields.System, BulkAction.Set)], Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _metadata.ApplyBulkAsync(All,
            [new BulkChange(MetadataFields.System, BulkAction.Set, "dnd"), new BulkChange(MetadataFields.System, BulkAction.Reset)], Ct));
    }

    /// <summary>Every field's values, confirmations and alternatives, in a comparable form.</summary>
    static string Describe(EffectiveMetadata metadata) => string.Join("; ", metadata.Fields.Select(f =>
        $"{f.Field.Key}={string.Join(",", f.Values.Select(v => $"{v.Value}{(v.Confirmed ? "!" : "")}"))}/{string.Join(",", f.Alternatives.Select(a => a.Value))}"));

    /// <summary>Same rows in the same states, and the same rejections from the same moment.</summary>
    sealed class SnapshotComparer : IEqualityComparer<FieldSnapshot>
    {
        public static readonly SnapshotComparer Instance = new();

        public bool Equals(FieldSnapshot? x, FieldSnapshot? y) =>
            x is not null && y is not null && x.DocumentId == y.DocumentId && x.Field == y.Field
            && x.Rows.OrderBy(r => r.Id).SequenceEqual(y.Rows.OrderBy(r => r.Id))
            && x.Rejections.OrderBy(r => r.Normalized, StringComparer.Ordinal).SequenceEqual(y.Rejections.OrderBy(r => r.Normalized, StringComparer.Ordinal));

        public int GetHashCode(FieldSnapshot obj) => obj.DocumentId.GetHashCode();
    }
}
