using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>
/// Cover images the user chooses for books owned elsewhere, which have no file to draw one from. The image is shrunk
/// to cover size and kept with the user's data, never the original, so moving or deleting the picture changes nothing.
/// </summary>
public sealed class ChosenCovers(CoverCache covers, IImageCodec images, EntryStore entries, MetadataProjector projector,
    ILogger<ChosenCovers>? log = null)
{
    /// <summary>A picture bigger than this is more likely a mistake than a cover.</summary>
    public const long MaxBytes = 50L * 1024 * 1024;

    /// <summary>The picker's filter: what <see cref="IImageCodec"/> can read.</summary>
    public const string FileFilter = "Images (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp";

    readonly ILogger _log = log ?? NullLogger<ChosenCovers>.Instance;

    /// <summary>Makes <paramref name="path"/> the book's cover, or says why it can't be.</summary>
    public async Task<string?> ChooseAsync(EntryId entryId, string path, CancellationToken ct = default)
    {
        var (jpeg, problem) = await ReadAsync(path, ct);
        return jpeg is null ? problem : await KeepAsync(entryId, jpeg, ct);
    }

    /// <summary>
    /// The picture at <paramref name="path"/> at cover size, or why it can't be a cover. The Add dialog reads it when
    /// it's picked, so a problem shows before the book is made.
    /// </summary>
    public async Task<(byte[]? Jpeg, string? Problem)> ReadAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (new FileInfo(path).Length > MaxBytes) return (null, TooBig);
            await using var stream = File.OpenRead(path);
            return Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogInformation("A chosen cover couldn't be read: {Message}", ex.Message);
            return (null, "Bibliotaph couldn't open that picture.");
        }
    }

    public const string TooBig = "That picture is too big to use as a cover.";

    /// <summary>A picture sent from a browser, at cover size, or why it can't be a cover; the caller keeps it under <see cref="MaxBytes"/>.</summary>
    public (byte[]? Jpeg, string? Problem) Read(Stream stream) =>
        images.Thumbnail(stream, CoverCache.Width) is { } jpeg ? (jpeg, null) : (null, "That file isn't a picture Bibliotaph can read.");

    /// <summary>Makes a picture <see cref="ReadAsync"/> read the book's cover, or says why it can't be.</summary>
    public async Task<string?> KeepAsync(EntryId entryId, byte[] jpeg, CancellationToken ct = default)
    {
        if (await SyncIdAsync(entryId, ct) is not { } syncId) return "Only a book you added by hand can have a cover chosen for it.";
        covers.KeepChosen(syncId, jpeg);
        await projector.ProjectAsync([entryId], ct);
        return null;
    }

    /// <summary>Takes the chosen cover away: the book shows its online library's cover again, or none.</summary>
    public async Task RemoveAsync(EntryId entryId, CancellationToken ct = default)
    {
        if (await SyncIdAsync(entryId, ct) is not { } syncId) return;
        covers.DeleteChosen(syncId);
        await projector.ProjectAsync([entryId], ct);
    }

    /// <summary>Whether the book shows a cover the user chose.</summary>
    public async Task<bool> HasChosenAsync(EntryId entryId, CancellationToken ct = default) =>
        await SyncIdAsync(entryId, ct) is { } syncId && covers.FindChosen(syncId) is not null;

    async Task<Guid?> SyncIdAsync(EntryId entryId, CancellationToken ct) =>
        (await entries.GetCurrentAsync([entryId], ct)).FirstOrDefault(e => e.EntryId == entryId)?.SyncId;
}
