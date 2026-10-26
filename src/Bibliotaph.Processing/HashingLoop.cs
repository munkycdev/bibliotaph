using Bibliotaph.Catalog;
using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.Processing;

/// <summary>
/// Hashes new and changed files into documents, and ZIPs member by member, then makes packs and follows folders made
/// into collections. It works while the Index lane does, and pauses with it. Online-only files wait while their drive
/// is short of space, and files that reading downloaded go back to online-only once their stages are done with them.
/// <paramref name="changed"/> is called when something a progress display would show has moved.
/// </summary>
internal sealed class HashingLoop(LibraryStore library, JobBoard queue, IndexingFiles files, PackService packs, FolderCollections folderCollections,
    LaneRunner lanes, IndexingActivityState activity, IndexingOptions options, ILogger log, TimeProvider clock, Action changed) : IDisposable
{
    readonly Signal _hashSignal = new();
    /// <summary>Set when a file may be done with: a file stage finished, a batch was hashed, a lane paused, the setting changed.</summary>
    readonly Signal _returnSignal = new();
    readonly Lock _lock = new();
    readonly Dictionary<long, UnreadableFile> _unreadable = [];

    /// <summary>Set when files may have arrived, moved or been hashed, so folders are looked at for packs after hashing.</summary>
    volatile bool _packsDue = true;
    volatile bool _packing;

    /// <summary>
    /// True while folders are due to be looked at for packs and for the collections that follow them, or are being
    /// looked at.
    /// </summary>
    public bool PacksPending => (packs is not null || folderCollections is not null) && (_packsDue || _packing);

    /// <summary>Files that couldn't be read for hashing, each with why.</summary>
    public IReadOnlyList<UnreadableFile> Unreadable
    {
        get { lock (_lock) return [.. _unreadable.Values]; }
    }

    /// <summary>Wakes the hasher to look for files to hash.</summary>
    public void Wake() => _hashSignal.Set();

    /// <summary>Wakes the returns to online-only: a file may be done with.</summary>
    public void WakeReturns() => _returnSignal.Set();

    /// <summary>A scan finished: files may have arrived or moved, so they are hashed and folders looked at for packs.</summary>
    public void Scanned()
    {
        _packsDue = true;
        _hashSignal.Set();
    }

    /// <summary>Anything under a folder that couldn't be read gets another try, now its folder has been looked at again.</summary>
    public void ForgetUnreadable(string folder)
    {
        lock (_lock)
            foreach (var stale in _unreadable.Values.Where(u => u.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)).ToList())
                _unreadable.Remove(stale.LocationId);
    }

    /// <summary>Hashes whatever is waiting each time it is woken, and looks again now and then without being told to.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (true)
        {
            await _hashSignal.WaitAsync(options.IdleRecheck, ct);
            if (lanes.IsPaused(Lane.Index)) continue;
            await HashPendingAsync(ct);
            await PackAsync(ct);
        }
    }

    /// <summary>
    /// Makes folders and ZIPs of many images one card, once what's there has been hashed, then adds the books new to a
    /// folder made into a collection to that collection. A failure is logged and tried again next time, so it never
    /// stops hashing.
    /// </summary>
    async Task PackAsync(CancellationToken ct)
    {
        if (!_packsDue || lanes.IsPaused(Lane.Index)) return;
        _packing = true;
        _packsDue = false;
        try
        {
            await packs.PlanAsync(ct);
            // After packing, so images that went into a pack don't join as books of their own.
            await folderCollections.FollowAsync(ct);
            changed();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            log.LogError(ex, "Looking for image packs, or for books new to a folder made into a collection, failed");
            _packsDue = true;
        }
        finally
        {
            _packing = false;
        }
    }

    async Task HashPendingAsync(CancellationToken ct)
    {
        // Online-only files skipped for space this pass; re-checked on the next pass.
        var deferred = new HashSet<long>();
        var waitingForSpace = false;
        try
        {
            while (!lanes.IsPaused(Lane.Index))
            {
                HashSet<long> skip;
                lock (_lock) skip = [.. _unreadable.Keys, .. deferred];
                var batch = (await library.NextUnhashedAsync(options.HashBatch + skip.Count, includeOnlineOnly: true, ct))
                    .Where(f => !skip.Contains(f.LocationId))
                    .Take(options.HashBatch)
                    .ToList();
                if (batch.Count == 0) return;

                foreach (var file in batch)
                {
                    if (lanes.IsPaused(Lane.Index)) return;
                    if (file.OnlineOnly && files.Disk.FreeBytes(file.FullPath) is { } free && free - file.SizeBytes < options.OnlineOnlyReserveBytes)
                    {
                        deferred.Add(file.LocationId);
                        waitingForSpace = true;
                        continue;
                    }
                    if (options.Foreground is { } foreground) await foreground.WaitForQuietAsync(ct);
                    await HashFileAsync(file, ct);
                }
                // A changed file hashed back to its old content is that document's file again.
                await lanes.ReleaseReachableAsync(ct);
                lanes.Wake(Lane.Index);
                // A copy of a book already read is done with its file as soon as it is hashed.
                _returnSignal.Set();
                changed();
            }
        }
        finally
        {
            // Only this loop changes it, so it can't move between the look and the change.
            if (activity.Current.WaitingForDiskSpace != waitingForSpace)
            {
                activity.Change(a => a with { WaitingForDiskSpace = waitingForSpace });
                if (waitingForSpace) log.LogWarning("Online-only files are waiting: less than {Reserve} bytes would be free", options.OnlineOnlyReserveBytes);
                changed();
            }
        }
    }

    void ShowHashing(string? path) => activity.Change(a => a with { Hashing = path });

    async Task HashFileAsync(UnhashedFile file, CancellationToken ct)
    {
        if (file.IsArchive)
        {
            await HashArchiveAsync(file, ct);
            return;
        }
        ContentHash hash;
        ShowHashing(file.FullPath);
        changed();
        try
        {
            await files.Returns.BeforeReadAsync(file, ct);
            hash = await files.Hasher.HashAsync(file.FullPath, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("Could not read {LocationId} for hashing: {Reason}", file.LocationId, ex.Message);
            lock (_lock) _unreadable[file.LocationId] = new UnreadableFile(file.LocationId, file.FullPath, ex.Message);
            return;
        }
        finally
        {
            ShowHashing(null);
        }

        await AttachAsync(file, hash, ct);
    }

    async Task AttachAsync(UnhashedFile file, ContentHash hash, CancellationToken ct)
    {
        var attached = await library.AttachHashAsync(file, hash, ct);
        _packsDue = true;
        // Idempotent: a copy of a known document finds its Probe job already there.
        if (attached is { } document) await queue.EnqueueAsync(document.DocumentId, hash.Hex, Pipeline.First, ct: ct);
    }

    /// <summary>
    /// Reads a ZIP: its own hash, then the list of what it holds, then each new or changed PDF and image hashed as it
    /// decompresses. The ZIP counts as read only once every member is, so a pause part way picks up with the members
    /// still to do.
    /// </summary>
    async Task HashArchiveAsync(UnhashedFile archive, CancellationToken ct)
    {
        ShowHashing(archive.FullPath);
        changed();
        try
        {
            await files.Returns.BeforeReadAsync(archive, ct);
            var hash = await files.Hasher.HashAsync(archive.FullPath, ct);
            var members = await Task.Run(() => files.Archives.List(archive.FullPath), ct);
            if (await library.ReconcileArchiveAsync(archive, members, ct) is not { } pending) return;
            if (pending.Count > 0)
            {
                using var zip = files.Archives.Open(archive.FullPath);
                foreach (var member in pending)
                {
                    if (lanes.IsPaused(Lane.Index)) return;
                    ShowHashing(member.FullPath);
                    changed();
                    ContentHash memberHash;
                    try
                    {
                        await using var stream = ArchiveReader.OpenMember(zip, member.EntryPath!);
                        memberHash = await FileHasher.HashAsync(stream, ct);
                    }
                    catch (InvalidDataException ex)
                    {
                        log.LogWarning("A file inside ZIP {LocationId} is damaged: {Reason}", archive.LocationId, ex.Message);
                        await library.SetProblemAsync(member.LocationId, ArchiveReader.Damaged, ct);
                        continue;
                    }
                    await AttachAsync(member, memberHash, ct);
                }
            }
            await library.AttachArchiveHashAsync(archive, hash, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            var reason = ex is InvalidDataException ? "it is damaged, or isn't a ZIP Bibliotaph can read" : ex.Message;
            log.LogWarning("Could not read ZIP {LocationId}: {Reason}", archive.LocationId, ex.Message);
            lock (_lock) _unreadable[archive.LocationId] = new UnreadableFile(archive.LocationId, archive.FullPath, reason);
        }
        finally
        {
            ShowHashing(null);
        }
    }

    /// <summary>
    /// Gives files that reading downloaded back to online-only once their stages are done with them. While online-only
    /// files wait for space, a paused lane's waiting stages don't keep their files: those go back too, and download
    /// again when the lane resumes, so the space a paused lane would hold lets hashing go on. Space freed wakes the
    /// hasher, now and again once the sync app has had time to let the files go.
    /// </summary>
    public async Task ReturnLoopAsync(CancellationToken ct)
    {
        while (true)
        {
            await _returnSignal.WaitAsync(options.IdleRecheck, ct);
            var waitingForSpace = activity.Current.WaitingForDiskSpace;
            // A stage indexing doesn't run (the web server's OCR, for now) never comes, so it holds no file.
            IReadOnlyCollection<Stage> holding = waitingForSpace
                ? [.. Pipeline.FileStages.Where(s => lanes.Stages.Find(s) is { } stage && !lanes.IsPaused(stage.Lane))]
                : [.. Pipeline.FileStages.Where(s => lanes.Stages.Find(s) is not null)];
            if (await files.Returns.ReturnDoneAsync(holding, ct) == 0 || !waitingForSpace) continue;
            _hashSignal.Set();
            await Task.Delay(options.SpaceSettle, clock, ct);
            _hashSignal.Set();
        }
    }

    public void Dispose()
    {
        _hashSignal.Dispose();
        _returnSignal.Dispose();
    }
}
