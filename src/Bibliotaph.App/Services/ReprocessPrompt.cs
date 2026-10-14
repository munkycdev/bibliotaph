using System.Globalization;
using Bibliotaph.Core.Progress;

namespace Bibliotaph.App.Services;

/// <summary>
/// The question Reprocess with OCR on every page asks first. Plain Reprocess doesn't ask: nothing of the user's is at
/// risk. OCR is slow enough (about a quarter of a second a page) that a long book merits a word first.
/// </summary>
static class ReprocessPrompt
{
    static readonly TimeSpan PerPage = TimeSpan.FromMilliseconds(250);

    public static bool ConfirmOcrEveryPage(IUserPrompts prompts, string title, int pages) => prompts.Confirm(
        $"Bibliotaph will read all {pages.ToString("N0", CultureInfo.CurrentCulture)} {(pages == 1 ? "page" : "pages")} of “{title}” with OCR, " +
        $"including pages whose text came from the PDF itself. That takes {TimeLeft.Describe(PerPage * pages)}.\n\n" +
        "Until each page has been read, its current text stays searchable. Nothing you added to the book changes.",
        "Reprocess with OCR on every page?", PromptKind.Question, okByDefault: true);
}
