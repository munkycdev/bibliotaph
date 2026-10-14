using System.Text.Json;
using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

/// <summary>
/// One kind of row that belongs to a library card: what happens to it when the card joins another (Match, Same book,
/// Pack together), when that join is undone (Not the same book, Undo), and when the card is removed and Undo brings it
/// back. Every kind answers all four, so a kind can't be moved by a join and then forgotten by its undo.
/// </summary>
internal interface IEntryAttachment
{
    /// <summary>The entity whose rows these are.</summary>
    Type Rows { get; }

    /// <summary>The property of <see cref="Rows"/> that points at the card.</summary>
    string ForeignKey { get; }

    /// <summary>Moves the joining card's rows of this kind to the target, recording in <see cref="EntryMove.Moved"/> what moved. Doesn't save.</summary>
    Task MoveAsync(EntryMove move, CancellationToken ct);

    /// <summary>Puts back on the card that joined what <see cref="EntryMoveBack.Moved"/> says moved, as those rows are now. Doesn't save, but may in between.</summary>
    Task MoveBackAsync(EntryMoveBack back, CancellationToken ct);

    /// <summary>Adds to <paramref name="removed"/> what of this kind is on <paramref name="entry"/>, before the entry is deleted.</summary>
    Task<RemovedEntry> CaptureAsync(CatalogDbContext db, Entry entry, RemovedEntry removed, CancellationToken ct);

    /// <summary>Puts back on the entry, saved again under a new id, what <see cref="CaptureAsync"/> kept. Doesn't save.</summary>
    Task RestoreIntoAsync(EntryReturn back, CancellationToken ct);
}

/// <summary>A card joining another: the card that joins, the card it joins, and the record of what moved, filled as it goes.</summary>
internal sealed record EntryMove(CatalogDbContext Db, Entry Joining, long Target, MovedRows Moved, DateTime Now)
{
    public long JoiningId => Joining.Id;
}

/// <summary>
/// A join being undone: <see cref="From"/>, the card the rows are on now; <see cref="Card"/>, the card that joined and
/// gets them back; and, tracked, the sources the caller loaded of <see cref="From"/>.
/// </summary>
internal sealed record EntryMoveBack(CatalogDbContext Db, long From, long Card, EntryJoin Join, MovedRows Moved, List<EntrySource> Sources);

/// <summary>A removed entry coming back: the entry, saved with its new id, and what was kept of it.</summary>
internal sealed record EntryReturn(CatalogDbContext Db, Entry Entry, RemovedEntry Removed, DateTime Now);

/// <summary>
/// What a join moved, kept as JSON with the join (<see cref="EntryJoin.MovedJson"/>) so it can be undone later, after
/// the app has been closed and updated. Rows already in users' catalogs have no <see cref="Version"/>; they read as
/// version 1, which is the shape they have.
/// </summary>
internal sealed class MovedRows
{
    /// <summary>The shape this build writes. Raise it when a change means older rows must be read differently.</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// The shape of the record. The default stays 1 whatever <see cref="CurrentVersion"/> becomes: it is what rows
    /// written without one are.
    /// </summary>
    public int Version { get; set; } = 1;
    public List<MovedSource> Sources { get; set; } = [];
    public List<long> Assertions { get; set; } = [];
    public List<long> SetAside { get; set; } = [];
    public List<long> Rejections { get; set; } = [];
    public List<string> Runs { get; set; } = [];
    /// <summary>The card that joined brought its heart.</summary>
    public bool Favorite { get; set; }
    /// <summary>The card that joined brought its reading position.</summary>
    public bool Reading { get; set; }
    /// <summary>The card that joined brought its pin.</summary>
    public bool Pin { get; set; }
    /// <summary>The collections the card that joined brought its membership of.</summary>
    public List<long> Collections { get; set; } = [];
    /// <summary>The session items that pointed at the card that joined.</summary>
    public List<long> SessionItems { get; set; } = [];
    /// <summary>The card that joined brought its own note, to a card without one.</summary>
    public bool Note { get; set; }
    /// <summary>The text added to the end of the surviving card's own note, from the note of the card that joined.</summary>
    public string? NoteAppended { get; set; }
    /// <summary>The page notes the card that joined brought.</summary>
    public List<long> PageNotes { get; set; } = [];
    /// <summary>For a pack merged into another: the images that were in it.</summary>
    public List<long> Members { get; set; } = [];
    /// <summary>The adventures marked in the joining card's file.</summary>
    public List<long> Parts { get; set; } = [];

    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>
    /// Reads a join's record. One written by a newer Bibliotaph is refused: it may list rows this build doesn't know to
    /// put back, and a partial undo would leave them on the wrong card without a word.
    /// </summary>
    public static MovedRows FromJson(string json)
    {
        var moved = JsonSerializer.Deserialize<MovedRows>(json) ?? new MovedRows();
        if (moved.Version > CurrentVersion)
            throw new InvalidOperationException($"This join was recorded by a newer version of Bibliotaph (record version {moved.Version}) and can't be undone by this one.");
        return moved;
    }
}

