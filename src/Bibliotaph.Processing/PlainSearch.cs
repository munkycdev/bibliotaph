using Bibliotaph.Catalog;
using Bibliotaph.Classification;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>What Ask made of a request: the checked reading, or why there is none.</summary>
public sealed record PlainSearchResult(Interpretation? Interpretation, string? Problem);

/// <summary>
/// The search box's Ask: a request in plain English read as the search box's own fields and words, by whichever AI is
/// set up (built in or a model server), or by <see cref="PlainSearchRules"/> while no chat model is, so Ask works
/// offline too. Nothing here searches: the reading is shown for the user to change and run.
/// </summary>
public sealed class PlainSearchService(AiSettings ai, VocabularyStore vocabularies, IndexQueries queries, AiUse use,
    ILogger<PlainSearchService>? log = null)
{
    readonly ILogger _log = log ?? NullLogger<PlainSearchService>.Instance;

    /// <summary>Whether AI is set up and on, so a request is read by it and a question can be answered from the books.</summary>
    public bool IsReady => ai.Interpreter is not null;

    /// <summary>
    /// Reads <paramref name="request"/>. When the model can't be reached or its answer can't be used, the request is read
    /// by rules instead, with a note saying why, so Ask still gives a search; cancelling throws.
    /// </summary>
    public async Task<PlainSearchResult> InterpretAsync(string request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request)) return new PlainSearchResult(null, "Type what you're looking for first, then press Enter.");
        var context = new PlainSearchContext(await vocabularies.GetAsync(ct), await queries.GetSystemsWithoutLevelsAsync(ct));
        if (ai.Interpreter is not { } interpreter) return ByRules(PlainSearchText.ReadByRules);
        using var busy = use.Begin();
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
            return ByRules(PlainSearchText.ReadByRulesInstead(ex.Message));
        }
        catch (ClassifierAnswerException ex)
        {
            _log.LogWarning("Ask couldn't use the AI's answer: {Reason}", ex.Message);
            return ByRules(PlainSearchText.ReadByRulesInstead(ex.Message));
        }

        PlainSearchResult ByRules(string note)
        {
            var rules = PlainSearchRules.Read(request, context);
            return new PlainSearchResult(rules with { Notes = [.. rules.Notes, note] }, null);
        }
    }
}

/// <summary>Ask's wording that more than one place shows.</summary>
public static class PlainSearchText
{
    public const string NeedsAi = AskPanelText.NeedsAi;

    /// <summary>Said under a reading made without AI, so a reading that missed something says why.</summary>
    public const string ReadByRules = "Read without AI, by the names in your vocabulary. Set AI up in Settings > AI for Ask to read requests more fully.";

    /// <summary>Said under a reading made by rules because the AI failed: what went wrong, so it can be put right.</summary>
    public static string ReadByRulesInstead(string problem) => $"The AI couldn't read this ({problem.TrimEnd('.')}), so it was read without AI, by the names in your vocabulary.";
}
