using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Classification;

/// <summary>
/// A book as the classifier sees it: the excerpt it reads, every page's text for the evidence check, and the user's
/// books sent as worked examples, which the evidence check never reads.
/// </summary>
public sealed record ClassifierInput(Excerpt Excerpt, string FileName, Func<int, string?> PageText, Vocabulary Vocabulary,
    IReadOnlyList<WorkedExample>? Examples = null);

/// <summary>What one classification produced: the claims that passed the evidence check, and those that didn't.</summary>
public sealed record ClassifierResult(string Provider, string Model, IReadOnlyList<CheckedClaim> Accepted, IReadOnlyList<DroppedClaim> Dropped);

/// <summary>
/// Classifies a book over a model server, the built-in AI's included: the prompt and schema from <see
/// cref="ClassifierPrompt"/>, the answer through <see cref="EvidenceCheck"/>; and the same model finding a compilation's
/// adventures, through <see cref="PartFinderPrompt"/> and <see cref="PartCheck"/>. The book's text goes only into the user
/// message, inside the excerpt tags. The results have passed their checks; what to store is the caller's business.
/// Tests stand in for the model with an <see cref="IModelClient"/>, not for this class.
/// </summary>
public sealed class ModelClassifier(IModelClient client, string provider, string model)
{
    public string Provider { get; } = provider;

    public string Model { get; } = model;

    /// <summary>Classifies <paramref name="input"/>, reporting the answer as it is written when <paramref name="progress"/> is given.</summary>
    /// <exception cref="ModelEndpointException">The endpoint can't be used right now.</exception>
    /// <exception cref="ClassifierAnswerException">
    /// The model answered, but not in the schema, or couldn't answer for this book while it still answers others.
    /// </exception>
    public async Task<ClassifierResult> ClassifyAsync(ClassifierInput input, IProgress<ModelProgress>? progress = null, CancellationToken ct = default)
    {
        var system = ClassifierPrompt.SystemMessage(input.Vocabulary);
        var user = ClassifierPrompt.UserMessage(input.Excerpt, input.FileName, input.Examples);
        var request = new ModelRequest(Model, system, user, ClassifierPrompt.SchemaJson, ClassifierPrompt.ContextTokens(system, user), Progress: progress);
        var claims = ClassifierPrompt.ParseAnswer(await CompleteAsync(request, ct));
        var checkedClaims = EvidenceCheck.Check(claims, input.Excerpt, input.PageText, input.Vocabulary);
        return new ClassifierResult(Provider, Model, checkedClaims.Accepted, checkedClaims.Dropped);
    }

    /// <summary>
    /// The adventures the model found among <paramref name="input"/>'s candidates whose evidence checks out, with where
    /// the book says it is a collection; none when fewer than two do, or the book doesn't say so.
    /// </summary>
    /// <exception cref="ModelEndpointException">The endpoint can't be used right now.</exception>
    /// <exception cref="ClassifierAnswerException">The model answered, but not in the schema, or couldn't answer for this file.</exception>
    public async Task<PartFinderResult> FindPartsAsync(PartFinderInput input, CancellationToken ct = default)
    {
        var system = PartFinderPrompt.SystemMessage();
        var user = PartFinderPrompt.UserMessage(input);
        var request = new ModelRequest(Model, system, user, PartFinderPrompt.SchemaJson, ClassifierPrompt.ContextTokens(system, user));
        return PartCheck.Check(PartFinderPrompt.ParseAnswer(await CompleteAsync(request, ct)), input);
    }

    async Task<string> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        try
        {
            return await client.CompleteAsync(request, ct);
        }
        catch (ModelContextException ex)
        {
            // The server said exactly why: this book's text, not the server, so there is nothing to check.
            throw new ClassifierAnswerException($"The model couldn't read this book. {ex.Message}", ex);
        }
        catch (ModelRequestException ex)
        {
            // The server took the book and failed on it. If it still answers a question with nothing of the book in it,
            // the failure is this book's, and the caller can move on to the next; if not, this throws the server's
            // problem and the caller waits for it.
            await CheckAsync(client, Model, request.ContextTokens, ct);
            throw new ClassifierAnswerException($"The model couldn't read this book. {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Asks the model for a few tokens of JSON with nothing of any book in it. Sent with the same context window as the
    /// book was, so Ollama doesn't load the model again to answer it.
    /// </summary>
    /// <exception cref="ModelEndpointException">The server can't answer even that.</exception>
    internal static Task<string> CheckAsync(IModelClient client, string model, int contextTokens, CancellationToken ct) =>
        client.CompleteAsync(new ModelRequest(model, CheckSystem, CheckUser, CheckSchemaJson, contextTokens, MaxTokens: 32), ct);

    const string CheckSystem = "You check that a model server is working. Answer only with the JSON asked for.";

    const string CheckUser = """Answer with {"ok": true}.""";

    const string CheckSchemaJson = """{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"],"additionalProperties":false}""";
}
