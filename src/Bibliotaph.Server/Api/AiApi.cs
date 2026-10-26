using System.Security.Claims;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Classification;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Bibliotaph.Server.Ai;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;

namespace Bibliotaph.Server.Api;

/// <summary>
/// /api/ai: Settings › AI, as the Windows app's model server setup has it, over <see cref="AiService"/>: Connect,
/// Save with a key, Use AI on and off, the folders sent to AI, Reclassify and Test with a book. Only a model service on
/// the internet, over https, can be used (<see cref="ModelServices"/>), and a key is never sent back.
/// </summary>
public static class AiApi
{
    const string TickFirst = "Tick “Send excerpts to this server” first.";

    public static void MapAiApi(this RouteGroupBuilder api)
    {
        var ai = api.MapGroup("/ai");

        ai.MapGet("", async (ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return await PageAsync(lease.Library, ct);
        });

        ai.MapPost("/connect", async (AiConnect connect, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            if (ModelServices.Problem(connect.Endpoint) is { } problem) return new AiConnection(null, null, problem, []);
            var endpoint = ModelServices.Tidy(connect.Endpoint)!;
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            try
            {
                var info = await ClientFor(lease.Library, endpoint, connect.Key).ConnectAsync(ct);
                return new AiConnection(endpoint, AiText.Found(info), null, info.Models);
            }
            catch (ModelEndpointException ex)
            {
                return new AiConnection(endpoint, null, ex.Message, []);
            }
        });

        ai.MapPut("", async (AiSave save, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            if (ModelServices.Problem(save.Endpoint) is { } problem) return new AiNote(null, problem);
            if (save.Model.Trim().Length == 0) return new AiNote(null, "Pick a model first.");
            if (!save.RemoteAllowed) return new AiNote(null, TickFirst);
            var endpoint = ModelServices.Tidy(save.Endpoint)!;
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            EndpointInfo info;
            try
            {
                info = await ClientFor(library, endpoint, save.Key).ConnectAsync(ct);
            }
            catch (ModelEndpointException ex)
            {
                return new AiNote(null, ex.Message);
            }
            if (save.Key is { Length: > 0 } key) await library.Get<ServerApiKeys>().RememberAsync(endpoint, key, ct);

            var service = library.Get<AiService>();
            var was = service.Setup;
            // A first save switches AI on: that's what saving a model is for. Later saves keep it as it was.
            var enabled = was.Endpoint is null || was.Model is null || was.Enabled;
            var model = save.Model.Trim();
            await service.SaveAsync(was with
            {
                Endpoint = endpoint,
                Model = model,
                Provider = info.Provider,
                Enabled = enabled,
                RemoteAllowed = true,
                BuiltIn = false,
            }, ct);
            return new AiNote(AiText.Saved(was.Model, model, enabled), null);
        });

        ai.MapDelete("/key", async (string endpoint, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            if (ModelServices.Tidy(endpoint) is not { } tidy) return Results.NotFound();
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            await lease.Library.Get<ServerApiKeys>().ForgetAsync(tidy, ct);
            return Results.NoContent();
        });

        ai.MapPut("/enabled", async (AiSwitch change, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var service = lease.Library.Get<AiService>();
            if (change.On && (service.Setup.Endpoint is null || service.Setup.Model is null)) return new AiNote(null, AiText.ConnectFirst);
            await service.SaveAsync(service.Setup with { Enabled = change.On }, ct);
            return new AiNote(change.On ? AiText.SwitchedOn : AiText.SwitchedOff, null);
        });

        ai.MapPut("/folders/{id:long}", async (long id, AiFolderSend send, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            if ((await library.Get<SourceRootStore>().ListAsync(ct)).All(r => r.Id != id)) return Results.NotFound();
            var service = library.Get<AiService>();
            var skipped = service.Setup.SkippedRoots.ToHashSet();
            if (send.Send) skipped.Remove(id);
            else skipped.Add(id);
            await service.SaveAsync(service.Setup with { SkippedRoots = skipped }, ct);
            return Results.NoContent();
        });

        ai.MapPost("/reclassify", async (ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return new AiNote(AiText.Reclassified(await lease.Library.Get<AiService>().ReclassifyAsync(ct)), null);
        });

        // Holds the request while the model reads, as the Windows dialog waits; closing the dialog cancels it.
        ai.MapPost("/test", async (AiTest test, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            if (ModelServices.Problem(test.Endpoint) is { } problem) return Report(problem);
            if (!test.RemoteAllowed) return Report("Tick “Send excerpts to this server” first: the test sends a book's excerpt too.");
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var service = lease.Library.Get<AiService>();
            var result = await service.TestAsync(ModelServices.Tidy(test.Endpoint)!, test.Model.Trim(), ct: ct);
            return result.Result is { } read
                ? new AiTestReport(AiText.Summary(result), result.Problem,
                    [.. AiText.Kept(read, await service.VocabularyAsync(ct)).Select(Line)], [.. AiText.Dropped(read).Select(Line)])
                : new AiTestReport(AiText.Summary(result), result.Problem, [], []);
        });
    }

    static AiTestReport Report(string problem) => new(null, problem, [], []);

    static AiTestReportLine Line(AiTestLine line) => new(line.Text, line.Detail);

    /// <summary>The client Connect and Save try: with the key typed, or else the one saved for the address.</summary>
    static IModelClient ClientFor(UserLibrary library, string endpoint, string? key)
    {
        var keys = library.Get<ServerApiKeys>();
        return library.Get<ModelClientFactory>().Create(endpoint, () => key is { Length: > 0 } typed ? typed.Trim() : keys.Find(endpoint));
    }

    internal static async Task<AiPage> PageAsync(UserLibrary library, CancellationToken ct)
    {
        var service = library.Get<AiService>();
        var setup = service.Setup;
        var progress = await library.Get<IndexQueries>().GetProgressAsync(ct);
        var summary = await service.SummarizeAsync(ct);
        var reclassify = summary.Earlier > 0 || progress.ClassifyFailed > 0
            ? new AiReclassify(AiText.Reclassify(summary.Earlier, progress.ClassifyFailed), AiText.ReclassifyLabel(summary.Earlier, progress.ClassifyFailed),
                progress.ClassifyFailed > 0)
            : null;
        var folders = (await library.Get<SourceRootStore>().ListAsync(ct))
            .Select(r => new AiFolder(r.Id, FolderText.Name(r.Path), FolderText.Location(r.Path, library.Drives), !setup.SkippedRoots.Contains(r.Id)))
            .ToList();
        var waiting = setup.IsReady ? library.Indexing.Unavailable(Lane.Classify) : null;
        return new AiPage(setup.Enabled, setup.Endpoint, setup.Model, setup.RemoteAllowed,
            setup.Endpoint is { } endpoint ? library.Get<ServerApiKeys>().Ending(endpoint) : null,
            AiText.Progress(setup, summary, progress), waiting, reclassify, folders);
    }
}
