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

    /// <summary>Another PC's file as last read, so an unchanged file isn't parsed again.</summary>
    sealed record PeerFile(DateTime LastWriteUtc, long Length, SyncFileContent Content);

    readonly Dictionary<string, PeerFile> _peerFiles = [with(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Set when this PC's file is behind what it knows: changes not written yet, or none written since Bibliotaph started.</summary>
    bool _unwritten = true;

    DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// One pass: what changed here is stamped with the time it was noticed; with <paramref name="merge"/>, the other
    /// PCs' files are merged in and the catalog made to agree, and what couldn't be used yet is kept for later; then
    /// this PC's file is written if it is behind.
    /// </summary>
    async Task<SyncPass> PassAsync(bool merge, CancellationToken ct)
    {
        if (await GetFolderAsync(ct) is not { } folder) return new SyncPass([], false, false);
        var files = Path.Combine(folder, SyncFile.FolderName);
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
            (mergedFrom, problem) = await ReadPeersAsync(files, machineId, ct);
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
        if (dirty.Count > 0 || removed.Count > 0) _unwritten = true;

        var wrote = false;
        if (problem is null || !problem.StartsWith(DuplicateName, StringComparison.Ordinal))
        {
            try
            {
                wrote = await WriteOwnAsync(files, machineId, state.Values, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Writing the library sync file to {Folder} failed", files);
                problem ??= $"This PC's sync file couldn't be written: {ex.Message} Bibliotaph tries again in a few minutes.";
            }
        }

        if (changed && _projector is not null) await _projector.ProjectAllAsync(ct);
        if (changed) Merged?.Invoke(this, EventArgs.Empty);
        var now = Now;
        var from = mergedFrom.Count == 0 ? null : string.Join(" and ", mergedFrom.OrderByDescending(p => p.WrittenUtc).Select(p => p.Machine).Distinct());
        if (from is not null) await _settings.SetAsync(SettingKeys.SyncLastMerge, JsonSerializer.Serialize(new LastMerge(from, now), SyncJson.Options), ct);
        SetStatus(s => s with
        {
            Folder = folder,
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

    const string DuplicateName = "Another PC called";

    /// <summary>
    /// The other PCs' files in the folder. A file that can't be read is skipped with a problem to show, and tried again
    /// next time; one the cloud app is still writing is simply tried again.
    /// </summary>
    async Task<(List<SyncFileContent> Peers, string? Problem)> ReadPeersAsync(string files, Guid machineId, CancellationToken ct)
    {
        var peers = new List<SyncFileContent>();
        IEnumerable<string> paths;
        try
        {
            Directory.CreateDirectory(files);
            paths = [.. Directory.EnumerateFiles(files, "*.json")];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogInformation(ex, "The library sync folder {Folder} can't be reached", files);
            return (peers, $"The sync folder can't be reached: {ex.Message} Bibliotaph tries again in a few minutes.");
        }

        string? problem = null;
        var own = SyncFile.FileName(MachineName);
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            SyncFileContent content;
            try
            {
                var info = new FileInfo(path);
                if (_peerFiles.TryGetValue(path, out var cached) && cached.LastWriteUtc == info.LastWriteTimeUtc && cached.Length == info.Length)
                    content = cached.Content;
                else
                {
                    await using (var stream = File.OpenRead(path)) content = await SyncFile.ReadAsync(stream, ct);
                    _peerFiles[path] = new PeerFile(info.LastWriteTimeUtc, info.Length, content);
                }
            }
            catch (InvalidDataException ex)
            {
                problem ??= $"{Path.GetFileNameWithoutExtension(name)}'s sync file can't be read. {ex.Message}";
                continue;
            }
            catch (IOException ex)
            {
                // Most likely the cloud app is still writing it: it changes again when done, and is read then.
                _log.LogDebug(ex, "Library sync file {Path} isn't readable yet", path);
                continue;
            }
            if (string.Equals(name, own, StringComparison.OrdinalIgnoreCase))
            {
                if (content.MachineId != machineId)
                    problem = $"{DuplicateName} {MachineName} also syncs with this folder, so this PC stops writing its file. Rename one of the PCs in Windows Settings > System > About.";
                continue;
            }
            // This PC's own file from before it was renamed.
            if (content.MachineId == machineId) continue;
            peers.Add(content);
        }
        return (peers, problem);
    }

    /// <summary>Writes this PC's file when it is behind: everything this PC has or did, never what is pending. Returns whether it wrote.</summary>
    async Task<bool> WriteOwnAsync(string files, Guid machineId, IEnumerable<Known> state, CancellationToken ct)
    {
        var path = Path.Combine(files, SyncFile.FileName(MachineName));
        if (!_unwritten && File.Exists(path)) return false;
        Directory.CreateDirectory(files);
        // Written aside and moved in, so the cloud app and the other PC never see half a file.
        var partial = Path.Combine(files, "~$" + SyncFile.FileName(MachineName) + ".partial");
        try
        {
            await using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                await SyncFile.WriteAsync(stream, MachineName, machineId, BackupService.AppVersion, Now,
                    state.Where(k => !k.Pending && (k.Record.Fields.Count > 0 || k.Record.DeletedUtc is not null)).Select(k => k.Record), ct);
            File.Move(partial, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
        _unwritten = false;
        return true;
    }

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
