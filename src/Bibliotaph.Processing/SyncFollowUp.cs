using Bibliotaph.Catalog;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>
/// What library sync does outside catalog.db after another device's work was applied: the user's own tags are guessed
/// here from this device's pages, packs made as another device made them are named after their folders here, a book
/// whose text was forgotten there is forgotten here (or read again), the files in a folder whose rules changed have
/// their hints read again, chosen cover pages are drawn, and the cover pictures of books owned elsewhere, chosen or
/// imported from an online library, are kept with this device's covers. Each is skipped where its service isn't here.
/// </summary>
public sealed class SyncFollowUp(UserTagService? userTags = null, MetadataHints? hints = null, ForgetText? forget = null, CoverPages? coverPages = null,
    CoverCache? covers = null, ILogger<SyncFollowUp>? log = null)
{
    readonly ILogger _log = log ?? NullLogger<SyncFollowUp>.Instance;

    /// <summary>The cover pictures chosen for books, by their entries' sync ids, as library sync names them.</summary>
    public IReadOnlyDictionary<Guid, byte[]> ReadChosenCovers() => covers?.ReadAllChosen() ?? new Dictionary<Guid, byte[]>();

    /// <summary>The covers books owned elsewhere were imported with, by their entries' sync ids, as library sync names them.</summary>
    public IReadOnlyDictionary<Guid, byte[]> ReadImportedCovers() => covers?.ReadAllImported() ?? new Dictionary<Guid, byte[]>();

    public async Task RunAsync(SyncFollowUps follow, CancellationToken ct)
    {
        if (follow.Tags.Count > 0 && userTags is not null) await userTags.RefreshAsync(follow.Tags, ct);
        if (follow.Packed.Count > 0 && hints is not null) await hints.StorePacksAsync(follow.Packed, ct);
        if (covers is not null)
        {
            foreach (var (syncId, jpeg) in follow.ChosenCovers)
            {
                if (jpeg is null) covers.DeleteChosen(syncId);
                else covers.KeepChosen(syncId, jpeg);
            }
            foreach (var (syncId, jpeg) in follow.ImportedCovers)
            {
                if (jpeg is null) covers.DeleteImported(syncId);
                else covers.KeepImported(syncId, jpeg);
            }
        }
        if (forget is not null)
        {
            foreach (var entryId in follow.Forget)
                if (!await forget.ForgetAsync(entryId, ct)) _log.LogInformation("Entry {EntryId} is still being read; its text goes when it lets go", entryId.Value);
            foreach (var entryId in follow.ReadAgain) await forget.ReadAgainAsync(entryId, ct);
        }
        if (hints is not null)
            foreach (var folder in follow.Folders) await hints.RefreshFolderAsync(folder, ct);
        if (coverPages is not null)
            foreach (var (documentId, page) in follow.CoverPages) await coverPages.UseAsync(documentId, page, ct);
    }
}
