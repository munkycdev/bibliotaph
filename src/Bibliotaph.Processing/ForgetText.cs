using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>
/// "Forget its text" and "Read it again" (slice 4i plan, choice 6), the spec's purge of derived content, for a book
/// the user would rather not have readable on disk. Forgetting removes what reading the book made (its pages and their
/// search text, its cover and the AI's results) and stops indexing it; the user's decision is kept in catalog.db, so
/// it outlasts a rebuilt index. The file itself is never touched, and the book keeps its card and catalog details.
/// </summary>
public sealed class ForgetText(
    EntryStore entries,
    LibraryStore library,
    IndexStore index,
    IndexingService indexing,
    CoverCache covers,
    ClassificationStore classifications,
    MetadataStore metadata,
    MetadataProjector projector,
    ILogger<ForgetText>? log = null)
{
    readonly ILogger _log = log ?? NullLogger<ForgetText>.Instance;

    /// <summary>How long forgetting waits for an interrupted stage to let go of the book.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Forgets the text of every copy of an entry's book. Returns false if a stage reading one didn't stop in time;
    /// the copies stay marked forgotten either way, so nothing reads them again, and forgetting again finishes it.
    /// </summary>
    public async Task<bool> ForgetAsync(EntryId entryId, CancellationToken ct = default)
    {
        var copies = await entries.GetCopiesAsync(entryId, ct);
        var documentIds = copies.Select(c => c.DocumentId).Distinct().ToList();
        if (documentIds.Count == 0) return true;
        var hashes = copies.Select(c => c.ContentHash).Distinct().ToList();

        // The flag first: from here no stage starts on these copies, and any running is stopped.
        await library.SetTextForgottenAsync(documentIds, forgotten: true, ct);
        indexing.Interrupt(documentIds);
        var purged = true;
        foreach (var documentId in documentIds) purged &= await PurgeAsync(documentId, ct);

        foreach (var hash in hashes) DeleteCover(hash);
        await classifications.ForgetAsync(hashes, ct);
        await metadata.ForgetAiAsync(entryId, hashes, ct);
        await projector.ProjectAsync([entryId], ct);
        _log.LogInformation("Forgot the text of entry {EntryId} ({Count} copies)", entryId.Value, documentIds.Count);
        return purged;
    }

    /// <summary>Undoes <see cref="ForgetAsync"/>: every copy is read again from the start, ahead of other work.</summary>
    public async Task ReadAgainAsync(EntryId entryId, CancellationToken ct = default)
    {
        var documentIds = (await entries.GetCopiesAsync(entryId, ct)).Select(c => c.DocumentId).Distinct().ToList();
        await library.SetTextForgottenAsync(documentIds, forgotten: false, ct);
        foreach (var documentId in documentIds) await indexing.ReprocessAsync(documentId, ct: ct);
        _log.LogInformation("Reading entry {EntryId} again", entryId.Value);
    }

    /// <summary>Removes a document's derived rows once no stage holds it: an interrupted one lets go at its next page.</summary>
    async Task<bool> PurgeAsync(long documentId, CancellationToken ct)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!await index.ForgetTextAsync(documentId, ct))
        {
            if (waited.Elapsed >= Patience)
            {
                _log.LogWarning("A stage reading document {DocumentId} didn't stop; its text is forgotten when it does", documentId);
                return false;
            }
            indexing.Interrupt([documentId]);
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }
        return true;
    }

    void DeleteCover(string contentHash)
    {
        try
        {
            covers.Delete(contentHash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Shown nowhere now (its row has no cover), and replaced if the book is read again.
            _log.LogWarning("A forgotten book's cover couldn't be deleted: {Message}", ex.Message);
        }
    }
}
