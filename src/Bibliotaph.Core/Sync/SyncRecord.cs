namespace Bibliotaph.Core.Sync;

/// <summary>
/// One field of a piece of user work in the library sync file: its value as compact JSON, or null when it was cleared
/// (a heart taken off, a note emptied), and when that last changed. A cleared field keeps its time, so the clearing
/// reaches the other PC instead of the old value coming back from it.
/// </summary>
public readonly record struct SyncField(string? Value, DateTime ChangedUtc);

/// <summary>
/// A piece of user work as the library sync file carries it: a book by content hash, or something the user made (a
/// collection, a binder, a saved search) by its sync id, with each field's value and change time. <see
/// cref="DeletedUtc"/> is a tombstone: the record was deleted then, and stays deleted unless a field changed later.
/// <see cref="Aliases"/> are the other keys a book answers to (its other copies' hashes), so a PC that has only one of
/// its copies still finds it.
/// </summary>
public sealed record SyncRecord(string Key, IReadOnlyDictionary<string, SyncField> Fields, DateTime? DeletedUtc = null)
{
    public static readonly IReadOnlyDictionary<string, SyncField> NoFields = new Dictionary<string, SyncField>(StringComparer.Ordinal);

    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>The latest change to any field, or <see cref="DateTime.MinValue"/> for a record with none.</summary>
    public DateTime LastChangedUtc => Fields.Count == 0 ? DateTime.MinValue : Fields.Values.Max(f => f.ChangedUtc);

    /// <summary>Deleted, and not changed since: a field changed after the deletion brings the record back.</summary>
    public bool IsDeleted => DeletedUtc is { } deleted && deleted >= LastChangedUtc;

    /// <summary>The fields that hold a value, by name: what the record says now.</summary>
    public IReadOnlyDictionary<string, string> Values =>
        Fields.Where(f => f.Value.Value is not null).ToDictionary(f => f.Key, f => f.Value.Value!, StringComparer.Ordinal);

    /// <summary>The same fields, times and tombstone. Aliases don't count: they describe this PC's copies, not the work.</summary>
    public bool SameAs(SyncRecord other) =>
        Key == other.Key && DeletedUtc == other.DeletedUtc && Fields.Count == other.Fields.Count
        && Fields.All(f => other.Fields.TryGetValue(f.Key, out var o) && o == f.Value);
}

/// <summary>
/// The rules that make two PCs agree: per record and per field, the newer change wins; a deletion wins over every
/// change made before it. Merging is commutative, associative and idempotent, so it doesn't matter in which order PCs
/// read each other's files or how often.
/// </summary>
public static class SyncMerge
{
    /// <summary>Both records' knowledge together: the newer of each field, and the later deletion.</summary>
    public static SyncRecord Merge(SyncRecord local, SyncRecord remote)
    {
        var fields = new Dictionary<string, SyncField>(local.Fields, StringComparer.Ordinal);
        foreach (var (name, field) in remote.Fields)
            fields[name] = fields.TryGetValue(name, out var mine) ? Newer(mine, field) : field;
        return new SyncRecord(local.Key, fields, Later(local.DeletedUtc, remote.DeletedUtc)) { Aliases = local.Aliases };
    }

    /// <summary>
    /// The newer of two versions of a field. Equal times (the same change read back) keep either; in the unlikely case
    /// of equal times and different values, the greater value wins, so every PC picks the same one.
    /// </summary>
    public static SyncField Newer(SyncField a, SyncField b) =>
        a.ChangedUtc != b.ChangedUtc ? (a.ChangedUtc > b.ChangedUtc ? a : b)
        : string.CompareOrdinal(a.Value, b.Value) >= 0 ? a : b;

    static DateTime? Later(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : a > b ? a : b;

    /// <summary>
    /// Records what the user did on this PC since the last look: <paramref name="current"/> is the record's fields as
    /// the catalog holds them now. Each field whose value differs from <paramref name="known"/> (or that is gone, which
    /// clears it) gets <paramref name="now"/> as its change time; so does every field of a record that comes back after
    /// a deletion, so it outlives the tombstone. Returns <paramref name="known"/> itself when nothing changed.
    /// </summary>
    public static SyncRecord Stamp(SyncRecord known, IReadOnlyDictionary<string, string> current, DateTime now)
    {
        var revived = known.IsDeleted;
        var fields = new Dictionary<string, SyncField>(known.Fields, StringComparer.Ordinal);
        var changed = false;
        foreach (var name in current.Keys.Union(known.Fields.Keys, StringComparer.Ordinal).ToList())
        {
            var value = current.GetValueOrDefault(name);
            var had = known.Fields.TryGetValue(name, out var field);
            if (had && field.Value == value && !revived) continue;
            if (!had && value is null) continue;
            var previous = had ? field.ChangedUtc : (DateTime?)null;
            if (revived || !had) previous = Later(previous, known.DeletedUtc);
            fields[name] = new SyncField(value, After(now, previous));
            changed = true;
        }
        if (revived && !changed)
        {
            // Back with nothing to say: a record must outlive its tombstone by at least one field.
            foreach (var (name, value) in current) fields[name] = new SyncField(value, After(now, known.DeletedUtc));
            changed = current.Count > 0;
        }
        return changed ? known with { Fields = fields } : known;
    }

    /// <summary>A new record of this PC's: every field changed <paramref name="now"/>.</summary>
    public static SyncRecord Create(string key, IReadOnlyDictionary<string, string> current, DateTime now) =>
        new(key, current.ToDictionary(f => f.Key, f => new SyncField(f.Value, now), StringComparer.Ordinal));

    /// <summary>The record deleted on this PC <paramref name="now"/>, after every change it knows of.</summary>
    public static SyncRecord Delete(SyncRecord known, DateTime now) =>
        known.IsDeleted ? known : known with { DeletedUtc = After(now, known.LastChangedUtc) };

    /// <summary>
    /// <paramref name="now"/>, or just after <paramref name="previous"/> when this PC's clock is behind the PC that made
    /// the previous change: a change made here must always win over the value it replaced, or the next merge would undo it.
    /// </summary>
    static DateTime After(DateTime now, DateTime? previous) =>
        previous is { } p && p >= now ? p.AddTicks(1) : now;
}
