using System.Security.Claims;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Pdf.Contracts;
using Bibliotaph.Processing;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;

namespace Bibliotaph.Server.Api;

/// <summary>
/// Check a download on the web: the browser reads the folder or ZIP picked, hashes each file and fingerprints a PDF's
/// pages, and sends only those, one file at a time, for the Windows app's verdict. Nothing is kept.
/// </summary>
public static class CheckApi
{
    public static void MapCheckApi(this RouteGroupBuilder api)
    {
        api.MapPost("/library/check", async (CheckFile file, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            var item = new CheckItem(file.Location, Problem: file.Problem);
            CheckedFile result;
            if (file.Problem is not null) result = new CheckedFile(item, CheckVerdict.Unreadable, file.Problem);
            else if (!ContentHash.TryParse(file.Hash, out var hash)) return Results.BadRequest();
            else
            {
                var vocabulary = await library.Get<VocabularyStore>().GetAsync(ct);
                result = await library.Get<DownloadCheck>().JudgeAsync(item, hash, Pages(file.Pages), file.RelativePath, vocabulary, ct);
            }
            var where = result.MatchWhere is { } path ? FolderText.Location(path, library.Drives) : null;
            return Results.Ok(new CheckResult((int)result.Verdict, DownloadCheckText.Label(result.Verdict), DownloadCheckText.Explanation(result.Verdict),
                DownloadCheckText.Detail(result with { MatchWhere = where }) is { Length: > 0 } detail ? detail : null,
                result.MatchEntryId?.Value, result.MatchDocumentId, result.MatchTitle, result.Verdict == CheckVerdict.AlreadyOwned));
        });
    }

    static PageReading? Pages(CheckPages? pages) => pages switch
    {
        null => null,
        { Unread: "password" } => PageReading.Unread(PageReading.NeedsPassword),
        { Unread: not null } => PageReading.Unread(PageReading.NotReadable),
        _ => PageReading.Read(pages.Fingerprints, pages.PagesWithText,
            new DocMetadata { Title = pages.Title, Author = pages.Author, Subject = pages.Subject, Keywords = pages.Keywords }),
    };
}
