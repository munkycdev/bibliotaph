using System.Security.Claims;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;

namespace Bibliotaph.Server.Api;

/// <summary>
/// /api/processing: Settings › Processing, as the Windows app's <c>LibraryActivity</c> gathers it, pausing each lane,
/// running a step again for every book, and whether books are looked up online. A pause is kept with the library, so it holds after the server closes an idle
/// library and opens it again.
/// </summary>
public static class ProcessingApi
{
    public static void MapProcessingApi(this RouteGroupBuilder api)
    {
        var processing = api.MapGroup("/processing");

        processing.MapGet("", async (ClaimsPrincipal principal, UserLibraries libraries, PcActivityBoard pcs, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return await StateAsync(lease.Library, pcs.Of(principal.UserId()!), ct);
        }).WatchesBackgroundWork();

        processing.MapPut("/paused", async (ProcessingPauses paused, ClaimsPrincipal principal, UserLibraries libraries, PcActivityBoard pcs,
            CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            await PausedLanes.SaveAsync(library.Get<SettingsStore>(), paused, ct);
            PausedLanes.Apply(library.Indexing, paused);
            return await StateAsync(library, pcs.Of(principal.UserId()!), ct);
        });

        processing.MapPost("/run-again/{step}", async (string step, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            // Only a step the page offers now: the AI's steps need the server's own AI, not a PC's.
            var offers = await library.Get<RunAgainService>().OffersAsync(await library.Get<IndexQueries>().GetProgressAsync(ct), library.Get<AiSettings>().Setup.IsReady, ct);
            if (offers.All(o => o.Step != step) || await library.Get<RunAgainService>().RunAsync(step, ct) is not { } note) return Results.NotFound();
            return Results.Ok(new RunAgainDone(note));
        });

    }

    static async Task<ProcessingState> StateAsync(UserLibrary library, IReadOnlyList<PcActivityBoard.Entry> pcs, CancellationToken ct)
    {
        var indexing = library.Indexing;
        var counts = await library.Get<LibraryStore>().GetCountsAsync(ct);
        var progress = await library.Get<IndexQueries>().GetProgressAsync(ct);
        var folders = (await library.Get<SourceRootStore>().ListAsync(ct)).Count;
        var aiOn = library.Get<AiSettings>().Setup.IsReady;
        var status = await StorageApi.StatusAsync(library, ct);
        return new ProcessingState(status.Text, folders > 0, indexing.IsScanning, counts.Files, Math.Max(0, counts.Unhashed - indexing.Unreadable.Count),
            progress.Indexed, progress.Queued, progress.OcrPagesDone, progress.OcrPages, aiOn, aiOn ? indexing.Unavailable(Lane.Classify) : null,
            progress.Classified, progress.ToClassify, PausedLanes.Of(indexing), await NowAsync(library, ct), status.Attention,
            indexing.Unavailable(Lane.Online), progress.LookedUp, progress.ToLookUp, library.Get<BookLookups>().Catalogue.Name,
            progress.ScannedWhole, progress.ToScanWhole, library.Get<ChapterScanService>().IsOn, await PcsAsync(library, pcs, ct),
            await library.Get<RunAgainService>().OffersAsync(progress, aiOn, ct));
    }

    /// <summary>
    /// The PCs' AI at work, in the words of <see cref="NowAsync"/>: a book is named by its card's title here, so a book
    /// only the PC has is never named.
    /// </summary>
    static async Task<IReadOnlyList<PcProcessing>> PcsAsync(UserLibrary library, IReadOnlyList<PcActivityBoard.Entry> pcs, CancellationToken ct)
    {
        if (pcs.Count == 0) return [];
        var stages = library.Get<IStageCatalog>();
        var titles = await library.Get<IndexQueries>().GetTitlesByHashAsync([.. pcs.Select(p => p.Activity.ContentHash).OfType<string>()], ct);
        return [.. pcs.Select(pc =>
        {
            var activity = pc.Activity;
            var doing = Enum.TryParse<Stage>(activity.Step, out var stage)
                ? $"{stages.Find(stage)?.Names.Running ?? "Working on"} {(activity.ContentHash is { } hash ? titles.GetValueOrDefault(hash) : null) ?? "one of its own books"}"
                : null;
            return new PcProcessing(pc.DeviceName, activity.Step, doing, activity.Paused, activity.Waiting,
                activity.Classified, activity.ToClassify, activity.ScannedWhole, activity.ToScanWhole);
        })];
    }

    /// <summary>What each part of indexing is working on right now, one line each, in the Windows app's words.</summary>
    static async Task<IReadOnlyList<string>> NowAsync(UserLibrary library, CancellationToken ct)
    {
        var indexing = library.Indexing;
        var stages = library.Get<IStageCatalog>();
        var queries = library.Get<IndexQueries>();
        var lines = new List<string>();
        if (indexing.Hashing is { } file) lines.Add($"Reading {Path.GetFileName(file)}");
        foreach (var lane in Enum.GetValues<Lane>())
            if (indexing.Running(lane) is { } job)
                lines.Add($"{stages.Find(job.Stage)?.Names.Running ?? "Working on"} {await queries.GetTitleAsync(job.DocumentId, ct) ?? "a new file"}");
        return lines;
    }
}

/// <summary>The lanes the user paused, kept as a setting of the library on the server.</summary>
public static class PausedLanes
{
    public static ProcessingPauses Of(IndexingService indexing) =>
        new(indexing.IsPaused(Lane.Index), indexing.IsPaused(Lane.Ocr), indexing.IsPaused(Lane.Classify));

    public static void Apply(IndexingService indexing, ProcessingPauses paused)
    {
        Set(indexing, Lane.Index, paused.Indexing);
        Set(indexing, Lane.Ocr, paused.Scans);
        Set(indexing, Lane.Classify, paused.Ai);
    }

    public static async Task<ProcessingPauses> LoadAsync(SettingsStore settings, CancellationToken ct = default)
    {
        var lanes = (await settings.GetAsync(SettingKeys.ServerPausedLanes, ct) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        return new(lanes.Contains(nameof(Lane.Index)), lanes.Contains(nameof(Lane.Ocr)), lanes.Contains(nameof(Lane.Classify)));
    }

    public static Task SaveAsync(SettingsStore settings, ProcessingPauses paused, CancellationToken ct = default)
    {
        var lanes = new[] { (paused.Indexing, Lane.Index), (paused.Scans, Lane.Ocr), (paused.Ai, Lane.Classify) }.Where(l => l.Item1).Select(l => l.Item2.ToString());
        return settings.SetAsync(SettingKeys.ServerPausedLanes, string.Join(",", lanes), ct);
    }

    static void Set(IndexingService indexing, Lane lane, bool paused)
    {
        if (paused) indexing.Pause(lane);
        else indexing.Resume(lane);
    }
}
