using System.Globalization;
using System.Text.Json;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>
/// The text OCR read on a file's scanned pages, as shared: each page's text and how good it looks, and its words with
/// their boxes in PDF points, four numbers a word (left, top, right, bottom), so selecting text works on the pages too.
/// </summary>
public sealed record SharedOcr(IReadOnlyList<SharedOcrPage> Pages);

public sealed record SharedOcrPage(int Page, string Text, double Quality, IReadOnlyList<string> Words, IReadOnlyList<double> Boxes);

/// <summary>
/// OCR shared through the web library like the AI's results (<see cref="SharedResults"/>), since reading a scanned
/// book is slow: a device about to OCR a file first asks for another device's OCR of the same content and takes it
/// for the pages it has never read, and a device that OCR'd a file sends its pages once it is done. Only pages this
/// device OCR'd itself are sent, so taken text never goes back; a page OCR'd here and flagged again by Reprocess with
/// OCR on every page is read here again, as the user asked.
/// </summary>
public sealed class SharedPageText(IndexQueries queries, IndexStore index, SharedResultCache cache, IResultChannel? channel = null,
    TimeProvider? clock = null, ILogger<SharedPageText>? log = null)
{
    /// <summary>The files OCR'd here waiting to be sent, each under this prefix and its content hash.</summary>
    const string WaitingPrefix = "ocr.waiting:";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly ILogger _log = log ?? NullLogger<SharedPageText>.Instance;

    /// <summary>
    /// For the OCR stage, before it reads a file: fills the pages it has never read from another device's OCR of the
    /// same content, when there is one. Returns how many pages it filled.
    /// </summary>
    public async Task<int> TryTakeAsync(long documentId, string contentHash, CancellationToken ct = default)
    {
        if (channel is not { IsOn: true }) return 0;
        var unread = (await queries.GetPagesNeverRecognizedAsync(documentId, ct)).ToHashSet();
        if (unread.Count == 0) return 0;
        if (await channel.ReadResultAsync(SharedResultKinds.PageText, contentHash, ct) is not { } result || Read(result.Content) is not { } shared) return 0;

        var filled = 0;
        foreach (var page in shared.Pages)
        {
            ct.ThrowIfCancellationRequested();
            if (!unread.Contains(page.Page) || page.Boxes.Count != page.Words.Count * 4) continue;
            await index.SetOcrPageAsync(documentId, page.Page, page.Text, page.Quality,
                [.. page.Words.Select((w, i) => new OcrWordRow(w, page.Boxes[i * 4], page.Boxes[i * 4 + 1], page.Boxes[i * 4 + 2], page.Boxes[i * 4 + 3]))], ct);
            filled++;
        }
        if (filled > 0) _log.LogInformation("Took another device's OCR for {Pages} pages of document {DocumentId}", filled, documentId);
        return filled;
    }

    /// <summary>For the OCR stage, once it has read pages of a file itself: the file's OCR is sent at the next pass.</summary>
    public Task OfferAsync(long documentId, string contentHash, CancellationToken ct = default) =>
        cache.SetStateAsync(WaitingPrefix + contentHash, string.Create(CultureInfo.InvariantCulture, $"{documentId}|{_clock.GetUtcNow().UtcDateTime:O}"), ct);

    /// <summary>Sends each file OCR'd here since the last pass, one request a file, as they can be large.</summary>
    public async Task PushAsync(CancellationToken ct = default)
    {
        if (channel is not { IsOn: true }) return;
        var sent = 0;
        foreach (var (key, value) in await cache.GetStatesAsync(WaitingPrefix, ct))
        {
            ct.ThrowIfCancellationRequested();
            var hash = key[WaitingPrefix.Length..];
            var parts = value.Split('|');
            if (parts.Length != 2 || !long.TryParse(parts[0], CultureInfo.InvariantCulture, out var documentId)
                || !DateTime.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var finished))
            {
                await cache.DeleteStateAsync(key, ct);
                continue;
            }
            var pages = await queries.GetRecognizedPagesAsync(documentId, ct);
            if (pages.Count > 0)
            {
                var content = new SharedOcr([.. pages.Select(p => new SharedOcrPage(p.PdfPage, p.Text, Math.Round(p.Quality, 3), [.. p.Words.Select(w => w.Text)],
                    [.. p.Words.SelectMany(w => new[] { Math.Round(w.Left, 1), Math.Round(w.Top, 1), Math.Round(w.Right, 1), Math.Round(w.Bottom, 1) })]))]);
                var result = new SharedResult(hash, SharedResultKinds.PageText, Pipeline.Version(Stage.Ocr),
                    $"{hash}@{finished.ToString("O", CultureInfo.InvariantCulture)}", finished, JsonSerializer.Serialize(content, Json));
                if (!await channel.WriteResultsAsync([result], ct)) return;
                sent++;
            }
            await cache.DeleteStateAsync(key, ct);
        }
        if (sent > 0) _log.LogInformation("Shared the OCR of {Count} files with the web library", sent);
    }

    /// <summary>OCR this version can't read is left alone, as other results are.</summary>
    static SharedOcr? Read(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SharedOcr>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
