using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Classification;
using Bibliotaph.Index;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>
/// A file's page vectors for search by meaning, as shared: the embedding model that made them, how long each is, and
/// each page's vector by its zero-based PDF page, with a hash of exactly what was sent to the model for it
/// (<see cref="SharedPageVectors.TextHash"/>), so a device whose text of the page differs makes its own.
/// </summary>
public sealed record SharedVectors(string Model, int Dimensions, IReadOnlyList<SharedPageVector> Pages);

/// <summary>A page's vector, as shared: half-precision floats, little-endian, in base64, half the size of the stored ones for no change in what they find.</summary>
public sealed record SharedPageVector(int Page, string Text, string Vector);

/// <summary>
/// Page vectors shared through the web library like OCR (<see cref="SharedPageText"/>), since embedding a whole library
/// takes hours: a device about to make a book's vectors first asks for another device's vectors of the same file by
/// the same model and takes them for the pages whose text is the same, and a device that finished a book's vectors,
/// having made some itself, sends them once. They are asked for one file at a time, being large.
/// </summary>
public sealed class SharedPageVectors(AskQueries ask, LibraryStore library, IndexStore index, SharedResultCache cache, IResultChannel? channel = null,
    TimeProvider? clock = null, ILogger<SharedPageVectors>? log = null)
{
    /// <summary>The files whose vectors were finished here waiting to be sent, under this prefix and the content hash, with the document, model and page hashes.</summary>
    const string WaitingPrefix = "vectors.waiting:";

    /// <summary>The models whose books finished before page vectors were shared are kept to send, with the text version.</summary>
    const string BackfilledPrefix = "vectors.backfilled:";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly ILogger _log = log ?? NullLogger<SharedPageVectors>.Instance;

    /// <summary>A short hash of what was sent to the embedding model for a page.</summary>
    public static string TextHash(string embedded) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(embedded)), 0, 8);

    /// <summary>
    /// Takes another device's vectors by <paramref name="model"/> for the book's pages that have none, where what this
    /// device would send for the page (<paramref name="embedded"/>) is what that device sent. Returns how many it took;
    /// none when the web library isn't connected or holds none for the file.
    /// </summary>
    public async Task<int> TakeAsync(long documentId, string model, Func<AskPageRow, string> embedded, CancellationToken ct = default)
    {
        if (channel is not { IsOn: true } || await library.GetContentHashAsync(documentId, ct) is not { } hash) return 0;
        if (await channel.ReadResultAsync(SharedResultKinds.Vectors, hash, ct) is not { } result || result.PromptVersion != EmbeddingText.Version
            || Read(result.Content) is not { } shared || shared.Model != model) return 0;
        var byPage = shared.Pages.GroupBy(p => p.Page).ToDictionary(g => g.Key, g => g.First());
        var pages = await ask.GetPagesWithoutVectorsAsync(documentId, model, EmbeddingText.Version, int.MaxValue, ct);
        var taken = new List<(long PageId, float[] Vector)>();
        foreach (var page in pages)
            if (byPage.TryGetValue(page.PdfPage, out var other) && other.Text == TextHash(embedded(page)) && Decode(other.Vector) is { } vector
                && vector.Length == shared.Dimensions)
                taken.Add((page.PageId, vector));
        await index.SaveVectorsAsync(model, EmbeddingText.Version, taken, ct);
        if (taken.Count > 0) _log.LogInformation("Took {Pages} page vectors for document {DocumentId} from the web library", taken.Count, documentId);
        return taken.Count;
    }

    /// <summary>
    /// Keeps a book whose vectors by <paramref name="model"/> were finished here, with the hash of what was sent for each
    /// page by its PDF page, to send at the next pass.
    /// </summary>
    public async Task RecordAsync(long documentId, string model, IReadOnlyDictionary<int, string> texts, CancellationToken ct = default)
    {
        if (await library.GetContentHashAsync(documentId, ct) is not { } hash) return;
        await cache.SetStateAsync(WaitingPrefix + hash, JsonSerializer.Serialize(new Waiting(documentId, model, _clock.GetUtcNow().UtcDateTime, texts), Json), ct);
    }

    /// <summary>Whether the books finished with <paramref name="model"/> before page vectors were shared have been kept to send.</summary>
    public async Task<bool> HasBackfilledAsync(string model, CancellationToken ct = default) =>
        await cache.GetStateAsync(BackfilledPrefix + model, ct) == EmbeddingText.Version.ToString(CultureInfo.InvariantCulture);

    /// <summary>Notes that they have.</summary>
    public Task SetBackfilledAsync(string model, CancellationToken ct = default) =>
        cache.SetStateAsync(BackfilledPrefix + model, EmbeddingText.Version.ToString(CultureInfo.InvariantCulture), ct);

    /// <summary>Sends each book finished here since the last pass, one request a file, as they can be large.</summary>
    public async Task PushAsync(CancellationToken ct = default)
    {
        if (channel is not { IsOn: true }) return;
        var sent = 0;
        foreach (var (key, value) in await cache.GetStatesAsync(WaitingPrefix, ct))
        {
            ct.ThrowIfCancellationRequested();
            var hash = key[WaitingPrefix.Length..];
            if (Read<Waiting>(value) is { } waiting)
            {
                List<SharedPageVector> pages = [.. (await ask.GetVectorsAsync(waiting.DocumentId, waiting.Model, EmbeddingText.Version, ct))
                    .Where(v => waiting.Texts.ContainsKey(v.PdfPage)).OrderBy(v => v.PdfPage)
                    .Select(v => new SharedPageVector(v.PdfPage, waiting.Texts[v.PdfPage], Encode(v.Vector)))];
                if (pages.Count > 0)
                {
                    var dimensions = Decode(pages[0].Vector)?.Length ?? 0;
                    var result = new SharedResult(hash, SharedResultKinds.Vectors, EmbeddingText.Version,
                        $"{hash}@{waiting.FinishedUtc.ToString("O", CultureInfo.InvariantCulture)}", waiting.FinishedUtc,
                        JsonSerializer.Serialize(new SharedVectors(waiting.Model, dimensions, pages), Json));
                    if (!await channel.WriteResultsAsync([result], ct)) return;
                    sent++;
                }
            }
            await cache.DeleteStateAsync(key, ct);
        }
        if (sent > 0) _log.LogInformation("Shared the page vectors of {Count} files with the web library", sent);
    }

    sealed record Waiting(long DocumentId, string Model, DateTime FinishedUtc, IReadOnlyDictionary<int, string> Texts);

    static string Encode(float[] vector)
    {
        var bytes = new byte[vector.Length * 2];
        for (var i = 0; i < vector.Length; i++) BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(i * 2), (Half)vector[i]);
        return Convert.ToBase64String(bytes);
    }

    static float[]? Decode(string base64)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
        var vector = new float[bytes.Length / 2];
        for (var i = 0; i < vector.Length; i++) vector[i] = (float)BinaryPrimitives.ReadHalfLittleEndian(bytes.AsSpan(i * 2));
        return vector;
    }

    static SharedVectors? Read(string json) => Read<SharedVectors>(json);

    /// <summary>Vectors this version can't read are left alone, as other results are.</summary>
    static T? Read<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
