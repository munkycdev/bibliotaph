using System.Text.Json;
using Bibliotaph.Catalog;
using Bibliotaph.Core.Sync;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.Processing;

public sealed partial class LibrarySync
{
    /// <summary>A record as this pass knows it: from sync_state, then stamped and merged.</summary>
    sealed class Known(SyncRecord record, bool pending)
    {
        public SyncRecord Record { get; set; } = record;

        /// <summary>From another PC, and not in this catalog (yet): kept, never written to this PC's file, never taken for a deletion here.</summary>
        public bool Pending { get; set; } = pending;

        public bool Dirty { get; set; }
    }

    /// <summary>The channels this device's file is behind in: changes not written there yet.</summary>
    readonly HashSet<ISyncChannel> _channelsBehind = [];

    /// <summary>The channels this PC's file has been written to since Bibliotaph started, which the first pass writes to whatever changed.</summary>
    readonly HashSet<ISyncChannel> _channelsWritten = [];

    DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// One pass: what changed here is stamped with the time it was noticed; with <paramref name="merge"/>, the other
    /// PCs' files are merged in and the catalog made to agree, and what couldn't be used yet is kept for later; then
    /// this PC's file is written if it is behind.
    /// </summary>
    async Task<SyncPass> PassAsync(bool merge, CancellationToken ct)
    {
        var channels = _channels.Where(c => c.IsOn).ToList();
        if (channels.Count == 0) return new SyncPass([], false, false);
        var machineId = await MachineIdAsync(ct);
        var snapshot = await _store.ReadAsync(ct);
        var state = (await _store.LoadStateAsync(ct)).ToDictionary(k => k.Key, k => new Known(k.Value.Record, k.Value.Pending), StringComparer.Ordinal);
        var removed = new HashSet<string>(StringComparer.Ordinal);
        StampLocal(state, snapshot, removed);

        string? problem = null;
        var mergedFrom = new List<SyncFileContent>();
        var changed = false;
        HashSet<string> waiting = [with(StringComparer.Ordinal)];
        if (merge)
        {
            foreach (var channel in channels)
            {
                var read = await channel.ReadPeersAsync(ct);
                mergedFrom.AddRange(read.Peers.Where(p => p.MachineId != machineId));
                problem ??= read.Problem;
            }
            // A device reached through more than one channel counts once, as its newest file.
            mergedFrom = [.. mergedFrom.GroupBy(p => p.MachineId).Select(g => g.MaxBy(p => p.WrittenUtc)!)];
            var aliases = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var record in mergedFrom.SelectMany(p => p.Records).Concat(state.Values.Select(k => k.Record)).Where(r => r.Aliases.Count > 0))
                aliases[record.Key] = record.Aliases;
            var resolver = new Resolver(snapshot, aliases);

            // Cards that weren't here before may be now, under this catalog's own key for them.
            foreach (var (key, known) in state.Where(k => k.Value.Pending && SyncKeys.IsCard(k.Key)).ToList())
            {
                if (resolver.LocalCard(key, known.Record.Aliases) is not { } local || local == key) continue;
                state.Remove(key);
                removed.Add(key);
                MergeIn(state, removed, resolver, known.Record);
            }
            foreach (var record in mergedFrom.SelectMany(p => p.Records)) MergeIn(state, removed, resolver, record);

            var changes = Changes(state, snapshot);
            if (changes.Count > 0)
            {
                var applied = await _store.ApplyAsync(changes, resolver.FindCard, ct);
                changed = applied.Changed;
                // Another device's tags are guessed here from this device's own pages.
                if (applied.Tags.Count > 0 && _userTags is not null) await _userTags.RefreshAsync(applied.Tags, ct);
                // A pack made here as another device made it is named after its folder here, as packing names it.
                if (applied.Packed.Count > 0 && _hints is not null) await _hints.StorePacksAsync(applied.Packed, ct);
                waiting.UnionWith(applied.NotApplied);
                snapshot = await _store.ReadAsync(ct);
                // What couldn't be used, or was used but doesn't show here, waits rather than reading as deleted here.
                foreach (var change in changes.Where(c => c.Wanted is not null && (applied.NotApplied.Contains(c.Key) || !snapshot.Records.ContainsKey(c.Key))))
                {
                    var known = state[change.Key];
                    if (known.Pending) continue;
                    known.Pending = true;
                    known.Dirty = true;
                }
            }
            // Records merged in that this catalog now has stop being pending; anything the catalog took but shows
            // differently becomes this PC's own change; what it refused waits.
            StampLocal(state, snapshot, removed, waiting);
        }

        foreach (var key in removed.Where(state.ContainsKey).ToList()) removed.Remove(key);
        var dirty = state.Values.Where(k => k.Dirty).Select(k => (k.Record, k.Pending)).ToList();
        await _store.SaveStateAsync(dirty, removed, ct);
        if (dirty.Count > 0 || removed.Count > 0) _channelsBehind.UnionWith(_channels);

