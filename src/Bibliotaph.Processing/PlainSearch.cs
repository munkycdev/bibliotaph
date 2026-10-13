using Bibliotaph.Catalog;
using Bibliotaph.Classification;
using Bibliotaph.Index;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>What Ask made of a request: the checked reading, or why there is none.</summary>
public sealed record PlainSearchResult(Interpretation? Interpretation, string? Problem);

/// <summary>
/// The search box's Ask: a request in plain English read by AI as the search box's own fields and words, through
/// whichever AI is set up (built in or a model server). It needs AI; without it the search box works as it always
/// has. Nothing here searches: the reading is shown for the user to change and run.
/// </summary>
public sealed class PlainSearchService(AiSettings ai, VocabularyStore vocabularies, IndexQueries queries, ILogger<PlainSearchService>? log = null,
    AiUse? use = null)
{
    readonly ILogger _log = log ?? NullLogger<PlainSearchService>.Instance;

    /// <summary>Whether AI is set up and on, so a request can be read.</summary>
    public bool IsReady => ai.Interpreter is not null;

    /// <summary>
    /// Reads <paramref name="request"/>. A model that can't be reached, or answers out of shape, comes back as a
    /// problem to show; cancelling throws.
    /// </summary>
    public async Task<PlainSearchResult> InterpretAsync(string request, CancellationToken ct = default)
    {
        if (ai.Interpreter is not { } interpreter) return new PlainSearchResult(null, PlainSearchText.NeedsAi);
        if (string.IsNullOrWhiteSpace(request)) return new PlainSearchResult(null, "Type what you're looking for first, then press Enter.");
        using var busy = use?.Begin();
        var context = new PlainSearchContext(await vocabularies.GetAsync(ct), await queries.GetSystemsWithoutLevelsAsync(ct));
        try
        {
            var interpretation = await interpreter.InterpretAsync(request, context, ct);
            // Counts only: what someone searches for stays off the log, as book text does.
            _log.LogInformation("Ask read a request into {Parts} parts with {Notes} notes (prompt {Version})",
                interpretation.Parts.Count, interpretation.Notes.Count, PlainSearchPrompt.Version);
            return new PlainSearchResult(interpretation, null);
        }
        catch (ModelEndpointException ex)
        {
            _log.LogWarning("Ask couldn't reach the AI: {Reason}", ex.Message);
            return new PlainSearchResult(null, $"The AI couldn't read your question. {ex.Message}");
        }
        catch (ClassifierAnswerException ex)
        {
            _log.LogWarning("Ask couldn't use the AI's answer: {Reason}", ex.Message);
            return new PlainSearchResult(null, "The AI's answer couldn't be used. Try asking in other words.");
        }
    }
}

/// <summary>Ask's wording that more than one place shows.</summary>
public static class PlainSearchText
{
    public const string NeedsAi = "Ask needs AI to read your question. Set AI up in Settings > AI, or close Ask and search as usual.";
}
