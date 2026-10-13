using Bibliotaph.Classification;
using Bibliotaph.Processing;

namespace Bibliotaph.App.Services;

/// <summary>
/// The smoke test's stand-in model server, at an address nothing else uses, so Ask this book can be driven in CI
/// without a real model: it lists a chat model and an embedding model, makes vectors by hashing words, so pages that
/// share a question's words come out closest to it, and answers an Ask question from the first page it is sent,
/// streaming a citation of it, a quotation from it and a citation of a page it wasn't sent. A follow-up comes back
/// unchanged when it repeats the last question, and otherwise with that question added. Any other address gets the
/// usual client, so the smoke test's checks of a server that isn't there still reach nothing.
/// </summary>
sealed class SmokeModel : IModelClient, IEmbeddingClient
{
    /// <summary>A name with no dot, which counts as this computer's network, so no "Send excerpts" tick is needed.</summary>
    public const string Endpoint = "http://smoke-model";

    public const string ChatModel = "smoke-chat";

    public const string EmbeddingModel = "smoke-embed";

    const int Dimensions = 64;

    static readonly SmokeModel Instance = new();

    /// <summary>The model server clients for the smoke test: this stand-in at <see cref="Endpoint"/>, the real client elsewhere.</summary>
    public static Func<string, IModelClient> ClientsFor(IApiKeyStore keys) =>
        endpoint => LocalModelClient.Tidy(endpoint) == Endpoint ? Instance : AiSettings.ClientFor(endpoint, keys);

    public Task<EndpointInfo> ConnectAsync(CancellationToken ct = default) =>
        Task.FromResult(new EndpointInfo(EndpointKind.OpenAiCompatible, null, [ChatModel, EmbeddingModel]));

    /// <summary>
    /// For a classification, an empty answer: the smoke test never lets the Classify lane run against it. For an Ask
    /// question (no schema), an answer from the first page sent, streamed a few characters at a time; for a follow-up's
    /// rewrite, the follow-up made to stand on its own.
    /// </summary>
    public async Task<string> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
        if (request.SchemaJson is not null) return "{}";
        if (request.System == AskPrompt.RewriteSystemMessage) return Rewrite(request.User);
        var answer = Answer(request.User);
        request.Progress?.Report(new ModelProgress(ModelStep.Waiting, ""));
        for (var i = 12; i < answer.Length; i += 12)
        {
            await Task.Delay(10, ct);
            request.Progress?.Report(new ModelProgress(ModelStep.Writing, answer[..i]));
        }
        request.Progress?.Report(new ModelProgress(ModelStep.Writing, answer));
        return answer;
    }

    /// <summary>
    /// "The answer is on page 1 [p. 1]. It says "dragon sleeps beneath the" [p. 1]. Nothing else is said [p. 999].":
    /// the first page sent, four of its words from the first of the question's words on it, and a page never sent.
    /// </summary>
    static string Answer(string user)
    {
        var lines = user.ReplaceLineEndings("\n").Split('\n');
        var question = lines.FirstOrDefault(l => l.StartsWith("Question: ", StringComparison.Ordinal))?["Question: ".Length..] ?? "";
        var at = Array.FindIndex(lines, l => l.StartsWith("[Page ", StringComparison.Ordinal));
        if (at < 0) return AskPrompt.NotAnswered;
        var marker = lines[at][6..^1];
        var text = lines.Skip(at + 1).FirstOrDefault(l => !l.StartsWith("Heading: ", StringComparison.Ordinal) && !l.StartsWith("Book: ", StringComparison.Ordinal)) ?? "";
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var first = AskRetrieval.Words(question).Select(w => Array.FindIndex(words, t => t.StartsWith(w, StringComparison.OrdinalIgnoreCase)))
            .Where(i => i >= 0).DefaultIfEmpty(0).Min();
        var quote = string.Join(' ', words.Skip(first).Take(4)).TrimEnd('.', ',', ';', ':');
        return $"The answer is on page {marker} [p. {marker}]. It says \"{quote}\" [p. {marker}]. Nothing else is said [p. 999].";
    }

    /// <summary>
    /// "And the mill? (Where does the dragon sleep?)": the follow-up with the last question after it, so it finds
    /// that question's pages; the follow-up as it was when it is that question again.
    /// </summary>
    static string Rewrite(string user)
    {
        var lines = user.ReplaceLineEndings("\n").Split('\n');
        var followUp = lines.LastOrDefault(l => l.StartsWith("Follow-up: ", StringComparison.Ordinal))?["Follow-up: ".Length..] ?? "";
        var last = lines.LastOrDefault(l => l.StartsWith("Q: ", StringComparison.Ordinal))?["Q: ".Length..] ?? "";
        return last.Length == 0 || string.Equals(followUp, last, StringComparison.OrdinalIgnoreCase) ? followUp : $"{followUp} ({last})";
    }

    public Task<IReadOnlyList<float[]>> EmbedAsync(string model, IReadOnlyList<string> texts, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<float[]>>([.. texts.Select(Vector)]);

    /// <summary>Each word (after any "search_…:" prefix) adds one to a slot chosen by a stable hash of it.</summary>
    static float[] Vector(string text)
    {
        var vector = new float[Dimensions];
        var colon = text.IndexOf(": ", StringComparison.Ordinal);
        if (text.StartsWith("search_", StringComparison.Ordinal) && colon > 0) text = text[(colon + 2)..];
        foreach (var word in AskRetrieval.Words(text))
        {
            var hash = 17u;
            foreach (var c in word) hash = unchecked(hash * 31 + c);
            vector[hash % Dimensions] += 1;
        }
        return vector;
    }
}
