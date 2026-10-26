using System.Globalization;
using System.Security.Claims;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;
using Bibliotaph.Server.Storage;
using Bibliotaph.Storage.OneDrive;

namespace Bibliotaph.Server.Api;

/// <summary>/api/storage and /api/library/folders: connected storage, and the library folders picked from it.</summary>
public static class StorageApi
{
    public static void MapStorageApi(this RouteGroupBuilder api)
    {
        api.MapGet("/storage", async (ClaimsPrincipal principal, StorageConnections connections, CancellationToken ct) =>
            (await connections.ListAsync(principal.UserId()!, ct)).Select(Account));

        api.MapDelete("/storage/{id}", async (string id, ClaimsPrincipal principal, StorageConnections connections, DriveTokens tokens,
            DriveNotifications notifications, UserLibraries libraries, CancellationToken ct) =>
        {
            var userId = principal.UserId()!;
            // Cancel notifications while the token still works, then forget the token.
            if (await connections.FindAsync(id, ct) is { } existing && existing.UserId == userId) await notifications.UnsubscribeAsync(existing, ct);
            if (await connections.RemoveAsync(userId, id, ct) is null) return Results.NotFound();
            tokens.Forget(id);
            await libraries.DrivesChangedAsync(userId);
            return Results.NoContent();
        });

        api.MapGet("/storage/{id}/folders", async (string id, string? path, ClaimsPrincipal principal, StorageConnections connections,
            UserDriveFactory drives, UserLibraries libraries, CancellationToken ct) =>
        {
            var userId = principal.UserId()!;
            var account = (await connections.ListAsync(userId, ct)).FirstOrDefault(a => a.Id == id);
            if (account is null) return Results.NotFound();
            var folder = (path ?? "").Trim('/');
            IReadOnlyList<DriveEntry>? entries;
            try
            {
                entries = await drives.ForConnection(id).ChildrenAsync(account.AccountId, folder, ct);
            }
            catch (OneDriveException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
            }
            if (entries is null) return Results.NotFound();

            await using var lease = await libraries.OpenAsync(userId, ct);
            var roots = (await lease.Library.Get<SourceRootStore>().ListAsync(ct)).Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var folders = entries.Where(e => e.IsFolder).OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(e =>
                {
                    var inner = folder.Length == 0 ? e.Name : $"{folder}/{e.Name}";
                    return new StorageFolder(e.Name, inner, roots.Contains(DrivePath.Of(account.AccountId, inner)));
                })
                .ToList();
            return Results.Ok(new StorageFolderList(id, folder, folders, entries.Count(e => !e.IsFolder)));
        });

        api.MapGet("/library/folders", async (ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return await FoldersAsync(lease.Library, ct);
        });

        api.MapPost("/library/folders", async (AddLibraryFolder request, ClaimsPrincipal principal, StorageConnections connections,
            UserLibraries libraries, CancellationToken ct) =>
        {
            var userId = principal.UserId()!;
            var account = (await connections.ListAsync(userId, ct)).FirstOrDefault(a => a.Id == request.StorageId);
            if (account is null) return Results.NotFound();
            await using var lease = await libraries.OpenAsync(userId, ct);
            var roots = lease.Library.Get<SourceRootStore>();
            var path = DrivePath.Of(account.AccountId, request.Path);
            var existing = await roots.ListAsync(ct);
            // As in the Windows app, a folder inside or around another library folder would read its files twice.
            if (existing.Any(r => r.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                return Results.Problem("That folder is already in your library.", statusCode: StatusCodes.Status409Conflict);
            var root = await roots.AddAsync(path, ct);
            lease.Library.Indexing.RequestScan(root.Id);
            return Results.Ok(await FoldersAsync(lease.Library, ct));
        });

        api.MapDelete("/library/folders/{id:long}", async (long id, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            await lease.Library.Get<SourceRootStore>().RemoveAsync(id, ct);
            return Results.Ok(await FoldersAsync(lease.Library, ct));
        });

        api.MapGet("/library/status", async (ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return await StatusAsync(lease.Library, ct);
        });

        api.MapPost("/library/rescan", async (ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            lease.Library.Indexing.RequestScan();
            return Results.NoContent();
        });
    }

    static StorageAccount Account(ConnectedAccount a) =>
        new(a.Id, a.Provider, a.DisplayName ?? OneDriveProvider.Name, a.NeedsReconnect, new DateTimeOffset(a.Created, TimeSpan.Zero));

    static async Task<IReadOnlyList<LibraryFolder>> FoldersAsync(UserLibrary library, CancellationToken ct)
    {
        var scans = library.Indexing.Scans.ToDictionary(s => s.RootId);
        var store = library.Get<LibraryStore>();
        var queries = library.Get<IndexQueries>();
        var folders = new List<LibraryFolder>();
        foreach (var root in await library.Get<SourceRootStore>().ListAsync(ct))
        {
            var ids = await store.GetVisibleEntryIdsAsync(root.Id, ct);
            var searchable = await queries.CountSearchableAsync(ids, ct);
            var offline = root.Availability == SourceRootAvailability.Offline;
            folders.Add(new LibraryFolder(root.Id, FolderText.Name(root.Path), FolderText.Location(root.Path, library.Drives),
                FolderText.Describe(root.AddedUtc, offline, scans.GetValueOrDefault(root.Id), searchable, ids.Count), offline));
        }
        return folders;
    }

    internal static async Task<LibraryStatus> StatusAsync(UserLibrary library, CancellationToken ct)
    {
        var indexing = library.Indexing;
        var counts = await library.Get<LibraryStore>().GetCountsAsync(ct);
        var folders = (await library.Get<SourceRootStore>().ListAsync(ct)).Count;
        var unhashed = counts.Unhashed - indexing.Unreadable.Count;
        var progress = await library.Get<IndexQueries>().GetProgressAsync(ct);
        var text = indexing.IsScanning ? "Looking through your folders"
            : unhashed > 0 && indexing.IsPaused(Lane.Index) ? "Indexing paused"
            : unhashed > 0 ? $"Reading {unhashed:N0} new {FolderText.Plural(unhashed, "file", "files")}"
            : progress.PagesAwaitingOcr > 0 ? indexing.IsPaused(Lane.Ocr) ? "Reading scanned pages paused" : $"Reading scanned pages · {progress.PagesAwaitingOcr:N0} left"
            : counts.Documents == 0 ? "Nothing to process"
            : $"Up to date · {counts.Documents:N0} {FolderText.Plural(counts.Documents, "item", "items")}";
        var suggestions = await library.Get<ReviewService>().CountAsync(ct);
        var attention = progress.NeedAttention + indexing.Unreadable.Count + counts.Problems;
        return new LibraryStatus(text, await library.IsBusyAsync(ct), counts.Documents, folders, (int)suggestions, (int)attention);
    }
}

/// <summary>How library folders read, in the Windows app's words.</summary>
static class FolderText
{
    public static string Name(string path) =>
        DrivePath.TryParse(path, out _, out var inner) ? inner.Length == 0 ? "OneDrive" : inner[(inner.LastIndexOf('/') + 1)..] : Path.GetFileName(path);

    /// <summary>"OneDrive · Dana › RPG › Adventures": the drive, then the folders down to this one.</summary>
    public static string Location(string path, UserDrives drives)
    {
        if (!DrivePath.TryParse(path, out var driveId, out var inner)) return path;
        var drive = drives.Account(driveId)?.DisplayName ?? "OneDrive (not connected)";
        return inner.Length == 0 ? drive : $"{drive} › {inner.Replace("/", " › ", StringComparison.Ordinal)}";
    }

    public static string Describe(DateTime addedUtc, bool offline, RootScan? scan, long searchable, int documents)
    {
        var added = addedUtc.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
        if (offline || scan is { Reachable: false })
            return $"Added {added} · Can't be reached right now. Its books stay in your library.";
        if (scan is null) return $"Added {added} · Waiting to be looked at";
        var state = documents == 0 ? "" : searchable >= documents ? "Up to date · " : $"{searchable:N0} of {documents:N0} searchable · ";
        return $"Added {added} · {state}{DescribeContents(scan.Summary!)}";
    }

    /// <summary>"488 PDFs, 12 e-books, 120 images, 3 ZIPs · 15 other files ignored".</summary>
    static string DescribeContents(ScanSummary summary)
    {
        var pdfs = summary.ByFormat.GetValueOrDefault(SourceFormats.Pdf);
        var ebooks = SourceFormats.Ebooks.Sum(f => summary.ByFormat.GetValueOrDefault(f));
        var images = summary.Indexable - pdfs - ebooks;
        var parts = new List<string>
        {
            summary.Indexable + summary.Archives == 0 ? "No PDFs, e-books or images"
                : string.Join(", ", new[] { Count(pdfs, "PDF", "PDFs"), Count(ebooks, "e-book", "e-books"), Count(images, "image", "images"), Count(summary.Archives, "ZIP", "ZIPs") }
                    .Where(p => p.Length > 0)),
        };
        if (summary.Unsupported > 0) parts.Add($"{Count(summary.Unsupported, "other file", "other files")} ignored");
        if (summary.Inaccessible.Count > 0) parts.Add($"{Count(summary.Inaccessible.Count, "folder", "folders")} couldn't be opened");
        return string.Join(" · ", parts);
    }

    static string Count(long count, string one, string many) =>
        count == 0 ? "" : $"{count.ToString("N0", CultureInfo.InvariantCulture)} {Plural(count, one, many)}";

    public static string Plural(long count, string one, string many) => count == 1 ? one : many;
}
