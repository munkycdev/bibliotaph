using System.Text.Json.Nodes;
using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog.Tests;

/// <summary>What moves with a card when it joins another, and what comes back with Undo.</summary>
public sealed class EntryAttachmentTests : IAsyncLifetime
{
    static readonly DateTime Monday = new(2026, 10, 5, 9, 30, 0, DateTimeKind.Utc);
    readonly string _dir = Directory.CreateTempSubdirectory("bibliotaph-attachments-").FullName;
    CatalogDatabase _database = null!;
    EntryStore _entries = null!;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = new CatalogDatabase(Path.Combine(_dir, "catalog.db"), Path.Combine(_dir, "backups"));
        await _database.MigrateFromTemplateAsync();
        _entries = new EntryStore(new Factory(_database));
    }

    public ValueTask DisposeAsync()
    {
        _database.ClearPool();
        Directory.Delete(_dir, recursive: true);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// What a copy joining another wrote as its record before records had a version, in a catalog seeded by
    /// <see cref="SeedCopyAsync"/>. Catalogs out there hold rows like this, and Not the same book must still undo them.
    /// </summary>
    const string CopyJoinRecorded = """
        {"Sources":[{"Id":2,"WasCurrent":true}],"Assertions":[13,12],"SetAside":[12],"Rejections":[23],"Runs":["run-31"],"Favorite":true,"Reading":true,"Pin":true,"Collections":[42],"SessionItems":[52],"Note":true,"NoteAppended":null,"PageNotes":[62],"Members":[],"Parts":[71]}
        """;

    /// <summary>What Pack together wrote for a pack merged into another before records had a version, seeded by <see cref="SeedPacksAsync"/>.</summary>
    const string PackMergeRecorded = """
        {"Sources":[],"Assertions":[],"SetAside":[],"Rejections":[],"Runs":[],"Favorite":false,"Reading":false,"Pin":false,"Collections":[],"SessionItems":[],"Note":false,"NoteAppended":null,"PageNotes":[],"Members":[1003,1004],"Parts":[]}
        """;

    /// <summary>
    /// The foreign keys to an entry that no kind of <see cref="EntryAttachments"/> moves, and why. A new table that
    /// points at an entry has to be added there or here.
    /// </summary>
    static readonly Dictionary<string, string> Staying = new(StringComparer.Ordinal)
    {
        ["EntryJoin.EntryId"] = "The record of the join itself: Undo reads it and deletes it.",
        ["EntryJoin.JoinedEntryId"] = "The record of the join itself: Undo reads it and deletes it.",
        ["Entry.MergedIntoEntryId"] = "Marks the card that joined: the join sets it and Undo clears it.",
        ["PackDecision.EntryId"] = "Stays with the pack it was made for: PackStore follows merges to find every folder behind a pack.",
        ["ElsewhereMatch.EntryId"] = "Names a book owned elsewhere, which is always the card kept in a join, never one that joins.",
        ["CorrectionRequest.EntryId"] = "Stays behind on the card that joined, and is there again when Not the same book brings it back.",
        ["CollectionDecline.EntryId"] = "Stays behind on the card that joined, and is there again when Not the same book brings it back.",
    };

    [Fact]
    public async Task Every_row_that_points_at_an_entry_moves_with_it_or_is_named_as_staying()
    {
        await using var db = _database.CreateContext();
        var keys = db.Model.GetEntityTypes().SelectMany(t => t.GetForeignKeys())
            .Where(k => k.PrincipalEntityType.ClrType == typeof(Entry))
            .Select(k => $"{k.DeclaringEntityType.ClrType.Name}.{Assert.Single(k.Properties).Name}")
            .ToHashSet(StringComparer.Ordinal);
        var moved = EntryAttachments.All.Select(a => $"{a.Rows.Name}.{a.ForeignKey}").ToHashSet(StringComparer.Ordinal);

        Assert.Empty(keys.Except(moved).Except(Staying.Keys).Order(StringComparer.Ordinal));
        Assert.Empty(moved.Except(keys).Order(StringComparer.Ordinal));
        Assert.Empty(Staying.Keys.Except(keys).Order(StringComparer.Ordinal));
        Assert.Empty(Staying.Keys.Intersect(moved).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_join_recorded_before_records_had_a_version_is_still_undone_by_not_the_same_book()
    {
        var (original, copy) = (await TestEntries.AddAsync(_database, 'a'), await TestEntries.AddAsync(_database, 'b'));
        await SeedCopyAsync(original.Value, copy.Value);
        var before = await OnAsync(original.Value, copy.Value);

        Assert.Equal(new CopyJoin(original, copy), await _entries.JoinAsCopyAsync(1, 2, Ct));

        // Everything moved but a rejection and a collection the card has already, and the record is the one written
        // before, with its version added.
        Assert.Equal(["1002 in collection 41", "1002 rejection 22"], await OnAsync(copy.Value));
        var written = JsonNode.Parse(await RecordedAsync())!.AsObject();
        Assert.Equal(MovedRows.CurrentVersion, (int)written["Version"]!);
        written.Remove("Version");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(CopyJoinRecorded), written), written.ToJsonString());

        await RecordAsync(CopyJoinRecorded);
        Assert.Equal(copy, await _entries.SplitCopyAsync(original, 2, ct: Ct));

        Assert.Equal(before, await OnAsync(original.Value, copy.Value));
        Assert.Null(await MergedIntoAsync(copy.Value));
    }

    [Fact]
    public async Task A_pack_merge_recorded_before_records_had_a_version_is_still_undone()
    {
        var (older, newer) = await SeedPacksAsync();
        var before = await OnAsync(older, newer);
        var packs = new PackStore(new Factory(_database));

        var together = await packs.PackTogetherAsync([new EntryId(newer), new EntryId(older)], Ct);

        Assert.NotNull(together);
        Assert.Equal([newer, 1003, 1004], together.Changed.Skip(1).Select(e => e.Value));
        Assert.Empty(await OnAsync(newer));
        var written = JsonNode.Parse(await RecordedAsync())!.AsObject();
        Assert.Equal(MovedRows.CurrentVersion, (int)written["Version"]!);
        written.Remove("Version");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(PackMergeRecorded), written), written.ToJsonString());

        await RecordAsync(PackMergeRecorded);
        await packs.UndoPackTogetherAsync(together, Ct);

        Assert.Equal(before, await OnAsync(older, newer));
        Assert.Null(await MergedIntoAsync(newer));
    }

    [Fact]
    public async Task A_join_recorded_by_a_newer_version_is_refused_rather_than_half_undone()
    {
        var (original, copy) = (await TestEntries.AddAsync(_database, 'a'), await TestEntries.AddAsync(_database, 'b'));
        await SeedCopyAsync(original.Value, copy.Value);
        await _entries.JoinAsCopyAsync(1, 2, Ct);
        var joined = await OnAsync(original.Value, copy.Value);
        await RecordAsync(CopyJoinRecorded.Replace("{\"Sources\"", "{\"Version\":2,\"Sources\"", StringComparison.Ordinal));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _entries.SplitCopyAsync(original, 2, ct: Ct));

        Assert.Equal(joined, await OnAsync(original.Value, copy.Value));
        Assert.Equal(original.Value, await MergedIntoAsync(copy.Value));
    }

    /// <summary>Everything on these entries that a join moves, one line per row, each starting with its entry's id.</summary>
    async Task<List<string>> OnAsync(params long[] entries)
    {
        List<string> rows = [];
        foreach (var entry in entries) rows.AddRange((await RowsAsync(entry)).Select(r => $"{entry} {r}"));
        return [.. rows.Order(StringComparer.Ordinal)];
    }

    async Task<List<string>> RowsAsync(long entry)
    {
        await using var db = _database.CreateContext();
        return
        [
            .. (await db.EntrySources.Where(s => s.EntryId == entry).ToListAsync(Ct)).Select(s => $"source {s.Id} of {s.DocumentId}, current {s.IsCurrent}"),
            .. (await db.Assertions.Where(a => a.EntryId == entry).ToListAsync(Ct)).Select(a => $"value {a.Id} {a.State}"),
            .. (await db.Rejections.Where(r => r.EntryId == entry).ToListAsync(Ct)).Select(r => $"rejection {r.Id}"),
            .. (await db.ClassificationRuns.Where(r => r.EntryId == entry).ToListAsync(Ct)).Select(r => $"run {r.Id}"),
            .. (await db.Favorites.Where(f => f.EntryId == entry).ToListAsync(Ct)).Select(f => $"heart {f.CreatedUtc:O}"),
            .. (await db.ReadingStates.Where(r => r.EntryId == entry).ToListAsync(Ct)).Select(r => $"reading {r.DocumentId} page {r.PageIndex}"),
            .. (await db.Pins.Where(p => p.EntryId == entry).ToListAsync(Ct)).Select(p => $"pin at {p.Position}"),
            .. (await db.CollectionItems.Where(i => i.EntryId == entry).ToListAsync(Ct)).Select(i => $"in collection {i.CollectionId}"),
            .. (await db.SessionItems.Where(i => i.EntryId == entry).ToListAsync(Ct)).Select(i => $"session item {i.Id} at {i.Position}"),
            .. (await db.Notes.Where(n => n.EntryId == entry).ToListAsync(Ct)).Select(n => $"note {n.Id} on {n.PageRefId}: {n.Text}"),
            .. (await db.Entries.Where(e => e.ParentEntryId == entry).ToListAsync(Ct)).Select(e => $"{e.Kind} {e.Id}"),
        ];
    }

    async Task<string> RecordedAsync()
    {
        await using var db = _database.CreateContext();
        return (await db.EntryJoins.SingleAsync(Ct)).MovedJson;
    }

    /// <summary>Replaces the join's record with <paramref name="json"/>, as an older or newer Bibliotaph would have written it.</summary>
    async Task RecordAsync(string json)
    {
        await using var db = _database.CreateContext();
        (await db.EntryJoins.SingleAsync(Ct)).MovedJson = json;
        await db.SaveChangesAsync(Ct);
    }

    async Task<long?> MergedIntoAsync(long entry)
    {
        await using var db = _database.CreateContext();
        return await db.Entries.Where(e => e.Id == entry).Select(e => e.MergedIntoEntryId).SingleAsync(Ct);
    }

    /// <summary>
    /// Puts one of everything that moves on <paramref name="copy"/>, with fixed ids, and a little on
    /// <paramref name="original"/> for the copy's to be compared with: a title that disagrees, a rejection both have,
    /// and a collection both are in.
    /// </summary>
    async Task SeedCopyAsync(long original, long copy)
    {
        await using var db = _database.CreateContext();
        Assertion Title(long id, long entry, string value) => new()
        {
            Id = id, EntryId = entry, Field = MetadataFields.Title.Key, ValueJson = $"\"{value}\"", NormalizedValue = value.ToLowerInvariant(),
            Origin = AssertionOrigin.User, State = AssertionState.Confirmed, CreatedUtc = Monday,
        };
        db.Assertions.AddRange(Title(11, original, "Chapel of Tides"), Title(12, copy, "Chapel backup"), new Assertion
        {
            Id = 13, EntryId = copy, Field = MetadataFields.Tags.Key, ValueJson = "\"Printed\"", NormalizedValue = "printed",
            Origin = AssertionOrigin.User, State = AssertionState.Confirmed, CreatedUtc = Monday,
        });
        db.Rejections.AddRange(
            new Rejection { Id = 21, EntryId = original, Field = MetadataFields.Types.Key, NormalizedValue = "adventure", CreatedUtc = Monday },
            new Rejection { Id = 22, EntryId = copy, Field = MetadataFields.Types.Key, NormalizedValue = "adventure", CreatedUtc = Monday },
            new Rejection { Id = 23, EntryId = copy, Field = MetadataFields.Types.Key, NormalizedValue = "map", CreatedUtc = Monday });
        db.ClassificationRuns.Add(new ClassificationRun
        {
            Id = "run-31", EntryId = copy, ContentHash = new string('b', 64), Provider = "ollama", Model = "model-a", StartedUtc = Monday,
        });
        db.Favorites.Add(new Favorite { EntryId = copy, CreatedUtc = Monday });
        db.ReadingStates.Add(new ReadingState { EntryId = copy, DocumentId = 2, PageIndex = 7, OpenedUtc = Monday });
        db.Pins.Add(new Pin { Kind = PinKind.Entry, EntryId = copy, Position = 5 });
        db.Collections.AddRange(
            new CollectionNode { Id = 41, Name = "Campaign", CreatedUtc = Monday, UsedUtc = Monday },
            new CollectionNode { Id = 42, Name = "Prep", CreatedUtc = Monday, UsedUtc = Monday });
        db.CollectionItems.AddRange(
            new CollectionItem { CollectionId = 41, EntryId = original, AddedUtc = Monday },
            new CollectionItem { CollectionId = 41, EntryId = copy, AddedUtc = Monday },
            new CollectionItem { CollectionId = 42, EntryId = copy, AddedUtc = Monday });
        db.SessionPacks.Add(new SessionPack { Id = 51, Title = "Session one", CreatedUtc = Monday, TouchedUtc = Monday });
        db.SessionItems.Add(new SessionItem { Id = 52, PackId = 51, Position = 0, EntryId = copy, AddedUtc = Monday });
        db.Notes.AddRange(
            new Note { Id = 61, EntryId = copy, Text = "Borrowed", CreatedUtc = Monday, UpdatedUtc = Monday },
            new Note
            {
                Id = 62, EntryId = copy, Text = "The map", CreatedUtc = Monday, UpdatedUtc = Monday,
                PageRef = new PageRef { Id = 63, DocumentId = 2, FirstPdfPage = 3, LastPdfPage = 4 },
            });
        db.Entries.Add(new Entry
        {
            Id = 71, Kind = EntryKind.Part, ParentEntryId = copy, CreatedUtc = Monday,
            Sources = [new EntrySource { Id = 72, DocumentId = 2, FirstPdfPage = 10, LastPdfPage = 19, IsCurrent = true }],
        });
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>Two packs of two images each, the older one first, with fixed ids.</summary>
    async Task<(long Older, long Newer)> SeedPacksAsync()
    {
        List<EntryId> images = [];
        foreach (var hash in "cdef") images.Add(await TestEntries.AddAsync(_database, hash));
        await using var db = _database.CreateContext();
        db.Entries.AddRange(
            new Entry { Id = 81, Kind = EntryKind.Pack, CreatedUtc = Monday },
            new Entry { Id = 82, Kind = EntryKind.Pack, CreatedUtc = Monday });
        await db.SaveChangesAsync(Ct);
        var members = await db.Entries.Where(e => e.Kind == EntryKind.Whole).OrderBy(e => e.Id).ToListAsync(Ct);
        for (var i = 0; i < members.Count; i++) members[i].ParentEntryId = i < 2 ? 81 : 82;
        await db.SaveChangesAsync(Ct);
        return (81, 82);
    }
}
