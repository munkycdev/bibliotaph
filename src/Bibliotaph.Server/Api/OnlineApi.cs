using System.Security.Claims;
using System.Text;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Processing;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;
using Microsoft.AspNetCore.Http.Features;

namespace Bibliotaph.Server.Api;

/// <summary>
/// Settings › Online libraries on the web: a library page the user saved from the service, read by the Windows app's
/// readers, its preview, the images saved with it for covers, and the import with Undo. A browser page can't hold the
/// service's own site as the Windows app's browser does, so a saved page is how a library arrives; the server never
/// visits the service.
/// </summary>
public static class OnlineApi
{
    /// <summary>A library page bigger than this isn't one: D&amp;D Beyond's, with its whole catalogue, is a few megabytes.</summary>
    const long MostPageBytes = 40L * 1024 * 1024;

    const long MostImageBytes = 8L * 1024 * 1024;

    public static void MapOnlineApi(this RouteGroupBuilder api)
    {
        api.MapGet("/online/services", () => OnlineImports.Services.Select(r => new OnlineService(r.Key, r.Name, r.LibraryPage.AbsoluteUri, Explanation(r))).ToList());

        api.MapPost("/online/pages", async (HttpContext context, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            var body = await ReadAsync(context, MostPageBytes, ct);
            if (body is null) return new OnlinePageRead(null, "That file is too big to be a library page.");
            var html = Encoding.UTF8.GetString(body);
            if (OnlineImports.Services.Select(r => (Reader: r, Page: r.Read(html))).FirstOrDefault(r => r.Page is not null) is not ({ } reader, { } page))
                return new OnlinePageRead(null, OnlineImports.Services.Select(r => r.Hint(html)).FirstOrDefault(h => h is not null) ?? OnlineImportText.Unreadable);
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var held = lease.Library.Get<HeldOnlinePages>();
            var token = held.Keep(reader, page);
            return new OnlinePageRead(await PreviewAsync(lease.Library, token, held.Find(token)!, ct), null);
        });

        api.MapGet("/online/pages/{token}", async (string token, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return lease.Library.Get<HeldOnlinePages>().Find(token) is { } held ? Results.Ok(await PreviewAsync(lease.Library, token, held, ct)) : Gone();
        });

        // An image saved with the page, kept by its file name for the covers that name it.
        api.MapPut("/online/pages/{token}/images/{name}", async (string token, string name, HttpContext context, ClaimsPrincipal principal,
            UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            if (lease.Library.Get<HeldOnlinePages>().Find(token) is not { } held) return Gone();
            var body = await ReadAsync(context, MostImageBytes, ct);
            if (body is not null && !held.AddImage(name, body))
                return Results.Problem("That's as many pictures as one page keeps.", statusCode: StatusCodes.Status400BadRequest);
            return Results.Ok(await PreviewAsync(lease.Library, token, held, ct));
        });

        api.MapPost("/online/pages/{token}/import", async (string token, OnlineImportChoice choice, ClaimsPrincipal principal, UserLibraries libraries,
            CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            if (library.Get<HeldOnlinePages>().Find(token) is not { } held) return Gone();
            var imports = library.Get<OnlineImports>();
            var plan = await imports.PlanAsync(held.Reader, held.Page, ct);
            var chosen = choice.Rows.Where(i => i >= 0 && i < plan.Rows.Count).Distinct().Select(i => plan.Rows[i]).ToList();
            var result = await imports.ImportAsync(plan, chosen, held.CoverAsync, ct);
            var undo = EditApi.Hold(library, () => imports.UndoAsync(result.Undo, CancellationToken.None), "The import couldn't be undone.");
            return Results.Ok(new OnlineImported(new ActionResult(OnlineImportText.Imported(held.Reader, result.Added, result.Changed), undo),
                await PreviewAsync(library, token, held, ct)));
        });
    }

    /// <summary>
    /// What Settings says of a service on the web: where to save the page from, and what its books become. The Windows
    /// app's says the same of its own browser.
    /// </summary>
    static string Explanation(IOnlineLibraryReader reader) => reader.LinkField == MetadataFields.AlsoOwn
        ? $"Open your library on {reader.Name}, signing in if it asks, and save the page with Save as, choosing Webpage, complete. Import that "
            + $"page here: only books you own are imported, each opening on {reader.Name}, and Bibliotaph never sees your password."
        : $"Open your library on {reader.Name}, signing in if it asks, and save the page with Save as, choosing Webpage, complete. Import that "
            + $"page here: books you haven't downloaded become books to get from {reader.Name}."
            + (reader is DriveThruRpgReader ? " Your DMs Guild books are in the same library." : "")
            + (reader.Hint("") is { } hint ? $" {hint}" : "");

    /// <summary>The page's preview as the library stands now, in the Windows app's groups and words.</summary>
    static async Task<OnlinePreview> PreviewAsync(UserLibrary library, string token, HeldOnlinePages.HeldPage held, CancellationToken ct)
    {
        var plan = await library.Get<OnlineImports>().PlanAsync(held.Reader, held.Page, ct);
        var indexed = plan.Rows.Select((row, index) => (Row: row, Index: index)).ToList();
        var groups = indexed.GroupBy(r => r.Row.Outcome).OrderBy(g => g.Key)
            .Select(g => new OnlineGroup(OnlineImportText.GroupHeading(g.Key, g.Count()), OnlineImportText.GroupExplanation(g.Key, held.Reader),
                [.. g.Select(r => new OnlineRow(r.Index, r.Row.Book.Title, OnlineImportText.Detail(r.Row), r.Row.Outcome != OnlineOutcome.Unchanged))]))
            .ToList();
        // Covers count only for the books that would get one: those new to the library.
        var covers = plan.Rows.Where(r => r.Outcome == OnlineOutcome.New && r.Book.CoverUrl is not null)
            .Select(r => OnlineImports.CoverFileName(r.Book.CoverUrl!)).OfType<string>().ToList();
        return new OnlinePreview(token, held.Reader.Name, OnlineImportText.Summary(held.Reader, plan.Rows.Count, held.Page.Shared), groups,
            [.. plan.Missing.Select(m => new OnlineMissing(m.EntryId.Value, m.Title))], covers.Count, covers.Count(held.HasImage));
    }

    static IResult Gone() => Results.Problem("That page has gone from Bibliotaph. Import it again.", statusCode: StatusCodes.Status404NotFound);

    /// <summary>The request's body, or null when it is bigger than <paramref name="most"/>.</summary>
    static async Task<byte[]?> ReadAsync(HttpContext context, long most, CancellationToken ct)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = most;
        if (context.Request.ContentLength > most) return null;
        using var body = new MemoryStream();
        try
        {
            await context.Request.Body.CopyToAsync(body, ct);
        }
        catch (BadHttpRequestException)
        {
            return null;
        }
        return body.ToArray();
    }
}
