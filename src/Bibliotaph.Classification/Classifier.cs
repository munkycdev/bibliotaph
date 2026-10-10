using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Classification;

/// <summary>A book as the classifier sees it: the excerpt it reads, and every page's text for the evidence check.</summary>
public sealed record ClassifierInput(Excerpt Excerpt, string FileName, Func<int, string?> PageText, Vocabulary Vocabulary);

/// <summary>What one classification produced: the claims that passed the evidence check, and those that didn't.</summary>
public sealed record ClassifierResult(string Provider, string Model, IReadOnlyList<CheckedClaim> Accepted, IReadOnlyList<DroppedClaim> Dropped);

/// <summary>
/// Classifies a book (architecture, "Metadata and classification"). The result's claims have passed the evidence check;
/// what to store is the caller's business.
/// </summary>
public interface IClassifier
{
    string Provider { get; }

    string Model { get; }

    /// <exception cref="ModelEndpointException">The endpoint can't be used right now.</exception>
    /// <exception cref="ClassifierAnswerException">
    /// The model answered, but not in the schema, or couldn't answer for this book while it still answers others.
    /// </exception>
    Task<ClassifierResult> ClassifyAsync(ClassifierInput input, CancellationToken ct = default);
}

/// <summary>
/// The classifier over a model server: the prompt and schema from <see cref="ClassifierPrompt"/>, the answer through
/// <see cref="EvidenceCheck"/>. The book's text goes only into the user message, inside the excerpt tags (A16).
/// </summary>
public sealed class ModelClassifier(IModelClient client, string provider, string model) : IClassifier
{
    public string Provider { get; } = provider;

    public string Model { get; } = model;

    public async Task<ClassifierResult> ClassifyAsync(ClassifierInput input, CancellationToken ct = default)
    {
        var system = ClassifierPrompt.SystemMessage(input.Vocabulary);
        var user = ClassifierPrompt.UserMessage(input.Excerpt, input.FileName);
        var request = new ModelRequest(Model, system, user, ClassifierPrompt.SchemaJson, ClassifierPrompt.ContextTokens(system, user));
        string answer;
        try
        {
            answer = await client.CompleteAsync(request, ct);
        }
        catch (ModelRequestException ex)
        {
            // The server took the book and failed on it. If it still answers a question with nothing of the book in it,
            // the failure is this book's, and the caller can move on to the next; if not, this throws the server's
            // problem and the caller waits for it.
            await CheckAsync(request.ContextTokens, ct);
            throw new ClassifierAnswerException($"The model couldn't read this book. {ex.Message}", ex);
        }
        var claims = ClassifierPrompt.ParseAnswer(answer);
        var checkedClaims = EvidenceCheck.Check(claims, input.Excerpt, input.PageText, input.Vocabulary);
        return new ClassifierResult(Provider, Model, checkedClaims.Accepted, checkedClaims.Dropped);
    }

    /// <summary>
    /// Asks the model for a few tokens of JSON with nothing of any book in it. Sent with the same context window as the
    /// book was, so Ollama doesn't load the model again to answer it.
    /// </summary>
    /// <exception cref="ModelEndpointException">The server can't answer even that.</exception>
    Task<string> CheckAsync(int contextTokens, CancellationToken ct) =>
        client.CompleteAsync(new ModelRequest(Model, CheckSystem, CheckUser, CheckSchemaJson, contextTokens, MaxTokens: 32), ct);

    const string CheckSystem = "You check that a model server is working. Answer only with the JSON asked for.";

    const string CheckUser = """Answer with {"ok": true}.""";

    const string CheckSchemaJson = """{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"],"additionalProperties":false}""";
}
