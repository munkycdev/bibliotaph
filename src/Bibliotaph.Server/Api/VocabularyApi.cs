using System.Security.Claims;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Processing;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;

namespace Bibliotaph.Server.Api;

/// <summary>
/// /api/vocabulary and /api/tags: Settings › Vocabulary, on the Windows app's services. A vocabulary edit changes the
/// catalog at once and the library catches up in the background once edits pause; saving a tag looks through the
/// library's text before it answers.
/// </summary>
public static class VocabularyApi
{
    public static void MapVocabularyApi(this RouteGroupBuilder api)
    {
        var vocabulary = api.MapGroup("/vocabulary");

        vocabulary.MapGet("/{key}", async (string key, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            if (!Known(key)) return Results.NotFound();
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var terms = await lease.Library.Get<VocabularyService>().ListAsync(key, ct);
            return Results.Ok(terms.Select(Term).ToList());
        });

        vocabulary.MapPost("/{key}", async (string key, NewTerm term, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            if (!Known(key)) return Results.NotFound();
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return Results.Ok(Change(await lease.Library.Get<VocabularyService>().AddTermAsync(key, term.Label, ct)));
        });

        // Only the vocabularies the AI can add to have the choice; editions never do.
        vocabulary.MapGet("/{key}/ai-terms", async (string key, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            if (!AiTerms.Extends(key)) return Results.NotFound();
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return Results.Ok(new AiTermChoice(await lease.Library.Get<VocabularyService>().AddsAiTermsAsync(key, ct)));
        });

        // Choosing Add them adds the terms waiting in Needs review too, so the answer waits for them.
        vocabulary.MapPut("/{key}/ai-terms", async (string key, AiTermChoice choice, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            if (!AiTerms.Extends(key)) return Results.NotFound();
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var added = await lease.Library.Get<VocabularyService>().SetAddsAiTermsAsync(key, choice.Add, ct);
            return Results.Ok(new AiTermChange(choice.Add, added));
        });

        vocabulary.MapGet("/{key}/overlaps", async (string key, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            if (!Known(key)) return Results.NotFound();
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var service = lease.Library.Get<VocabularyService>();
            var proposals = await service.FindOverlapsAsync(key, ct);
            var kept = await service.KeptApartAsync(key, ct);
            return Results.Ok(new TermOverlapPage(
                [.. proposals.Select(p => new TermOverlapGroup([.. p.Terms.Select(t => new TermOverlapMember(t.Id, t.Label, t.Books))], p.Reason, p.Sure, p.KeepId))],
                [.. kept.Select(k => new KeptApartTerms(k.Id, k.First, k.Second))]));
        });

        vocabulary.MapPost("/{key}/combine", async (string key, CombineTerms combine, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            if (!Known(key)) return Results.NotFound();
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var service = lease.Library.Get<VocabularyService>();
            TermCombination combined;
            try
            {
                combined = await service.CombineAsync(combine.KeepId, combine.MergeIds, ct);
            }
            catch (ArgumentException e)
            {
                return Results.BadRequest(new ActionResult(e.Message));
            }
            return Results.Ok(new ActionResult(combined.Note, EditApi.Hold(lease.Library, () => service.UndoCombineAsync(combined, CancellationToken.None), OverlapText.UndoFailed)));
        });

        vocabulary.MapPost("/{key}/keep-apart", async (string key, KeepTermsApart apart, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            if (!Known(key)) return Results.NotFound();
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var service = lease.Library.Get<VocabularyService>();
            var listed = (await service.ListAsync(key, ct)).ToDictionary(e => e.Id);
            var terms = apart.Ids.Where(listed.ContainsKey).Select(id => listed[id]).ToList();
            var ids = await service.KeepApartAsync(key, [.. terms.Select(e => e.Key)], ct);
            return Results.Ok(new ActionResult(OverlapText.KeptApartNote([.. terms.Select(e => e.Label)]),
                EditApi.Hold(lease.Library, async () => { foreach (var id in ids) await service.ForgetKeptApartAsync(id, CancellationToken.None); }, OverlapText.UndoFailed)));
        });

        vocabulary.MapDelete("/{key}/kept-apart/{id:long}", async (string key, long id, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            if (!Known(key)) return Results.NotFound();
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            await lease.Library.Get<VocabularyService>().ForgetKeptApartAsync(id, ct);
            return Results.NoContent();
        });

        vocabulary.MapPut("/terms/{id:long}", async (long id, TermRename rename, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return Change(await lease.Library.Get<VocabularyService>().RenameAsync(id, rename.Label, rename.ShortLabel, ct));
        });

        vocabulary.MapPost("/terms/{id:long}/aliases", async (long id, NewAlias alias, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return Change(await lease.Library.Get<VocabularyService>().AddAliasAsync(id, alias.Text, ct));
        });

        vocabulary.MapDelete("/aliases/{id:long}", async (long id, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            await lease.Library.Get<VocabularyService>().RemoveAliasAsync(id, ct);
            return Results.NoContent();
        });

        var tags = api.MapGroup("/tags");

        tags.MapGet("", async (ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return (await lease.Library.Get<UserTagService>().ListAsync(ct)).Select(Tag).ToList();
        });

        tags.MapPost("", async (UserTagText tag, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return Change(await lease.Library.Get<UserTagService>().SaveAsync(null, tag.Name, tag.Words, ct));
        });

        tags.MapPut("/{id:long}", async (long id, UserTagText tag, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return Change(await lease.Library.Get<UserTagService>().SaveAsync(id, tag.Name, tag.Words, ct));
        });

        tags.MapDelete("/{id:long}", async (long id, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return await lease.Library.Get<UserTagService>().DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound();
        });
    }

    // Publishers are a vocabulary too, though their field holds free text.
    static bool Known(string key) => MetadataFields.TermVocabularies.Contains(key) || key == MetadataFields.Publisher.Vocabulary;

    static VocabularyTerm Term(VocabularyEntry e) =>
        new(e.Id, e.Label, e.ShortLabel, e.Origin == TermOrigin.User, [.. e.Aliases.Select(a => new VocabularyAlias(a.Id, a.Text))]);

    static VocabularyChange Change(VocabularyEdit edit) => new(edit.TermId, edit.Problem, edit.Note);

    static UserTag Tag(UserTagEntry t) => new(t.Id, t.Name, t.Words, t.Books);

    static UserTagChange Change(UserTagEdit edit) => new(edit.Tag is { } tag ? Tag(tag) : null, edit.Problem);
}
