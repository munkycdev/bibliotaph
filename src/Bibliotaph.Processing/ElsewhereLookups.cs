using System.Text.Json;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>
/// Looking up the books owned elsewhere, as those imported from D&amp;D Beyond or Demiplane are: they have no file, so
/// Look up, which goes file by file, never comes to them. Each is searched for by its title where a catalogue finds
/// books by title alone, and its records are kept as a file's are, under the name the book has on every PC
/// (<see cref="LookupRecordStore.KeyOf"/>), so they fill its fields, show in Found online and take the user's answers
/// the same way. With no pages to check a record against, the title its service gave stands in for them: a record is
/// used when its publisher is the book's too, and asked about in Needs review when only the name agrees. A book is
/// asked once for the catalogues there are and the title it has, and again when either changes.
/// </summary>
public sealed class ElsewhereLookups(BookLookups lookups, LookupRecordStore records, MetadataStore metadata, MetadataProjector projector, SettingsStore settings,
    ILogger<ElsewhereLookups>? log = null) : IDisposable
{
    /// <summary>What each book was last asked with, by its key: the catalogues and its title, as JSON.</summary>
    public const string AskedKey = "lookup.elsewhere-asked";

    readonly ILogger _log = log ?? NullLogger<ElsewhereLookups>.Instance;
    readonly SemaphoreSlim _running = new(1, 1);

    /// <summary>Ends a run with the library: it outlives whatever asked for it.</summary>
    readonly CancellationTokenSource _closing = new();

    public void Dispose()
    {
        _closing.Cancel();
        _closing.Dispose();
    }

    /// <summary>
    /// Looks the books up in the background, one run at a time: those not asked yet with the catalogues and title
    /// they have now; with <paramref name="again"/>, every one; with <paramref name="missedOnly"/>, only those no
    /// catalogue's record is used for.
    /// </summary>
    public void Start(bool again = false, bool missedOnly = false)
    {
        CancellationToken closing;
        try
        {
            closing = _closing.Token;
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        _ = Task.Run(() => RunQuietlyAsync(again, missedOnly, closing), CancellationToken.None);
    }

    async Task RunQuietlyAsync(bool again, bool missedOnly, CancellationToken ct)
    {
        try
        {
            await RunAsync(again, missedOnly, ct);
        }
        catch (OperationCanceledException)
        {
            // Closing: the books not reached are asked next time.
        }
        catch (ObjectDisposedException)
        {
            // Closed mid-run.
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Looking up the books owned elsewhere failed");
        }
    }

    /// <summary>As <see cref="Start"/>, awaited. Returns how many books were asked about.</summary>
    public async Task<int> RunAsync(bool again = false, bool missedOnly = false, CancellationToken ct = default)
    {
        if (!lookups.Catalogue.FindsByTitleAlone) return 0;
        await _running.WaitAsync(ct);
        try
        {
            var books = await records.GetFilelessAsync(ct);
            if (books.Count == 0) return 0;
            var asked = Read(await settings.GetAsync(AskedKey, ct));
            var found = again && missedOnly ? (await records.GetFoundByAsync([.. books.Select(b => b.EntryId)], ct)).Select(f => f.EntryId).ToHashSet() : [];
            var all = await metadata.GetManyAsync([.. books.Select(b => b.EntryId)], ct);
            var count = 0;
            foreach (var (entryId, key) in books)
            {
                ct.ThrowIfCancellationRequested();
                var effective = all.GetValueOrDefault(entryId)?.Compute();
                // The title the user or the service gave, not one a record suggested: that would search for the answer.
                if (effective?[MetadataFields.Title].Values.Concat(effective[MetadataFields.Title].Alternatives)
                        .FirstOrDefault(v => v.Origin != AssertionOrigin.Lookup)?.Value is not { Length: > 0 } title) continue;
                var stamp = $"{lookups.Catalogue.Name}|{BookRecords.Simplify(title)}";
                if (asked.GetValueOrDefault(key) == stamp && (!again || found.Contains(entryId))) continue;
                var publisher = effective[MetadataFields.Publisher].Values.FirstOrDefault(v => v.Origin != AssertionOrigin.Lookup)?.Value;
                try
                {
                    await AskAsync(entryId, key, title, publisher, ct);
                }
                catch (CatalogueUnavailableException ex)
                {
                    // The rest wait for the next run, as files wait for the catalogue.
                    _log.LogInformation("Stopped looking up the books owned elsewhere: {Why}", ex.Message);
                    break;
                }
                asked[key] = stamp;
                count++;
                await settings.SetAsync(AskedKey, JsonSerializer.Serialize(asked), ct);
            }
            return count;
        }
        finally
        {
            _running.Release();
        }
    }

    async Task AskAsync(EntryId entryId, string key, string title, string? publisher, CancellationToken ct)
    {
        var kept = await records.GetKeptAsync(key, ct);
        var refused = await records.GetRefusedAsync(key, ct);
        var answer = await lookups.Catalogue.AskAsync([], [title], [], ct: ct);
        // Several items of the name: which one it is, is asked.
        var several = (answer.Candidates ?? []).Where(c => !kept.Any(k => k.Source == c.Source) && !answer.Records.Any(r => r.Source == c.Source)
            && !refused.Contains((c.Source, c.Key)) && BookRecords.Simplify(c.Name) == BookRecords.Simplify(title)).Take(BookRecords.MostAsked + 1).ToList();
        List<BookRecord> picked = [];
        if (several.Count <= BookRecords.MostAsked)
            foreach (var item in several)
                if (await lookups.Catalogue.ReadAsync(item.Source, item.Key, ct) is { } read) picked.Add(read);
        List<BookRecord> found = [.. answer.Records.Where(BookRecords.ByTitle).Concat(picked)
            .Where(r => !kept.Any(k => k.Source == r.Source && k.Key != r.Key && k.Read))];
        var named = BookRecords.Simplify(publisher ?? "");
        await records.SaveAsync(key, found,
            r => !picked.Contains(r) && named.Length > 0 && BookRecords.IsNamedWholeIn(r, [title]) && r.Publishers.Any(p => BookRecords.Simplify(p) == named), ct);
        var (_, grew) = await records.ApplyAsync(entryId, key, ct);
        if (grew) await projector.ProjectVocabularyAsync(ct);
        await projector.ProjectAsync([entryId], ct);
    }

    static Dictionary<string, string> Read(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new(StringComparer.Ordinal);
        try
        {
            return new(JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [], StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new(StringComparer.Ordinal);
        }
    }
}