internal sealed record MovedSource(long Id, bool WasCurrent);

/// <summary>
/// Every kind of row that belongs to a card, in the order joins move them. <see cref="EntryStore"/> walks this list to
/// join, undo a join, remove an entry and bring it back.
/// </summary>
internal static class EntryAttachments
{
    public static IReadOnlyList<IEntryAttachment> All { get; } =
    [
        new SourceRows(), new AssertionRows(), new RejectionRows(), new RunRows(), new FavoriteRow(), new ReadingRow(), new PinRow(),
        new CollectionRows(), new SessionItemRows(), new OwnNote(), new PageNotes(), new PackMembers(), new Parts(),
    ];

    /// <summary>
    /// The copies: all of them move, without changing which is current; Undo puts back which was. A removed entry has
    /// none to keep: a book owned elsewhere has no file, and <see cref="PartStore"/> keeps an adventure's pages itself.
    /// </summary>
    sealed class SourceRows : IEntryAttachment
    {
        public Type Rows => typeof(EntrySource);
        public string ForeignKey => nameof(EntrySource.EntryId);

        public async Task MoveAsync(EntryMove move, CancellationToken ct)
        {
            var sources = await move.Db.EntrySources.Where(s => s.EntryId == move.JoiningId).ToListAsync(ct);
            move.Moved.Sources.AddRange(sources.Select(s => new MovedSource(s.Id, s.IsCurrent)));
            foreach (var source in sources) source.EntryId = move.Target;
        }

        public async Task MoveBackAsync(EntryMoveBack back, CancellationToken ct)
        {
            var moved = back.Moved.Sources;
            var sourceIds = moved.Select(s => s.Id).ToList();
            var sources = back.Sources;
            // Cleared first and saved: the unique index allows one current source per entry at any moment.
            foreach (var source in sources.Where(s => sourceIds.Contains(s.Id))) source.IsCurrent = false;
            await back.Db.SaveChangesAsync(ct);
            foreach (var source in sources.Where(s => sourceIds.Contains(s.Id)))
            {
                source.EntryId = back.Card;
                source.IsCurrent = moved.Single(s => s.Id == source.Id).WasCurrent;
            }
            if (!sources.Any(s => s.EntryId == back.Card && s.IsCurrent) && sources.FirstOrDefault(s => s.DocumentId == back.Join.DocumentId && s.FirstPdfPage == null) is { } leaving)
                leaving.IsCurrent = true;
        }

        public Task<RemovedEntry> CaptureAsync(CatalogDbContext db, Entry entry, RemovedEntry removed, CancellationToken ct) => Task.FromResult(removed);

