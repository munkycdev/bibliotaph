using Bibliotaph.Processing;

namespace Bibliotaph.App.Services;

/// <summary>The question Reprocess with OCR on every page asks first, in the words the web asks it too.</summary>
static class ReprocessPrompt
{
    public static bool ConfirmOcrEveryPage(IUserPrompts prompts, string title, int pages) => prompts.Confirm(
        DetailsText.OcrEveryPageQuestion(title, pages) + "\n\n" + DetailsText.OcrEveryPageNote,
        "Reprocess with OCR on every page?", PromptKind.Question, okByDefault: true);
}
