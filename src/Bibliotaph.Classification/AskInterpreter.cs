namespace Bibliotaph.Classification;

/// <summary>
/// Reads a request in plain English as a search (scenario review, row 5). The result has passed <see cref="AskCheck"/>;
/// running it is the caller's business, and only once the user presses Search.
/// </summary>
public interface IAskInterpreter
{
    string Model { get; }

    /// <exception cref="ModelEndpointException">The endpoint can't be used right now.</exception>
    /// <exception cref="ClassifierAnswerException">The model answered, but not in the schema.</exception>
    Task<Interpretation> InterpretAsync(string request, AskContext context, CancellationToken ct = default);
}

/// <summary>
/// Ask over a model server, the built-in AI's included, through the same <see cref="IModelClient"/> as the classifier:
/// the prompt and schema from <see cref="AskPrompt"/>, the answer through <see cref="AskCheck"/>. The request goes only
/// into the user message, inside the request tags.
/// </summary>
public sealed class ModelAskInterpreter(IModelClient client, string model) : IAskInterpreter
{
    public string Model { get; } = model;

    public async Task<Interpretation> InterpretAsync(string request, AskContext context, CancellationToken ct = default)
    {
        var system = AskPrompt.SystemMessage(context.Vocabulary);
        var user = AskPrompt.UserMessage(request);
        var answer = await client.CompleteAsync(new ModelRequest(Model, system, user, AskPrompt.SchemaJson, ClassifierPrompt.ContextTokens(system, user),
            MaxTokens: AskPrompt.AnswerTokens, SchemaName: "library_search"), ct);
        return AskCheck.Check(request, AskPrompt.ParseAnswer(answer), context);
    }
}