        public Task RestoreIntoAsync(EntryReturn back, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// The card's values. All of them move; a value the user set that disagrees with one set on the other card, for a
    /// field with one value, is set aside for a "Copies disagree" card. A removed entry's are kept, loaded with it.
    /// </summary>
    sealed class AssertionRows : IEntryAttachment
    {
        public Type Rows => typeof(Assertion);
        public string ForeignKey => nameof(Assertion.EntryId);

        public async Task MoveAsync(EntryMove move, CancellationToken ct)
        {
            var settled = await move.Db.Assertions.AsNoTracking()
                .Where(a => a.EntryId == move.Target && a.State == AssertionState.Confirmed)
                .Select(a => new { a.Field, a.NormalizedValue })
                .ToListAsync(ct);
            foreach (var assertion in await move.Db.Assertions.Where(a => a.EntryId == move.JoiningId).ToListAsync(ct))
            {
                if (assertion.State == AssertionState.Confirmed && MetadataFields.Find(assertion.Field) is { Multiple: false }
                    && settled.Any(s => s.Field == assertion.Field && s.NormalizedValue != assertion.NormalizedValue))
                {
                    assertion.State = AssertionState.SetAside;
                    move.Moved.SetAside.Add(assertion.Id);
                }
                assertion.EntryId = move.Target;
                move.Moved.Assertions.Add(assertion.Id);
            }
        }

        public async Task MoveBackAsync(EntryMoveBack back, CancellationToken ct)
        {
            foreach (var assertion in await back.Db.Assertions.Where(a => a.EntryId == back.From && back.Moved.Assertions.Contains(a.Id)).ToListAsync(ct))
            {
                assertion.EntryId = back.Card;
                // Back to what it was on its own card, even after the user settled the disagreement on this one.
                if (back.Moved.SetAside.Contains(assertion.Id) && assertion.State is AssertionState.SetAside or AssertionState.Superseded)
                    assertion.State = AssertionState.Confirmed;
            }
        }

        public Task<RemovedEntry> CaptureAsync(CatalogDbContext db, Entry entry, RemovedEntry removed, CancellationToken ct) =>
            Task.FromResult(removed with { Assertions = [.. entry.Assertions.Select(Detached)] });

        public Task RestoreIntoAsync(EntryReturn back, CancellationToken ct)
        {
            back.Entry.Assertions.AddRange(back.Removed.Assertions.Select(Detached));
            return Task.CompletedTask;
        }

        static Assertion Detached(Assertion a) => new()
        {
            Field = a.Field,
            ValueJson = a.ValueJson,
            NormalizedValue = a.NormalizedValue,
            Origin = a.Origin,
            ContentHash = a.ContentHash,
            EvidencePagesJson = a.EvidencePagesJson,
            EvidenceQuote = a.EvidenceQuote,
            FromSampling = a.FromSampling,
            RunId = a.RunId,
            State = a.State,
            CreatedUtc = a.CreatedUtc,
            DecidedUtc = a.DecidedUtc,
        };
    }

    /// <summary>
    /// The values the user said are wrong. They move unless the other card has them already; those stay behind. A
    /// removed entry's are kept, loaded with it.
    /// </summary>
    sealed class RejectionRows : IEntryAttachment
    {
        public Type Rows => typeof(Rejection);
        public string ForeignKey => nameof(Rejection.EntryId);

        public async Task MoveAsync(EntryMove move, CancellationToken ct)
        {
            var rejected = await move.Db.Rejections.AsNoTracking().Where(r => r.EntryId == move.Target).Select(r => new { r.Field, r.NormalizedValue }).ToListAsync(ct);
            foreach (var rejection in await move.Db.Rejections.Where(r => r.EntryId == move.JoiningId).ToListAsync(ct))
            {
                if (rejected.Any(r => r.Field == rejection.Field && r.NormalizedValue == rejection.NormalizedValue)) continue;
                rejection.EntryId = move.Target;
                move.Moved.Rejections.Add(rejection.Id);
            }
        }

        public async Task MoveBackAsync(EntryMoveBack back, CancellationToken ct)
        {
            foreach (var rejection in await back.Db.Rejections.Where(r => r.EntryId == back.From && back.Moved.Rejections.Contains(r.Id)).ToListAsync(ct))
                rejection.EntryId = back.Card;
        }

        public Task<RemovedEntry> CaptureAsync(CatalogDbContext db, Entry entry, RemovedEntry removed, CancellationToken ct) =>
            Task.FromResult(removed with { Rejections = [.. entry.Rejections.Select(Detached)] });

        public Task RestoreIntoAsync(EntryReturn back, CancellationToken ct)
        {
            back.Entry.Rejections.AddRange(back.Removed.Rejections.Select(Detached));
            return Task.CompletedTask;
        }

        static Rejection Detached(Rejection r) => new() { Field = r.Field, NormalizedValue = r.NormalizedValue, CreatedUtc = r.CreatedUtc };
    }

    /// <summary>The model runs that read the card's copies: all of them move. A removed entry's aren't kept.</summary>
    sealed class RunRows : IEntryAttachment
    {
        public Type Rows => typeof(ClassificationRun);
        public string ForeignKey => nameof(ClassificationRun.EntryId);

        public async Task MoveAsync(EntryMove move, CancellationToken ct)
        {
            foreach (var run in await move.Db.ClassificationRuns.Where(r => r.EntryId == move.JoiningId).ToListAsync(ct))
            {
                run.EntryId = move.Target;
                move.Moved.Runs.Add(run.Id);
            }
        }

        public async Task MoveBackAsync(EntryMoveBack back, CancellationToken ct)
        {
            foreach (var run in await back.Db.ClassificationRuns.Where(r => r.EntryId == back.From && back.Moved.Runs.Contains(r.Id)).ToListAsync(ct))
                run.EntryId = back.Card;
        }

        public Task<RemovedEntry> CaptureAsync(CatalogDbContext db, Entry entry, RemovedEntry removed, CancellationToken ct) => Task.FromResult(removed);

        public Task RestoreIntoAsync(EntryReturn back, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// The heart. It moves only to a card without one; otherwise it stays behind on the card that joined, which shows
    /// nowhere, until Not the same book brings it back. Undo takes it back as it is now: unmarked since, it stays
    /// unmarked. A removed entry's is kept.
    /// </summary>
    sealed class FavoriteRow : IEntryAttachment
    {
        public Type Rows => typeof(Favorite);
        public string ForeignKey => nameof(Favorite.EntryId);

        public async Task MoveAsync(EntryMove move, CancellationToken ct)
        {
            var db = move.Db;
            if (await db.Favorites.SingleOrDefaultAsync(f => f.EntryId == move.JoiningId, ct) is { } favorite
                && !await db.Favorites.AnyAsync(f => f.EntryId == move.Target, ct))
            {
                db.Favorites.Remove(favorite);
                db.Favorites.Add(new Favorite { EntryId = move.Target, CreatedUtc = favorite.CreatedUtc });
                move.Moved.Favorite = true;
            }
        }

        public async Task MoveBackAsync(EntryMoveBack back, CancellationToken ct)
        {
            var db = back.Db;
            if (back.Moved.Favorite && await db.Favorites.SingleOrDefaultAsync(f => f.EntryId == back.From, ct) is { } favorite)
            {
                db.Favorites.Remove(favorite);
                db.Favorites.Add(new Favorite { EntryId = back.Card, CreatedUtc = favorite.CreatedUtc });
            }
        }

        public async Task<RemovedEntry> CaptureAsync(CatalogDbContext db, Entry entry, RemovedEntry removed, CancellationToken ct) =>
            removed with { Favorite = await db.Favorites.AnyAsync(f => f.EntryId == entry.Id, ct) };

        public Task RestoreIntoAsync(EntryReturn back, CancellationToken ct)
        {
            if (back.Removed.Favorite) back.Db.Favorites.Add(new Favorite { EntryId = back.Entry.Id, CreatedUtc = back.Now });
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Where the reader left the book. It moves as the heart does, only to a card without one. A removed entry's isn't
    /// kept.
    /// </summary>
    sealed class ReadingRow : IEntryAttachment
    {
        public Type Rows => typeof(ReadingState);
        public string ForeignKey => nameof(ReadingState.EntryId);

        public async Task MoveAsync(EntryMove move, CancellationToken ct)
        {
            var db = move.Db;
            if (await db.ReadingStates.SingleOrDefaultAsync(r => r.EntryId == move.JoiningId, ct) is { } reading
                && !await db.ReadingStates.AnyAsync(r => r.EntryId == move.Target, ct))
            {
                db.ReadingStates.Remove(reading);
                db.ReadingStates.Add(new ReadingState { EntryId = move.Target, DocumentId = reading.DocumentId, PageIndex = reading.PageIndex, OpenedUtc = reading.OpenedUtc });
                move.Moved.Reading = true;
            }
        }

        public async Task MoveBackAsync(EntryMoveBack back, CancellationToken ct)
        {
            var db = back.Db;
            if (back.Moved.Reading && await db.ReadingStates.SingleOrDefaultAsync(r => r.EntryId == back.From, ct) is { } reading)
            {
                db.ReadingStates.Remove(reading);
                db.ReadingStates.Add(new ReadingState { EntryId = back.Card, DocumentId = reading.DocumentId, PageIndex = reading.PageIndex, OpenedUtc = reading.OpenedUtc });
            }
        }

        public Task<RemovedEntry> CaptureAsync(CatalogDbContext db, Entry entry, RemovedEntry removed, CancellationToken ct) => Task.FromResult(removed);

        public Task RestoreIntoAsync(EntryReturn back, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// The card's pin in the sidebar. It moves as the heart does, keeping its place. A removed entry's place is kept,
    /// and Undo pins it there again.
    /// </summary>
    sealed class PinRow : IEntryAttachment
    {
        public Type Rows => typeof(Pin);
        public string ForeignKey => nameof(Pin.EntryId);

        public async Task MoveAsync(EntryMove move, CancellationToken ct)
        {
            var db = move.Db;
            if (await db.Pins.SingleOrDefaultAsync(p => p.EntryId == move.JoiningId, ct) is { } pin && !await db.Pins.AnyAsync(p => p.EntryId == move.Target, ct))
            {
                pin.EntryId = move.Target;
                move.Moved.Pin = true;
            }
        }

        public async Task MoveBackAsync(EntryMoveBack back, CancellationToken ct)
        {
            if (back.Moved.Pin && await back.Db.Pins.SingleOrDefaultAsync(p => p.EntryId == back.From, ct) is { } pin) pin.EntryId = back.Card;
        }

        public async Task<RemovedEntry> CaptureAsync(CatalogDbContext db, Entry entry, RemovedEntry removed, CancellationToken ct) =>
            removed with { PinPosition = await PinStore.PositionAsync(db, PinTarget.Entry(new EntryId(entry.Id)), ct) };

        public async Task RestoreIntoAsync(EntryReturn back, CancellationToken ct)
        {
            if (back.Removed.PinPosition is { } pin) await PinStore.AddAsync(back.Db, PinTarget.Entry(new EntryId(back.Entry.Id)), pin, ct);
        }
    }

    /// <summary>
    /// Membership of collections. It moves to every collection the card isn't in already; those it is in stay behind
    /// with the card that joined. A removed entry goes back into those of its collections that are still there.
    /// </summary>
    sealed class CollectionRows : IEntryAttachment
    {
        public Type Rows => typeof(CollectionItem);
        public string ForeignKey => nameof(CollectionItem.EntryId);

        public async Task MoveAsync(EntryMove move, CancellationToken ct)
        {
            var db = move.Db;
            var targetCollections = await db.CollectionItems.Where(i => i.EntryId == move.Target).Select(i => i.CollectionId).ToListAsync(ct);
            foreach (var item in await db.CollectionItems.Where(i => i.EntryId == move.JoiningId && !targetCollections.Contains(i.CollectionId)).ToListAsync(ct))
            {
                db.CollectionItems.Remove(item);
                db.CollectionItems.Add(new CollectionItem { CollectionId = item.CollectionId, EntryId = move.Target, AddedUtc = item.AddedUtc });
                move.Moved.Collections.Add(item.CollectionId);
            }
        }

        public async Task MoveBackAsync(EntryMoveBack back, CancellationToken ct)
        {
            var db = back.Db;
            foreach (var item in await db.CollectionItems.Where(i => i.EntryId == back.From && back.Moved.Collections.Contains(i.CollectionId)).ToListAsync(ct))
            {
                db.CollectionItems.Remove(item);
                db.CollectionItems.Add(new CollectionItem { CollectionId = item.CollectionId, EntryId = back.Card, AddedUtc = item.AddedUtc });
            }
        }

        public async Task<RemovedEntry> CaptureAsync(CatalogDbContext db, Entry entry, RemovedEntry removed, CancellationToken ct) =>
            removed with { Collections = await db.CollectionItems.Where(i => i.EntryId == entry.Id).Select(i => i.CollectionId).ToListAsync(ct) };

        public async Task RestoreIntoAsync(EntryReturn back, CancellationToken ct)
        {
            if (back.Removed.Collections is not { Count: > 0 } collections) return;
            var still = await back.Db.Collections.Where(c => collections.Contains(c.Id)).Select(c => c.Id).ToListAsync(ct);
            back.Db.CollectionItems.AddRange(still.Select(id => new CollectionItem { CollectionId = id, EntryId = back.Entry.Id, AddedUtc = back.Now }));
        }
    }

    /// <summary>
    /// Session items. They all move, in their places: a pack can hold the same book any number of times. A removed
    /// entry's go back to their places, in packs and sections that are still there.
    /// </summary>
    sealed class SessionItemRows : IEntryAttachment
    {
        public Type Rows => typeof(SessionItem);
        public string ForeignKey => nameof(SessionItem.EntryId);

        public async Task MoveAsync(EntryMove move, CancellationToken ct)
        {
            foreach (var item in await move.Db.SessionItems.Where(i => i.EntryId == move.JoiningId).ToListAsync(ct))
            {
                item.EntryId = move.Target;
                move.Moved.SessionItems.Add(item.Id);
            }
        }

        public async Task MoveBackAsync(EntryMoveBack back, CancellationToken ct)
        {
            foreach (var item in await back.Db.SessionItems.Where(i => i.EntryId == back.From && back.Moved.SessionItems.Contains(i.Id)).ToListAsync(ct))
                item.EntryId = back.Card;
        }

        public async Task<RemovedEntry> CaptureAsync(CatalogDbContext db, Entry entry, RemovedEntry removed, CancellationToken ct)
        {
            var entryId = new EntryId(entry.Id);
            return removed with
            {
                SessionItems = [.. (await db.SessionItems.AsNoTracking().Where(i => i.EntryId == entry.Id).ToListAsync(ct))
                    .Select(i => new SessionItemInfo(i.Id, i.PackId, i.SectionId, i.Position, entryId, null, i.Label, i.Note, i.AddedUtc) { Link = i.Link })],
            };
        }

        public async Task RestoreIntoAsync(EntryReturn back, CancellationToken ct)
        {
            var db = back.Db;
            foreach (var item in back.Removed.SessionItems ?? [])
            {
                if (!await db.SessionPacks.AnyAsync(p => p.Id == item.PackId, ct)) continue;
                var section = item.SectionId is { } s && await db.SessionSections.AnyAsync(x => x.Id == s, ct) ? item.SectionId : null;
                await db.SessionItems.Where(i => i.PackId == item.PackId && i.SectionId == section && i.Position >= item.Position)
                    .ExecuteUpdateAsync(u => u.SetProperty(i => i.Position, i => i.Position + 1), ct);
                db.SessionItems.Add(new SessionItem
                {
                    PackId = item.PackId,
                    SectionId = section,
                    Position = item.Position,
                    EntryId = back.Entry.Id,
                    Link = item.Link,
                    Label = item.Label,
                    Note = item.Note,
                    AddedUtc = item.AddedUtc,
                });
            }
        }
    }

    /// <summary>
    /// The card's own note. It moves to a card without one; to a card with one, its text is added at the end, and the
    /// note stays behind on the card that joined for Not the same book. A removed entry's is kept.
    /// </summary>
    sealed class OwnNote : IEntryAttachment
    {
        public Type Rows => typeof(Note);
        public string ForeignKey => nameof(Note.EntryId);

        public async Task MoveAsync(EntryMove move, CancellationToken ct)
        {
            var db = move.Db;
            if (await db.Notes.SingleOrDefaultAsync(n => n.EntryId == move.JoiningId && n.PageRefId == null, ct) is not { } note) return;
            if (await db.Notes.SingleOrDefaultAsync(n => n.EntryId == move.Target && n.PageRefId == null, ct) is { } own)
            {
                var appended = $"{Environment.NewLine}{Environment.NewLine}{note.Text}";
                own.Text += appended;
                own.UpdatedUtc = move.Now;
                move.Moved.NoteAppended = appended;
            }
            else
            {
                note.EntryId = move.Target;
                move.Moved.Note = true;
            }
        }

        public async Task MoveBackAsync(EntryMoveBack back, CancellationToken ct)
        {
            var own = await back.Db.Notes.SingleOrDefaultAsync(n => n.EntryId == back.From && n.PageRefId == null, ct);
            if (back.Moved.Note && own is not null) own.EntryId = back.Card;
            // The added text comes off again, unless the note has been edited past it since; the card kept its own note anyway.
            else if (back.Moved.NoteAppended is { } appended && own is not null && own.Text.EndsWith(appended, StringComparison.Ordinal))
                own.Text = own.Text[..^appended.Length];
        }

        public async Task<RemovedEntry> CaptureAsync(CatalogDbContext db, Entry entry, RemovedEntry removed, CancellationToken ct) =>
            removed with { Note = await db.Notes.Where(n => n.EntryId == entry.Id && n.PageRefId == null).Select(n => n.Text).SingleOrDefaultAsync(ct) };

        public Task RestoreIntoAsync(EntryReturn back, CancellationToken ct)
        {
            if (back.Removed.Note is { } note) back.Db.Notes.Add(new Note { EntryId = back.Entry.Id, Text = note, CreatedUtc = back.Now, UpdatedUtc = back.Now });
            return Task.CompletedTask;
        }
    }

    /// <summary>Notes on pages of the card's copies: they all move. A removed entry's aren't kept.</summary>
    sealed class PageNotes : IEntryAttachment
    {
        public Type Rows => typeof(Note);
        public string ForeignKey => nameof(Note.EntryId);

        public async Task MoveAsync(EntryMove move, CancellationToken ct)
        {
            foreach (var pageNote in await move.Db.Notes.Where(n => n.EntryId == move.JoiningId && n.PageRefId != null).ToListAsync(ct))
            {
                pageNote.EntryId = move.Target;
                move.Moved.PageNotes.Add(pageNote.Id);
            }
        }

        public async Task MoveBackAsync(EntryMoveBack back, CancellationToken ct)
        {
            foreach (var pageNote in await back.Db.Notes.Where(n => n.EntryId == back.From && back.Moved.PageNotes.Contains(n.Id)).ToListAsync(ct))
                pageNote.EntryId = back.Card;
        }

        public Task<RemovedEntry> CaptureAsync(CatalogDbContext db, Entry entry, RemovedEntry removed, CancellationToken ct) => Task.FromResult(removed);

        public Task RestoreIntoAsync(EntryReturn back, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// A pack's files, when one pack merges into another: they all go into the pack it joins, and Undo takes back
    /// those still in it. Only a book owned elsewhere or an adventure is removed, and neither holds files.
    /// </summary>
    sealed class PackMembers : IEntryAttachment
    {
        public Type Rows => typeof(Entry);
        public string ForeignKey => nameof(Entry.ParentEntryId);

        public async Task MoveAsync(EntryMove move, CancellationToken ct)
        {
            if (move.Joining.Kind != EntryKind.Pack) return;
            foreach (var member in await move.Db.Entries.Where(e => e.ParentEntryId == move.JoiningId).ToListAsync(ct))
            {
                member.ParentEntryId = move.Target;
                move.Moved.Members.Add(member.Id);
            }
        }

        public async Task MoveBackAsync(EntryMoveBack back, CancellationToken ct)
        {
            foreach (var member in await back.Db.Entries.Where(e => e.ParentEntryId == back.From && back.Moved.Members.Contains(e.Id)).ToListAsync(ct))
                member.ParentEntryId = back.Card;
        }

        public Task<RemovedEntry> CaptureAsync(CatalogDbContext db, Entry entry, RemovedEntry removed, CancellationToken ct) => Task.FromResult(removed);

        public Task RestoreIntoAsync(EntryReturn back, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// The adventures marked in the card's file. They go with it: their pages are in a copy of this book now, and are
    /// looked for in its other copies. Only a book owned elsewhere or an adventure is removed, and neither holds any.
    /// </summary>
    sealed class Parts : IEntryAttachment
    {
        public Type Rows => typeof(Entry);
        public string ForeignKey => nameof(Entry.ParentEntryId);

        public async Task MoveAsync(EntryMove move, CancellationToken ct)
        {
            foreach (var part in await move.Db.Entries.Where(e => e.ParentEntryId == move.JoiningId && e.Kind == EntryKind.Part).ToListAsync(ct))
            {
                part.ParentEntryId = move.Target;
                move.Moved.Parts.Add(part.Id);
            }
        }

        public async Task MoveBackAsync(EntryMoveBack back, CancellationToken ct)
        {
            foreach (var part in await back.Db.Entries.Where(e => e.ParentEntryId == back.From && back.Moved.Parts.Contains(e.Id)).ToListAsync(ct))
                part.ParentEntryId = back.Card;
        }

        public Task<RemovedEntry> CaptureAsync(CatalogDbContext db, Entry entry, RemovedEntry removed, CancellationToken ct) => Task.FromResult(removed);

        public Task RestoreIntoAsync(EntryReturn back, CancellationToken ct) => Task.CompletedTask;
    }
}
