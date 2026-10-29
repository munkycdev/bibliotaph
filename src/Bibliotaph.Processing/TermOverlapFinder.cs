using System.Security.Cryptography;
using System.Text;
using Bibliotaph.Catalog;
using Bibliotaph.Classification;
using Bibliotaph.Core.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>
/// The AI pass of Find overlaps: asks the model, in the background, which names in each vocabulary it can add to
/// (<see cref="AiTerms.Extensible"/>) refer to the same thing, and keeps what it says in catalog.db for Needs review to
/// show. It runs once the vocabulary has changed or the AI has gone quiet after cataloguing, and asks only when the
/// names or the model differ from what was last asked, so a list that hasn't changed costs nothing. Like scanning
/// whole books it gives way to anything a person is waiting on, stopping its request and trying again later.
/// </summary>
public sealed class TermOverlapFinder : IDisposable
{
    readonly VocabularyStore _vocabularies;
    readonly SettingsStore _settings;
    readonly AiSettings _ai;
    readonly ILogger _log;
    readonly TimeSpan _settle;
    readonly CancellationTokenSource _stop = new();
    int _wanted;
    int _running;

    public TermOverlapFinder(VocabularyStore vocabularies, SettingsStore settings, AiSettings ai, ILogger<TermOverlapFinder>? log = null, TimeSpan? settle = null)
    {
        _vocabularies = vocabularies;
        _settings = settings;
        _ai = ai;
        _log = log ?? NullLogger<TermOverlapFinder>.Instance;
        _settle = settle ?? TimeSpan.FromSeconds(5);
        // Cataloguing adds terms without an edit; once its requests end, the new names get compared.
        _ai.Activity.Finished += OnModelFinished;
    }

    /// <summary>Whether the AI is set up, so a pass will ask it; without, Find overlaps compares names only.</summary>
    public bool CanAsk => _ai.Current.Setup.IsReady && _ai.Current.Background is not null;

    /// <summary>Raised after a pass that found new groups, so a page can show them.</summary>
    public event EventHandler? Found;

    /// <summary>Whether a pass is running or waiting for the AI to be quiet.</summary>
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    public void Dispose()
    {
        _ai.Activity.Finished -= OnModelFinished;
        _stop.Cancel();
        _stop.Dispose();
    }

    void OnModelFinished(object? sender, EventArgs e) => Schedule();

    /// <summary>Asks for a pass once things settle. A pass already running is followed by another, so nothing is missed.</summary>
    public void Schedule()
    {
        if (_stop.IsCancellationRequested || !CanAsk) return;
        Volatile.Write(ref _wanted, 1);
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        _ = Task.Run(LoopAsync);
    }

    async Task LoopAsync()
    {
        try
        {
            while (Interlocked.Exchange(ref _wanted, 0) == 1)
            {
                await Task.Delay(_settle, _stop.Token);
                if (await RunAsync(_stop.Token)) Found?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Comparing vocabulary names with the AI failed");
        }
        finally
        {
            Volatile.Write(ref _running, 0);
            if (Volatile.Read(ref _wanted) == 1) Schedule();
        }
    }

    /// <summary>
    /// One pass over every vocabulary the AI can add to, asking about the lists whose names it hasn't seen. Returns
    /// whether it found a group it didn't know.
    /// </summary>
    public async Task<bool> RunAsync(CancellationToken ct = default)
    {
        var ai = _ai.Current;
        if (!ai.Setup.IsReady || ai.Background is not { } client || ai.Setup.ModelName is not { } model) return false;
        var found = false;
        foreach (var vocabulary in AiTerms.Extensible)
        {
            ct.ThrowIfCancellationRequested();
            var terms = await _vocabularies.GetOverlapTermsAsync(vocabulary, ct);
            if (terms.Count < 2) continue;
            var names = terms.Select(Describe).ToList();
            var fingerprint = Fingerprint(model, names);
            if (await _settings.OverlapsAskedAsync(vocabulary, ct) == fingerprint) continue;

            var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var term in terms)
                foreach (var name in term.Names.Prepend(Describe(term))) keys.TryAdd(name.Trim(), term.Key);
            var starters = terms.Where(t => t.Starter).Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
            var groups = new List<(IReadOnlyList<string> Keys, string Reason)>();
            var answered = true;
            foreach (var batch in names.Chunk(TermOverlapPrompt.MaxNames))
            {
                if (await AskAsync(client, model, ListName(vocabulary), batch, ct) is not { } answers)
                {
                    answered = false;
                    break;
                }
                foreach (var answer in answers)
                {
                    var matched = answer.Names.Select(n => keys.GetValueOrDefault(n.Trim())).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
                    // Two terms that came with Bibliotaph are never doubles: that list is curated.
                    if (matched.Count < 2 || matched.All(starters.Contains)) continue;
                    groups.Add((matched, answer.Why));
                }
            }
            // The model couldn't be reached: the names are asked about again next time.
            if (!answered) return found;
            var added = await _vocabularies.AddAiOverlapsAsync(vocabulary, groups, ct);
            await _settings.SetOverlapsAskedAsync(vocabulary, fingerprint, ct);
            _log.LogInformation("Compared {Count} {Vocabulary} names with {Model}: {Groups} groups, {Added} new", names.Count, vocabulary, model, groups.Count, added);
            found |= added > 0;
        }
        return found;
    }

    /// <summary>
    /// One request, once the AI is quiet. Null when the model can't be reached; empty when it answered badly, which
    /// is this batch's problem, not the server's.
    /// </summary>
    async Task<IReadOnlyList<TermOverlapAnswer>?> AskAsync(IModelClient client, string model, string listName, IReadOnlyList<string> names, CancellationToken ct)
    {
        var system = TermOverlapPrompt.SystemMessage(listName);
        var user = TermOverlapPrompt.UserMessage(names);
        var request = new ModelRequest(model, system, user, TermOverlapPrompt.SchemaJson, ClassifierPrompt.ContextTokens(system, user),
            TermOverlapPrompt.AnswerTokens, SchemaName: "term_overlaps");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!_ai.Activity.TryQuiet(out var yield, out var wait))
            {
                await Task.Delay(wait, ct);
                continue;
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, yield);
            try
            {
                return TermOverlapPrompt.ParseAnswer(await client.CompleteAsync(request, linked.Token));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The user asked the AI something; their request comes first.
                await Task.Delay(_ai.Activity.QuietPeriod, ct);
            }
            catch (ModelEndpointException ex)
            {
                _log.LogInformation("Couldn't compare {List} names with {Model}: {Problem}", listName, model, ex.Message);
                return null;
            }
            catch (ClassifierAnswerException ex)
            {
                _log.LogInformation("The model's answer about {List} names couldn't be read: {Problem}", listName, ex.Message);
                return [];
            }
        }
    }

    /// <summary>A term as the list names it: its label, with its other names in brackets.</summary>
    public static string Describe(OverlapTerm term)
    {
        var others = term.Names.Skip(1).Where(n => !string.Equals(n, term.Label, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return others.Count == 0 ? term.Label : $"{term.Label} (also: {string.Join("; ", others)})";
    }

    /// <summary>What the model is told the list holds: "game systems", "settings".</summary>
    static string ListName(string vocabulary) => ReviewText.VocabularyNoun(vocabulary) + "s";

    /// <summary>Which names were asked about, and of which model, so the same question isn't put twice.</summary>
    public static string Fingerprint(string model, IEnumerable<string> names) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{TermOverlapPrompt.Version}\n{model}\n{string.Join('\n', names.Order(StringComparer.Ordinal))}")))[..16];
}
