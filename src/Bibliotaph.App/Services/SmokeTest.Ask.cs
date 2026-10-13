using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Documents;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Classification;
using Bibliotaph.Core;
using Bibliotaph.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.App.Services;

/// <summary>Ask this book: the reader's Ask panel, against the smoke test's stand-in model server.</summary>
static partial class SmokeTest
{
    /// <summary>
    /// With AI off the reader has no Ask and the details don't offer it. With AI on, on the stand-in model server and
    /// its embedding model, "Ask this book" in the details opens the smoke PDF with its Ask panel; a question finds the
    /// page with the dragon on it, and the stand-in's streamed answer arrives checked: its citation of that page a
    /// link, its quotation found on the page, and its citation of a page it wasn't sent taken out and the sentence
    /// marked. The citation opens the page with the quotation marked by Find. Opening the book started making its
    /// vectors; asked again once they are made, the page is found by meaning too, and the page's own link opens it with
    /// the question's word marked. The first answer moves up into the conversation; a follow-up is shown as the model rewrote it and answered from
    /// the dragon's page. Reopened, the book's panel lists the conversation, and New conversation clears it. Switching
    /// AI off again hides Ask. The Classify lane is paused meanwhile, so the stand-in reads no book, and AI
    /// and the book's place are put back afterwards.
    /// </summary>
    static async Task AskBookAsync(IServiceProvider services, Window window, long documentId)
    {
        var shell = services.GetRequiredService<ShellViewModel>();
        var navigation = services.GetRequiredService<INavigationService>();
        var ai = services.GetRequiredService<AiSettings>();
        var ask = services.GetRequiredService<AskService>();
        var indexing = services.GetRequiredService<IndexingService>();
        var reading = services.GetRequiredService<ReadingService>();
        var before = ai.Setup;
        var kept = await reading.GetPositionAsync(documentId);
        var wasPaused = indexing.IsPaused(Lane.Classify);
        indexing.Pause(Lane.Classify);
        try
        {
            shell.NavigateCommand.Execute(Route.Library);
            var library = shell.CurrentPage as LibraryViewModel ?? throw new InvalidOperationException("The Library didn't open.");
            await WaitUntilAsync(window, () => library.Items.Any(i => i.DocumentId == documentId), () => "The smoke PDF isn't in the Library.");
            await library.OpenBookCommand.ExecuteAsync(library.Items.First(i => i.DocumentId == documentId));
            var viewer = shell.CurrentPage as ViewerViewModel ?? throw new InvalidOperationException("Open didn't open the viewer.");
            await WaitUntilAsync(window, () => viewer.IsPdf, () => "The smoke PDF didn't open.");
            if (Shown(window, "ToggleAsk")) throw new InvalidOperationException("With AI off, the reader offers Ask.");
            navigation.GoBack();
            await Settle(window);
            library = shell.CurrentPage as LibraryViewModel ?? throw new InvalidOperationException("Back didn't return to the Library.");
            await library.OpenDetailsCommand.ExecuteAsync(library.Items.First(i => i.DocumentId == documentId));
            await WaitUntilAsync(window, () => library.Inspector is not null && Shown(window, "OpenBook"), () => "The smoke PDF's details didn't open.");
            if (Shown(window, "AskBook")) throw new InvalidOperationException("With AI off, the details offer Ask this book.");
            library.CloseDetailsCommand.Execute(null);
            await Settle(window);

            await ai.SaveAsync(before with
            {
                BuiltIn = false,
                Endpoint = SmokeModel.Endpoint,
                Model = SmokeModel.ChatModel,
                Provider = "openai",
                Enabled = true,
                EmbeddingModel = SmokeModel.EmbeddingModel,
            });

            // Ask this book, from the details.
            await library.OpenDetailsCommand.ExecuteAsync(library.Items.First(i => i.DocumentId == documentId));
            await WaitUntilAsync(window, () => library.Inspector is not null && Shown(window, "AskBook"), () => "With AI on, the details don't offer Ask this book.");
            Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "AskBook"), "the details' Ask this book");
            await WaitUntilAsync(window, () => shell.CurrentPage is ViewerViewModel { IsPdf: true, AskVisible: true } && Shown(window, "ToggleAsk")
                    && Descendants<TextBox>(window).Any(t => t.Name == "AskBox" && t.IsVisible && t.IsEnabled),
                () => "Ask this book didn't open the book with its Ask panel.");
            viewer = (ViewerViewModel)shell.CurrentPage!;
            // Opening the book with an embedding model set starts making its vectors, before anything is asked.
            await WaitUntilAsync(window, () => ask.FillOf(documentId) is not null, () => "Opening the book didn't start making its vectors.");

