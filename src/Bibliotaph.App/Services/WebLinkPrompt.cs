namespace Bibliotaph.App.Services;

/// <summary>
/// The question asked before a web address opens in the browser: a PDF's web links and a book's Get it from links ask
/// the same one.
/// </summary>
static class WebLinkPrompt
{
    public static bool ConfirmOpen(IUserPrompts prompts, string uri) =>
        prompts.Confirm($"This link goes to a web page:\n\n{uri}\n\nOpen it in your browser?", "Open a web link?", PromptKind.Question, okByDefault: false);
}
