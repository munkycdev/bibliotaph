using System.Security.Claims;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Core;
using Bibliotaph.Processing;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;

namespace Bibliotaph.Server.Api;

/// <summary>
/// Goes with, as the details drawer and Select's Edit metadata change it: the book picker, a card's book chosen, kept
/// or refused, and a book's card taken away from it. Each change answers with what it did and the token that undoes it.
/// </summary>
public static class SourceBooksApi
{
    public static void MapSourceBooksApi(this RouteGroupBuilder api)
    {
        // The picker: books whose title or details have the words, or those opened lately; never the cards in "not".
        api.MapPost("/library/source-books", async (BookSearch search, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var excluding = search.Not.Select(id => new EntryId(id)).ToList();
            var found = await lease.Library.Get<SourceBooksService>().FindBooksAsync(search.Text, excluding, ct: ct);
            return Results.Ok(found.Select(ToChoice).ToList());
        });

        var book = api.MapGroup("/library/books/{entryId:long}/source");

        book.MapPut("", async (long entryId, SourceBookEdit edit, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var books = lease.Library.Get<SourceBooksService>();
            return Results.Ok(Done(lease.Library, books, await books.LinkAsync([new EntryId(entryId)], new EntryId(edit.Book), ct)));
        });

        book.MapPost("/keep", async (long entryId, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var books = lease.Library.Get<SourceBooksService>();
            return Results.Ok(Done(lease.Library, books, await books.ConfirmAsync([new EntryId(entryId)], ct)));
        });

        // Not from this book: the link goes, and the rules won't suggest that book again.
        book.MapDelete("", async (long entryId, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var books = lease.Library.Get<SourceBooksService>();
            return Results.Ok(Done(lease.Library, books, await books.UnlinkAsync([new EntryId(entryId)], refuse: true, ct)));
        });
    }

    internal static ActionResult Done(UserLibrary library, SourceBooksService books, SourceBookChange change) =>
        new(change.Message, change.Changed.Count == 0 ? null
            : EditApi.Hold(library, () => books.UndoAsync(change.Undo, CancellationToken.None), "The change couldn't be undone."));

    public static BookChoice ToChoice(SourceBookChoice choice) => new(choice.EntryId.Value, choice.Title, choice.Detail);

    public static DetailGoesWith ToGoesWith(SourceBookDetails details) => new(
        details.CanGoWith,
        details.GoesWith is { } link ? new DetailSourceBook(link.Book.Value, link.Title, link.How, link.Confirmed) : null,
        [.. details.Proposed.Select(ToChoice)],
        [.. details.Companions.Select(c => new DetailCompanion(c.EntryId.Value, c.Title, c.How))],
        SourceBookText.CompanionsHeading(details.Companions.Count));
}
