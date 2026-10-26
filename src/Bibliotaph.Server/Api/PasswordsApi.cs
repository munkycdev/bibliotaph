using System.Security.Claims;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;

namespace Bibliotaph.Server.Api;

/// <summary>
/// A locked PDF's password, as the Windows reader keeps it: the reader tries a remembered one first, forgets it when
/// it no longer opens the book, and sends one the user typed with the dialog's ticks, so indexing can read the book
/// and, when asked, the next open needn't ask. Settings › Passwords lists the books with one remembered, never the
/// passwords, and forgets them.
/// </summary>
public static class PasswordsApi
{
    public static void MapPasswordsApi(this RouteGroupBuilder api)
    {
        var document = api.MapGroup("/library/documents/{documentId:long}/password");

        document.MapGet("", async (long documentId, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return await HashAsync(lease.Library, documentId, ct) is { } hash
                ? Results.Ok(new ReaderPassword(lease.Library.Get<ServerPdfPasswords>().Remembered(hash)))
                : Results.NotFound();
        });

        document.MapPost("", async (long documentId, ReaderPasswordUse use, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(use.Password)) return Results.BadRequest();
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            if (await HashAsync(library, documentId, ct) is not { } hash) return Results.NotFound();
            await library.Get<ServerPdfPasswords>().KeepAsync(hash, use.Password, use.Remember, use.Searchable, ct);
            // Indexing can read the book now, so its stages blocked on the password run again.
            if (use.Remember || use.Searchable) await library.Indexing.RetryAsync(documentId, ct);
            return Results.NoContent();
        });

        // The remembered password no longer opens the book: it changed, or another file now has this content.
        document.MapDelete("", async (long documentId, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            if (await HashAsync(lease.Library, documentId, ct) is not { } hash) return Results.NotFound();
            await lease.Library.Get<ServerPdfPasswords>().ForgetAsync(hash, ct);
            return Results.NoContent();
        });

        var remembered = api.MapGroup("/library/passwords");

        remembered.MapGet("", async (ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return await ListAsync(lease.Library, ct);
        });

        remembered.MapDelete("/{contentHash}", async (string contentHash, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            await lease.Library.Get<ServerPdfPasswords>().ForgetAsync(contentHash, ct);
            return await ListAsync(lease.Library, ct);
        });

        remembered.MapDelete("", async (ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            await lease.Library.Get<ServerPdfPasswords>().ForgetAllAsync(ct);
            return await ListAsync(lease.Library, ct);
        });
    }

    static async Task<string?> HashAsync(UserLibrary library, long documentId, CancellationToken ct) =>
        await library.Get<LibraryStore>().GetSourceAsync(documentId, ct) is { Format: SourceFormats.Pdf } source ? source.ContentHash : null;

    static async Task<IReadOnlyList<RememberedPassword>> ListAsync(UserLibrary library, CancellationToken ct)
    {
        var hashes = library.Get<ServerPdfPasswords>().List();
        var titles = await library.Get<IndexQueries>().GetTitlesByHashAsync([.. hashes], ct);
        return [.. hashes
            .Select(h => new RememberedPassword(h, titles.TryGetValue(h, out var title) ? title : "A book not in your library now"))
            .OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase)];
    }
}
