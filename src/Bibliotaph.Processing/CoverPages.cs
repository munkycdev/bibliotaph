using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>
/// "Use this page as the cover": the page a file's card shows, for books whose first page is blank or a plain title
/// sheet. The choice is kept in catalog.db, so reading the file again keeps it, and the cover is drawn at once.
/// </summary>
public sealed class CoverPages(LibraryStore library, EntryStore entries, StageServices stages, ILogger<CoverPages>? log = null)
{
    readonly ILogger _log = log ?? NullLogger<CoverPages>.Instance;

    /// <summary>The page the document's cover shows: 0 unless the user chose another.</summary>
    public Task<int> GetAsync(long documentId, CancellationToken ct = default) => library.GetCoverPageAsync(documentId, ct);

    /// <summary>
    /// Makes <paramref name="page"/> the document's cover, or the first page again for 0. False when the cover couldn't
    /// be drawn now; the choice is kept either way, and the Covers stage draws it when the file is read again.
    /// </summary>
    public async Task<bool> UseAsync(long documentId, int page, CancellationToken ct = default)
    {
        await library.SetCoverPageAsync(documentId, page, ct);
        if ((await entries.GetEntryAsync(documentId, ct))?.ContentHash is not { } contentHash) return false;
        try
        {
            var (session, _) = await StageHelpers.OpenPdfAsync(stages, new JobRecord(0, documentId, contentHash, Stage.Covers, 0, 0), ct);
            if (session is null) return false;
            await using (session)
            {
                if (page < 0 || page >= session.Doc.PageCount) page = 0;
                if (await StageHelpers.DrawCoverAsync(stages, session, page, ct) is not { } jpeg) return false;
                var key = page > 0 ? CoverCache.PageKey(contentHash, page) : contentHash;
                await stages.Index.SetCoverAsync(documentId, stages.Covers.Write(key, jpeg), ct);
            }
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "The cover of document {DocumentId} couldn't be drawn now", documentId);
            return false;
        }
    }
}