            Descendants<TextBox>(window).First(t => t.Name == "AskBox").Text = "Where does the dragon sleep?";
            await WaitUntilAsync(window, () => Descendants<Button>(window).Any(b => b.Name == "AskButton" && b.IsVisible && b.IsEnabled),
                () => "The Ask button isn't shown and enabled with a question typed.");
            Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "AskButton"), "the panel's Ask");
            await WaitUntilAsync(window, () => !viewer.Ask.IsAsking && viewer.Ask.Answered is not null
                    && viewer.Ask.Passages.FirstOrDefault() is { Passage: { Page: "1", Phrase: "dragon", ByWords: true } } && Shown(window, "AskPassageLink"),
                () => $"Asking didn't answer from the dragon's page: {viewer.Ask.Message ?? string.Join(", ", viewer.Ask.Passages.Select(p => p.Link))}.");
            var answer = viewer.Ask.Answer;
            if (answer.FirstOrDefault(p => p.Kind == AnswerPartKind.Citation) is not { PdfPage: 1 }
                || !answer.Any(p => p.Kind == AnswerPartKind.Quote) || !answer.Any(p => p.Kind == AnswerPartKind.Unsupported)
                || answer.Any(p => p.Text.Contains("999", StringComparison.Ordinal)))
                throw new InvalidOperationException($"The answer wasn't checked as it should be: {string.Join(" | ", answer.Select(p => $"{p.Kind}:{p.Text}"))}");
            if (viewer.Ask.PassagesHeading != "Read from page 1") throw new InvalidOperationException($"The pages are headed “{viewer.Ask.PassagesHeading}”.");
            if (viewer.Ask.CoverageText?.Contains("pages have text", StringComparison.Ordinal) != true)
                throw new InvalidOperationException($"The panel says “{viewer.Ask.CoverageText}” about the book's text.");
            await AuditAsync(window, "the reader's Ask panel with an answer");

            // The citation opens its page with the quotation marked.
            var pages = FindChild<Bibliotaph.Viewer.PdfPagesView>(window) ?? throw new InvalidOperationException("The viewer has no page surface.");
            await GoToFirstPageAsync(window, viewer, pages);
            var block = Descendants<TextBlock>(window).FirstOrDefault(t => t.Name == "AskAnswer" && t.IsVisible)
                ?? throw new InvalidOperationException("The answer isn't showing.");
            var link = block.Inlines.OfType<Hyperlink>().FirstOrDefault() ?? throw new InvalidOperationException("The answer's citation isn't a link.");
            if (AutomationProperties.GetName(link) != "Go to page 1") throw new InvalidOperationException($"The citation is named “{AutomationProperties.GetName(link)}”.");
            ((IInvokeProvider)ContentElementAutomationPeer.CreatePeerForElement(link).GetPattern(PatternInterface.Invoke)).Invoke();
            await WaitUntilAsync(window, () => pages.CurrentPageIndex == 1 && viewer.FindText.StartsWith("dragon", StringComparison.Ordinal)
                    && viewer.Highlights.Items.Count > 0 && viewer.FindStatus.StartsWith("1 of ", StringComparison.Ordinal),
                () => $"The citation left page {pages.CurrentPageIndex} in view with Find saying “{viewer.FindStatus}” for “{viewer.FindText}”.");

            // Opening the book made its vectors in the background; once they are all made, a question is found by meaning too.
            await WaitUntilAsync(window, () => ask.FillOf(documentId) is { Running: false, Problem: null, Done: > 0 } fill && fill.Done == fill.Total,
                () => $"The book's vectors weren't made: {ask.FillOf(documentId)}.");
            Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "AskButton"), "the panel's Ask, again");
            await WaitUntilAsync(window, () => !viewer.Ask.IsAsking && viewer.Ask.MeaningText == "Found by words and by meaning."
                    && viewer.Ask.Passages.FirstOrDefault() is { Passage: { Page: "1", ByMeaning: true } },
                () => $"Asking again didn't find the page by meaning: {viewer.Ask.MeaningText}");

            await GoToFirstPageAsync(window, viewer, pages);
            Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "AskPassageLink"), "the passage's page link");
            await WaitUntilAsync(window, () => pages.CurrentPageIndex == 1 && viewer.FindText == "dragon" && viewer.Highlights.Items.Count > 0
                    && viewer.FindStatus.StartsWith("1 of ", StringComparison.Ordinal),
                () => $"The page link left page {pages.CurrentPageIndex} in view with Find saying “{viewer.FindStatus}”.");

            // The same question again stood on its own, so it wasn't rewritten; the first answer is now the conversation's.
            await WaitUntilAsync(window, () => viewer.Ask.History.Count == 1 && viewer.Ask.LookedFor is null && Visible(window, "AskHistory"),
                () => $"After asking twice the panel lists {viewer.Ask.History.Count} earlier turns, looking for “{viewer.Ask.LookedFor}”.");

            // A follow-up is rewritten to stand on its own, then answered from the page that question finds.
            Descendants<TextBox>(window).First(t => t.Name == "AskBox").Text = "And the mill?";
            await WaitUntilAsync(window, () => Descendants<Button>(window).Any(b => b.Name == "AskButton" && b.IsVisible && b.IsEnabled),
                () => "The Ask button isn't shown and enabled with a follow-up typed.");
            Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "AskButton"), "the panel's Ask, for a follow-up");
            await WaitUntilAsync(window, () => !viewer.Ask.IsAsking && viewer.Ask.Answered is not null && viewer.Ask.History.Count == 2
                    && viewer.Ask.LookedFor == "Looked for: And the mill? (Where does the dragon sleep?)" && Visible(window, "AskLookedFor")
                    && viewer.Ask.Answer.FirstOrDefault(p => p.Kind == AnswerPartKind.Citation) is { PdfPage: 1 },
                () => $"The follow-up wasn't rewritten and answered: {viewer.Ask.Message ?? viewer.Ask.LookedFor ?? "nothing looked for"}.");
            await AuditAsync(window, "the reader's Ask panel with a conversation");
            if ((await ask.HistoryAsync(documentId)).Count != 3) throw new InvalidOperationException("The conversation wasn't kept.");

            // Reopened, the book's panel lists the conversation, and New conversation clears it.
            navigation.GoBack();
            await Settle(window);
            library = shell.CurrentPage as LibraryViewModel ?? throw new InvalidOperationException("Back didn't return to the Library.");
            await WaitUntilAsync(window, () => library.Items.Any(i => i.DocumentId == documentId), () => "The smoke PDF isn't in the Library on the way back.");
            if (library.Inspector is null) await library.OpenDetailsCommand.ExecuteAsync(library.Items.First(i => i.DocumentId == documentId));
            await WaitUntilAsync(window, () => library.Inspector is not null && Shown(window, "AskBook"), () => "The smoke PDF's details don't offer Ask this book.");
            Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "AskBook"), "the details' Ask this book, again");
            await WaitUntilAsync(window, () => shell.CurrentPage is ViewerViewModel { AskVisible: true } reopened
                    && reopened.Ask.History.Count == 3 && Visible(window, "AskHistory") && Shown(window, "AskNewConversation"),
                () => "Reopening the book didn't list its conversation.");
            viewer = (ViewerViewModel)shell.CurrentPage!;
            Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "AskNewConversation"), "the panel's New conversation");
            await WaitUntilAsync(window, () => viewer.Ask.History.Count == 0 && !Visible(window, "AskHistory") && !Shown(window, "AskNewConversation"),
                () => "New conversation didn't clear the panel.");
            if ((await ask.HistoryAsync(documentId)).Count != 0) throw new InvalidOperationException("New conversation didn't forget the conversation.");

            await ai.SaveAsync(before);
            await WaitUntilAsync(window, () => !viewer.Ask.IsOn && !Shown(window, "ToggleAsk") && !Shown(window, "AskButton"),
                () => "Switching AI off didn't hide Ask.");
            viewer.AskVisible = false;
            navigation.GoBack();
            await Settle(window);
            if (shell.CurrentPage is LibraryViewModel back)
            {
                back.CloseDetailsCommand.Execute(null);
                await Settle(window);
            }
        }
        finally
        {
            await ask.ClearHistoryAsync(documentId);
            await ai.SaveAsync(before);
            if (!wasPaused) indexing.Resume(Lane.Classify);
            await reading.SavePositionAsync(documentId, kept);
        }
    }

    /// <summary>
    /// Ask your library: with AI on, on the stand-in model server, a question typed in the top bar in Ask mode and sent
    /// with Answer from my books finds the dragon's page in the smoke PDF among the books the Library shows, and the
    /// stand-in's answer arrives checked, its citation naming the book and page; the citation opens the book at that
    /// page. Ask is closed and AI put back afterwards.
    /// </summary>
    static async Task AskLibraryAsync(IServiceProvider services, Window window, long documentId)
    {
        var shell = services.GetRequiredService<ShellViewModel>();
        var ai = services.GetRequiredService<AiSettings>();
        var indexing = services.GetRequiredService<IndexingService>();
        var reading = services.GetRequiredService<ReadingService>();
        var before = ai.Setup;
        var kept = await reading.GetPositionAsync(documentId);
        var wasPaused = indexing.IsPaused(Lane.Classify);
        indexing.Pause(Lane.Classify);
        try
        {
            await ai.SaveAsync(before with
            {
                BuiltIn = false,
                Endpoint = SmokeModel.Endpoint,
                Model = SmokeModel.ChatModel,
                Provider = "openai",
                Enabled = true,
                EmbeddingModel = SmokeModel.EmbeddingModel,
            });
            shell.NavigateCommand.Execute(Route.Library);
            await Settle(window);
            shell.PlainSearch.IsOn = true;
            shell.SearchText = "Where does the dragon sleep?";
            await WaitUntilAsync(window, () => Shown(window, "LibraryAskButton"), () => "Ask in the top bar doesn't offer Answer from my books.");
            Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "LibraryAskButton"), "Answer from my books");
            var asked = shell.PlainSearch.Library;
            await WaitUntilAsync(window, () => !asked.IsAsking && asked.Answered is not null && Visible(window, "LibraryAskAnswer")
                    && asked.Answer.Any(p => p is { Kind: AnswerPartKind.Citation, PdfPage: 1 } && p.DocumentId == documentId),
                () => $"Asking the library didn't answer from the dragon's page: {asked.Message ?? string.Join(", ", asked.Passages.Select(p => p.Link))}.");
            if (asked.Answer.Any(p => p.Text.Contains("999", StringComparison.Ordinal)))
                throw new InvalidOperationException("The answer kept a citation of a page it wasn't sent.");
            await AuditAsync(window, "Ask in the top bar with an answer from the library");

            var block = Descendants<TextBlock>(window).FirstOrDefault(t => t.Name == "LibraryAskAnswer" && t.IsVisible)
                ?? throw new InvalidOperationException("The library's answer isn't showing.");
            var link = block.Inlines.OfType<Hyperlink>().FirstOrDefault() ?? throw new InvalidOperationException("The answer's citation isn't a link.");
            if (AutomationProperties.GetName(link) is not { } name || !name.StartsWith("Open ", StringComparison.Ordinal) || !name.EndsWith(", p. 1", StringComparison.Ordinal))
                throw new InvalidOperationException($"The citation is named “{AutomationProperties.GetName(link)}”.");
            ((IInvokeProvider)ContentElementAutomationPeer.CreatePeerForElement(link).GetPattern(PatternInterface.Invoke)).Invoke();
            await WaitUntilAsync(window, () => shell.CurrentPage is ViewerViewModel { IsPdf: true, CurrentPageIndex: 1 } viewer
                    && viewer.Here()?.DocumentId == documentId,
                () => "The citation didn't open the book at its page.");
            shell.PlainSearch.IsOn = false;
            shell.SearchText = "";
            shell.GoBackCommand.Execute(null);
            await Settle(window);
        }
        finally
        {
            shell.PlainSearch.IsOn = false;
            await ai.SaveAsync(before);
            if (!wasPaused) indexing.Resume(Lane.Classify);
            await reading.SavePositionAsync(documentId, kept);
        }
    }

    static async Task GoToFirstPageAsync(Window window, ViewerViewModel viewer, Bibliotaph.Viewer.PdfPagesView pages)
    {
        viewer.PageEntry = "i";
        viewer.GoToPageCommand.Execute(null);
        await WaitUntilAsync(window, () => pages.CurrentPageIndex == 0, () => "Going to page i didn't show the first page.");
    }

    /// <summary>Whether the control named <paramref name="name"/> is showing, whatever kind it is (<see cref="Shown"/> looks only at buttons).</summary>
    static bool Visible(Window window, string name) => Descendants<FrameworkElement>(window).Any(e => e.Name == name && e.IsVisible);
}
