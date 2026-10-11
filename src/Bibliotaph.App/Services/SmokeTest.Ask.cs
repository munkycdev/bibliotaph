using System.Windows;
using System.Windows.Controls;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Core;
using Bibliotaph.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.App.Services;

/// <summary>Ask this book, Q1: the reader's Ask panel, against the smoke test's stand-in model server.</summary>
static partial class SmokeTest
{
    /// <summary>
    /// With AI off the reader has no Ask. With AI on, on the stand-in model server and its embedding model, Ask opens
    /// beside the smoke PDF, a question finds the page with the dragon on it, by its words and then by its meaning once
    /// the book's vectors are made, and the page's link opens it with "dragon" marked by Find. Switching AI off again
    /// hides Ask. The Classify lane is paused meanwhile, so the stand-in reads no book, and AI and the book's place are
    /// put back afterwards.
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

            await ai.SaveAsync(before with
            {
                BuiltIn = false,
                Endpoint = SmokeModel.Endpoint,
                Model = SmokeModel.ChatModel,
                Provider = "openai",
                Enabled = true,
                EmbeddingModel = SmokeModel.EmbeddingModel,
            });
            await WaitUntilAsync(window, () => viewer.Ask.IsOn && Shown(window, "ToggleAsk"), () => "With AI on, the reader doesn't offer Ask.");
            Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "ToggleAsk"), "the reader's Ask");
            await WaitUntilAsync(window, () => viewer.AskVisible && Descendants<TextBox>(window).Any(t => t.Name == "AskBox" && t.IsVisible && t.IsEnabled),
                () => "Ask didn't open the Ask panel with its question box.");
            Descendants<TextBox>(window).First(t => t.Name == "AskBox").Text = "Where does the dragon sleep?";
            await WaitUntilAsync(window, () => Descendants<Button>(window).Any(b => b.Name == "AskButton" && b.IsVisible && b.IsEnabled),
                () => "The Ask button isn't shown and enabled with a question typed.");
            Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "AskButton"), "the panel's Ask");
            await WaitUntilAsync(window, () => !viewer.Ask.IsAsking && viewer.Ask.Passages.FirstOrDefault() is { Passage: { Page: "1", Phrase: "dragon", ByWords: true } }
                    && Shown(window, "AskPassageLink"),
                () => $"Asking didn't list the dragon's page first: {viewer.Ask.Message ?? string.Join(", ", viewer.Ask.Passages.Select(p => p.Link))}.");
            if (viewer.Ask.CoverageText?.Contains("pages have text", StringComparison.Ordinal) != true)
                throw new InvalidOperationException($"The panel says “{viewer.Ask.CoverageText}” about the book's text.");

            // The first question made the book's vectors in the background; the next is answered by meaning too.
            await WaitUntilAsync(window, () => ask.FillOf(documentId) is { Running: false, Problem: null, Done: > 0 } fill && fill.Done == fill.Total,
                () => $"The book's vectors weren't made: {ask.FillOf(documentId)}.");
            Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "AskButton"), "the panel's Ask, again");
            await WaitUntilAsync(window, () => !viewer.Ask.IsAsking && viewer.Ask.MeaningText == "Found by words and by meaning."
                    && viewer.Ask.Passages.FirstOrDefault() is { Passage: { Page: "1", ByMeaning: true } },
                () => $"Asking again didn't find the page by meaning: {viewer.Ask.MeaningText}");
            await AuditAsync(window, "the reader's Ask panel with the pages found");

            var pages = FindChild<Bibliotaph.Viewer.PdfPagesView>(window) ?? throw new InvalidOperationException("The viewer has no page surface.");
            viewer.PageEntry = "i";
            viewer.GoToPageCommand.Execute(null);
            await WaitUntilAsync(window, () => pages.CurrentPageIndex == 0, () => "Going to page i didn't show the first page.");
            Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "AskPassageLink"), "the passage's page link");
            await WaitUntilAsync(window, () => pages.CurrentPageIndex == 1 && viewer.FindText == "dragon" && viewer.Highlights.Items.Count > 0
                    && viewer.FindStatus.StartsWith("1 of ", StringComparison.Ordinal),
                () => $"The page link left page {pages.CurrentPageIndex} in view with Find saying “{viewer.FindStatus}”.");

            await ai.SaveAsync(before);
            await WaitUntilAsync(window, () => !viewer.Ask.IsOn && !Shown(window, "ToggleAsk") && !Shown(window, "AskButton"),
                () => "Switching AI off didn't hide Ask.");
            viewer.AskVisible = false;
            navigation.GoBack();
            await Settle(window);
        }
        finally
        {
            await ai.SaveAsync(before);
            if (!wasPaused) indexing.Resume(Lane.Classify);
            await reading.SavePositionAsync(documentId, kept);
        }
    }
}
