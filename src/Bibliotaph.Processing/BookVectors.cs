using System.Security.Cryptography;
using System.Text;
using Bibliotaph.Catalog;
using Bibliotaph.Classification;
using Bibliotaph.Index;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>What one step of making the cards' vectors did.</summary>
public enum BookVectorsStep
{
    /// <summary>Nothing to do: no embedding model, every card is up to date, or it looked a moment ago.</summary>
    Nothing,

    /// <summary>Made some cards' vectors; there may be more.</summary>
    Made,

    /// <summary>The embedding model failed.</summary>
    Failed,
}

/// <summary>
/// Makes each card's vector of what it is about, so search by meaning ranks books as well as pages: from its title,
/// details, tags, notes and what the AI found in its chapters, by the same embedding model as the page vectors. It
/// is a vector a card rather than a page, so a whole library takes minutes, and it runs whenever an embedding model is
/// set, even with reading the library for meaning switched off. A card whose text changes gets a new vector; a book in a
/// folder kept from AI gets none. <see cref="LibraryVectors"/> runs it ahead of the pages.
/// </summary>
public sealed class BookVectors(AiSettings ai, BookVectorQueries queries, IndexStore store, LibraryStore library, TimeProvider? clock = null,
    ILogger<BookVectors>? log = null)
{
    /// <summary>Cards sent to the model at once.</summary>
    const int Batch = 32;

    /// <summary>How long it waits after finding every card up to date before looking again.</summary>
    public static readonly TimeSpan Recheck = TimeSpan.FromMinutes(10);

    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly ILogger _log = (ILogger?)log ?? NullLogger.Instance;

    /// <summary>When every card was last found up to date, and for which model.</summary>
    (string Model, long At)? _upToDate;

    /// <summary>The problem the last step had, when it failed.</summary>
    public string? Problem { get; private set; }

    /// <summary>Makes the next few cards' vectors that are missing or out of date.</summary>
    public async Task<BookVectorsStep> FillNextAsync(CancellationToken ct = default)
    {
        var state = ai.Current;
        if (!state.Setup.IsReady || state.BackgroundEmbedder is not { } embedder || state.Setup.EmbeddingName is not { } model) return BookVectorsStep.Nothing;
        if (_upToDate is { } checkedAt && checkedAt.Model == model && _clock.GetElapsedTime(checkedAt.At) < Recheck) return BookVectorsStep.Nothing;

        HashSet<long> kept = state.Setup.SkippedRoots.Count == 0 ? []
            : [.. await library.GetDocumentIdsInRootsAsync([.. state.Setup.SkippedRoots], ct)];
        var stale = new List<(long EntryId, string Text, string Hash)>();
        foreach (var row in await queries.GetProfilesAsync(ct))
        {
            if (row.DocumentId is { } document && kept.Contains(document)) continue;
            var text = Text(model, row);
            if (text is null) continue;
            var hash = Hash(text);
            if (row.Model == model && row.Version == EmbeddingText.Version && row.Hash == hash) continue;
            stale.Add((row.EntryId, text, hash));
            if (stale.Count == Batch) break;
        }
        if (stale.Count == 0)
        {
            _upToDate = (model, _clock.GetTimestamp());
            return BookVectorsStep.Nothing;
        }

        try
        {
            var vectors = await embedder.EmbedAsync(model, [.. stale.Select(s => s.Text)], ct);
            await store.SaveBookVectorsAsync(model, EmbeddingText.Version, [.. stale.Select((s, i) => (s.EntryId, s.Hash, vectors[i]))], ct);
            Problem = null;
            return BookVectorsStep.Made;
        }
        catch (ModelEndpointException ex)
        {
            Problem = ex.Message;
            _log.LogWarning("Making books' vectors with {Model} failed: {Reason}", model, ex.Message);
            return BookVectorsStep.Failed;
        }
    }

    /// <summary>
    /// What a card's vector is made from, as the embedding model is sent it: its title, then a line each for what is
    /// known of it. Null for a card with nothing to say.
    /// </summary>
    internal static string? Text(string model, BookProfileRow row)
    {
        var lines = new List<string>();
        void Add(string label, string value)
        {
            var tidy = value.Replace(" · ", "; ", StringComparison.Ordinal).Trim(' ', ';');
            if (tidy.Length > 0) lines.Add(label + tidy);
        }
        Add("", row.Subtitle);
        Add("By ", row.Authors);
        Add("Published by ", row.Publisher);
        Add("Series: ", row.Series);
        Add("", row.Confirmed);
        Add("", row.Provisional);
        Add("Inside: ", string.Join(", ", row.Inside));
        Add("Tags: ", row.Tags);
        Add("Notes: ", row.Notes);
        if (row.Title.Trim().Length == 0 && lines.Count == 0) return null;
        return EmbeddingText.Document(model, row.Title.Trim(), string.Join(". ", lines));
    }

    static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
}
