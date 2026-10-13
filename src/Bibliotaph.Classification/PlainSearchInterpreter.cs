namespace Bibliotaph.Classification;

/// <summary>
/// Reads a request in plain English as a search. The result has passed <see cref="PlainSearchCheck"/>; running it is
/// the caller's business, and only once the user presses Search.
/// </summary>
public interface IPlainSearchInterpreter
{
    string Model { get; }

    /// <exception cref="ModelEndpointException">The endpoint can't be used right now.</exception>
    /// <exception cref="ClassifierAnswerException">The model answered, but not in the schema.</exception>
    Task<Interpretation> InterpretAsync(string request, PlainSearchContext context, CancellationToken ct = default);
}

/// <summary>
/// Ask over a model server, the built-in AI's included, through the same <see cref="IModelClient"/> as the classifier:
/// the prompt and schema from <see cref="PlainSearchPrompt"/>, the answer through <see cref="PlainSearchCheck"/>. The request goes only
/// into the user message, inside the request tags.
/// </summary>
public sealed class ModelPlainSearchInterpreter(IModelClient client, string model) : IPlainSearchInterpreter
{
    public string Model { get; } = model;

    public async Task<Interpretation> InterpretAsync(string request, PlainSearchContext context, CancellationToken ct = default)
    {
        var system = PlainSearchPrompt.SystemMessage(context.Vocabulary);
        var user = PlainSearchPrompt.UserMessage(request);
        var answer = await client.CompleteAsync(new ModelRequest(Model, system, user, PlainSearchPrompt.SchemaJson, ClassifierPrompt.ContextTokens(system, user),
            MaxTokens: PlainSearchPrompt.AnswerTokens, SchemaName: "library_search"), ct);
        return PlainSearchCheck.Check(request, PlainSearchPrompt.ParseAnswer(answer), context);
    }
}
