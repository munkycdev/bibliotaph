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
/// /api/processing: Settings › Processing, as the Windows app's <c>LibraryActivity</c> gathers it, and pausing each lane.
/// A pause is kept with the library, so it holds after the server closes an idle library and opens it again.
/// </summary>
public static class ProcessingApi
{
    public static void MapProcessingApi(this RouteGroupBuilder api)
    {
        var processing = api.MapGroup("/processing");

        processing.MapGet("", async (ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return await StateAsync(lease.Library, ct);
        });

        processing.MapPut("/paused", async (ProcessingPauses paused, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            await PausedLanes.SaveAsync(library.Get<SettingsStore>(), paused, ct);
            PausedLanes.Apply(library.Indexing, paused);
            return await StateAsync(library, ct);
        });
    }

    static async Task<ProcessingState> StateAsync(UserLibrary library, CancellationToken ct)
    {
        var indexing = library.Indexing;
        var counts = await library.Get<LibraryStore>().GetCountsAsync(ct);
        var progress = await library.Get<IndexQueries>().GetProgressAsync(ct);
        var folders = (await library.Get<SourceRootStore>().ListAsync(ct)).Count;
        var aiOn = library.Get<AiSettings>().Setup.IsReady;
        var status = await StorageApi.StatusAsync(library, ct);
        return new ProcessingState(status.Text, folders > 0, indexing.IsScanning, counts.Files, Math.Max(0, counts.Unhashed - indexing.Unreadable.Count),
            progress.Indexed, progress.Queued, progress.OcrPagesDone, progress.OcrPages, aiOn, aiOn ? indexing.Unavailable(Lane.Classify) : null,
            progress.Classified, progress.ToClassify, PausedLanes.Of(indexing), await NowAsync(library, ct), status.Attention);
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