        var wrote = false;

        foreach (var channel in channels.Where(c => _channelsBehind.Contains(c) || !_channelsWritten.Contains(c)))
        {
            var records = Own(state.Values).ToList();
            if (await channel.WriteOwnAsync((stream, token) =>
                SyncFile.WriteAsync(stream, MachineName, machineId, BackupService.AppVersion, Now, records, token), ct) is { } failed)
            {
                problem ??= failed;
                continue;
            }
            _channelsBehind.Remove(channel);
            _channelsWritten.Add(channel);
            wrote = true;
        }

        if (changed && _projector is not null) await _projector.ProjectAllAsync(ct);
        if (changed) Merged?.Invoke(this, EventArgs.Empty);
        var now = Now;
        var from = mergedFrom.Count == 0 ? null : string.Join(" and ", mergedFrom.OrderByDescending(p => p.WrittenUtc).Select(p => p.Machine).Distinct());
        if (from is not null) await _settings.SetAsync(SettingKeys.SyncLastMerge, JsonSerializer.Serialize(new LastMerge(from, now), SyncJson.Options), ct);
        SetStatus(s => s with
        {
            Problem = problem,
            LastMergedFrom = from ?? s.LastMergedFrom,
            LastMergedUtc = from is null ? s.LastMergedUtc : now,
            LastWrittenUtc = wrote ? now : s.LastWrittenUtc,
        });
        if (from is not null) _log.LogInformation("Library sync merged {Records} records from {Machines}{Changed}", mergedFrom.Sum(p => p.Records.Count), from,
            changed ? ", changing this catalog" : "");
        return new SyncPass([.. mergedFrom.Select(p => p.Machine)], changed, wrote);
    }

    /// <summary>
    /// Notices what the user did on this PC since the last pass. A record whose fields differ from what was known gets
    /// them stamped now; a new one is added, a card known under another key (its files changed) keeps its history under
    /// its new key; one that went away is a deletion, unless it never was here (pending) or can't be deleted by the user.
    /// A pending record the catalog now has a place for isn't cleared by what the catalog shows: before merging, and for
    /// <paramref name="waiting"/> (what applying refused), it keeps waiting; once applied, the catalog's state is its own.
    /// </summary>
    void StampLocal(Dictionary<string, Known> state, SyncSnapshot snapshot, HashSet<string> removed, HashSet<string>? waiting = null)
    {
        var now = Now;
        var elsewhereKeys = snapshot.Cards.Where(c => c.Key.StartsWith(SyncKeys.Elsewhere, StringComparison.Ordinal)).ToDictionary(c => c.Value, c => c.Key);
        foreach (var (key, fields) in snapshot.Records)
        {
            var card = SyncKeys.IsCard(key);
            var aliases = card ? CardAliases(key, snapshot, elsewhereKeys, state) : [];
            if (!state.TryGetValue(key, out var known))
            {
                SyncRecord? inherited = null;
                foreach (var alias in aliases)
                {
                    if (!state.Remove(alias, out var old)) continue;
                    removed.Add(alias);
                    var moved = old.Record with { Key = key };
                    inherited = inherited is null ? moved : SyncMerge.Merge(inherited, moved);
                }
                // A card nobody has done anything with has nothing to say.
                if (inherited is null && card && fields.Count == 0) continue;
                var record = inherited is null ? SyncMerge.Create(key, fields, now) : SyncMerge.Stamp(inherited, fields, now);
                state[key] = new Known(record with { Aliases = aliases }, pending: false) { Dirty = true };
                removed.Remove(key);
                continue;
            }
            if (known.Pending && (waiting is null || waiting.Contains(key)))
            {
                // Waiting to be used here, and the catalog doesn't show it yet: what is here doesn't clear it. What the
                // user did here meanwhile is kept, as this PC's newer change.
                var mine = fields.Where(f => known.Record.Fields.GetValueOrDefault(f.Key).Value != f.Value).ToDictionary(StringComparer.Ordinal);
                var met = Same(known.Record.Values, fields);
                if (mine.Count == 0 && !met) continue;
                if (mine.Count > 0) known.Record = SyncMerge.Merge(known.Record, SyncMerge.Create(key, mine, now)) with { Aliases = aliases };
                known.Pending = !met;
                known.Dirty = true;
                continue;
            }
            var stamped = SyncMerge.Stamp(known.Record, fields, now);
            if (!ReferenceEquals(stamped, known.Record) || known.Pending || !known.Record.Aliases.SequenceEqual(aliases))
            {
                known.Record = stamped with { Aliases = aliases };
                known.Pending = false;
                known.Dirty = true;
            }
        }
        foreach (var (key, known) in state)
        {
            if (snapshot.Records.ContainsKey(key) || known.Pending || known.Record.IsDeleted || !SyncKeys.CanBeDeleted(key)) continue;
            // A book owned elsewhere that met its file here isn't deleted: its work went on under the file's card.
            if (key.StartsWith(SyncKeys.Elsewhere, StringComparison.Ordinal) && snapshot.Cards.ContainsKey(key)) continue;
            known.Record = SyncMerge.Delete(known.Record, now);
            known.Dirty = true;
        }
    }

    /// <summary>
    /// A card's other keys: its other copies' hashes (or a pack's other images'), and its entry's sync id when it was a
    /// book owned elsewhere that sync knows of, so a PC where it still is one finds it.
    /// </summary>
    static IReadOnlyList<string> CardAliases(string key, SyncSnapshot snapshot, Dictionary<long, string> elsewhereKeys, Dictionary<string, Known> state)
    {
        var aliases = snapshot.Aliases.GetValueOrDefault(key) ?? [];
        if (snapshot.FindCard(key) is { } entryId && elsewhereKeys.TryGetValue(entryId, out var elsewhere) && elsewhere != key
            && (state.ContainsKey(elsewhere) || (state.TryGetValue(key, out var known) && known.Record.Aliases.Contains(elsewhere))))
            return [.. aliases, elsewhere];
        return aliases;
    }

    /// <summary>
    /// Merges another PC's record into what this PC knows, under this catalog's own key for its card, with the cards
    /// its fields point at named as this catalog names them. A record this PC didn't know is pending until it shows here.
    /// </summary>
    static void MergeIn(Dictionary<string, Known> state, HashSet<string> removed, Resolver resolver, SyncRecord remote)
    {
        var key = remote.Key;
        var local = SyncKeys.IsCard(key) ? resolver.LocalCard(key, remote.Aliases) : key;
        var fields = remote.Fields.ToDictionary(f => f.Key, f => f.Key == SyncFields.Card && f.Value.Value is { } value
            && SyncJson.Read<string>(value) is { } card && resolver.LocalCard(card, resolver.AliasesOf(card)) is { } mine
                ? f.Value with { Value = SyncJson.Value(mine) } : f.Value, StringComparer.Ordinal);
        var incoming = new SyncRecord(local ?? key, fields, remote.DeletedUtc) { Aliases = local is null ? remote.Aliases : [] };
        if (state.TryGetValue(incoming.Key, out var known))
        {
            var merged = SyncMerge.Merge(known.Record, incoming);
            if (known.Pending && local is null && merged.Aliases.Count == 0) merged = merged with { Aliases = remote.Aliases };
            if (merged.SameAs(known.Record) && merged.Aliases.SequenceEqual(known.Record.Aliases)) return;
            known.Record = merged;
            known.Dirty = true;
            return;
        }
        state[incoming.Key] = new Known(incoming, pending: true) { Dirty = true };
        removed.Remove(incoming.Key);
    }

    /// <summary>The records the catalog doesn't agree with after merging: to change, to make, or to delete.</summary>
    static List<SyncChange> Changes(Dictionary<string, Known> state, SyncSnapshot snapshot)
    {
        var changes = new List<SyncChange>();
        foreach (var (key, known) in state)
        {
            var current = snapshot.Records.GetValueOrDefault(key);
            if (known.Record.IsDeleted)
            {
                if (current is not null && SyncKeys.CanBeDeleted(key)) changes.Add(new SyncChange(key, null, current));
                continue;
            }
            var wanted = known.Record.Values;
            if (current is null ? wanted.Count == 0 || !known.Pending : Same(wanted, current)) continue;
            changes.Add(new SyncChange(key, wanted, current));
        }
        return changes;
    }

    static bool Same(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(f => b.TryGetValue(f.Key, out var v) && v == f.Value);

    /// <summary>What this PC's file holds: everything this PC has or did, never what is pending.</summary>
    static IEnumerable<SyncRecord> Own(IEnumerable<Known> state) =>
        state.Where(k => !k.Pending && (k.Record.Fields.Count > 0 || k.Record.DeletedUtc is not null)).Select(k => k.Record);

    async Task<Guid> MachineIdAsync(CancellationToken ct)
    {
        if (await _settings.GetAsync(SettingKeys.SyncMachineId, ct) is { } text && Guid.TryParse(text, out var id)) return id;
        id = Guid.NewGuid();
        await _settings.SetAsync(SettingKeys.SyncMachineId, id.ToString("D"), ct);
        return id;
    }

    /// <summary>Finds cards by another PC's names for them: their own key, or any of their copies' hashes that this catalog has.</summary>
    sealed class Resolver(SyncSnapshot snapshot, IReadOnlyDictionary<string, IReadOnlyList<string>> aliases)
    {
        public IReadOnlyList<string> AliasesOf(string key) => aliases.GetValueOrDefault(key) ?? [];

        public string? LocalCard(string key, IEnumerable<string> others) =>
            snapshot.LocalKey(key) ?? others.Select(snapshot.LocalKey).FirstOrDefault(k => k is not null);

        public long? FindCard(string key) => snapshot.FindCard(key) ?? AliasesOf(key).Select(snapshot.FindCard).FirstOrDefault(id => id is not null);
    }
}
