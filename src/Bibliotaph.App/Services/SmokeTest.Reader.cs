using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.App.Services;

/// <summary>The reader's thumbnail strip, and a binder opening its next page's book ahead of time.</summary>
static partial class SmokeTest
{
    /// <summary>
    /// The thumbnail strip: hidden at first, shown by its toolbar button with a thumbnail for each page, drawn through
    /// the viewer worker, the page in view outlined and a badge on the page with a note. A click on a thumbnail goes to
    /// its page, and so does Enter on one. It is hidden again, and the note removed, for the checks after.
    /// </summary>
    static async Task UseThumbnailStripAsync(IServiceProvider services, Window window, long documentId)
    {
        var shell = services.GetRequiredService<ShellViewModel>();
        var navigation = services.GetRequiredService<INavigationService>();
        var notes = services.GetRequiredService<NotesService>();
        var reading = services.GetRequiredService<ReadingService>();
        var kept = await reading.GetPositionAsync(documentId);
        // A note on the second page (printed page 1), as the reader's Note makes one.
        var note = await notes.AddPageNoteAsync(documentId, 1, 1, "The mill wheel turns at dawn.")
            ?? throw new InvalidOperationException("The page note for the strip wasn't added.");

        shell.NavigateCommand.Execute(Route.Library);
        var library = shell.CurrentPage as LibraryViewModel ?? throw new InvalidOperationException("The Library didn't open.");
        await WaitUntilAsync(window, () => library.Items.Any(i => i.DocumentId == documentId), () => "The smoke PDF isn't in the Library.");
        await library.OpenBookCommand.ExecuteAsync(library.Items.First(i => i.DocumentId == documentId));
        var viewer = shell.CurrentPage as ViewerViewModel ?? throw new InvalidOperationException("Open didn't open the viewer.");
        await WaitUntilAsync(window, () => viewer.IsPdf && viewer.HasPageNotes && Shown(window, "ToggleStrip"),
            () => $"The PDF didn't open with its page note and the Page thumbnails button (still {viewer.Mode}).");
        if (viewer.ShowStrip || Descendants<Border>(window).Any(b => b.Name == "StripPanel" && b.IsVisible))
            throw new InvalidOperationException("The thumbnail strip showed before it was asked for.");

        Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "ToggleStrip"), "the reader's Page thumbnails");
        ListBox? strip = null;
        await WaitUntilAsync(window, () => (strip = Descendants<ListBox>(window).FirstOrDefault(l => l.Name == "PageStrip" && l.IsVisible)) is { Items.Count: 2 },
            () => "Page thumbnails didn't show a thumbnail for each page.");
        bool Badged(int index) => strip!.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem item
            && Descendants<Border>(item).Any(b => b.Name == "NoteBadge" && b.IsVisible);
        await WaitUntilAsync(window, () => viewer.Strip.Pages is [{ Image: not null }, { Image: not null }],
            () => "The viewer worker didn't draw the pages' thumbnails.");
        await WaitUntilAsync(window, () => Badged(1) && !Badged(0) && viewer.Strip.Pages[1].AccessibleName.Contains("1 note", StringComparison.Ordinal),
            () => "The page with a note has no badge in the strip, or the page without one has.");
        await WaitUntilAsync(window, () => viewer.Strip.Current is { } current && current.Index == viewer.CurrentPageIndex && current.IsCurrent,
            () => "The strip doesn't mark the page in view.");
        await AuditAsync(window, "the reader with its page thumbnails");

        // A click on the other page's thumbnail goes there; Enter on the thumbnail of the page it was on comes back.
        var other = viewer.CurrentPageIndex == 0 ? 1 : 0;
        if (strip!.ItemContainerGenerator.ContainerFromIndex(other) is not ListBoxItem { IsVisible: true } target)
            throw new InvalidOperationException("The other page's thumbnail isn't showing, so it can't be clicked.");
        // MouseLeftButtonUp is raised by each element as a mouse-up bubbles past it, so the click is a bubbling mouse-up
        // from the thumbnail, which reaches the strip's own handler.
        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseUpEvent });
        await WaitUntilAsync(window, () => viewer.CurrentPageIndex == other && viewer.Strip.Current?.Index == other,
            () => $"Clicking page {other + 1}'s thumbnail didn't go to it (at page index {viewer.CurrentPageIndex}).");
        var back = 1 - other;
        if (strip.ItemContainerGenerator.ContainerFromIndex(back) is not ListBoxItem { IsVisible: true } returning)
            throw new InvalidOperationException("The thumbnail of the page it was on isn't showing, so Enter can't reach it.");
        returning.Focus();
        var source = PresentationSource.FromVisual(returning) ?? throw new InvalidOperationException("The strip isn't on screen.");
        returning.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
        await WaitUntilAsync(window, () => viewer.CurrentPageIndex == back, () => $"Enter on page {back + 1}'s thumbnail didn't go to it.");

        // Hidden again, as later checks expect, and the book left as it was.
        Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "ToggleStrip"), "the reader's Page thumbnails");
        await WaitUntilAsync(window, () => !viewer.ShowStrip && !Descendants<Border>(window).Any(b => b.Name == "StripPanel" && b.IsVisible),
            () => "Page thumbnails didn't hide the strip again.");
        navigation.GoBack();
        await Settle(window);
        await notes.DeletePageNoteAsync(note.Id);
        await reading.SavePositionAsync(documentId, kept);
    }

    /// <summary>
    /// Reading ahead across books: in a binder whose second page is in another book, that book is opened ahead of time,
    /// with the page drawn, while the first page is read; Next then shows it from the page already drawn.
    /// </summary>
    static async Task ReadAheadAcrossBooksAsync(IServiceProvider services, Window window, long documentId)
    {
        const string ZippedTitle = "Smoke Zipped Book";
        var shell = services.GetRequiredService<ShellViewModel>();
        var sessions = services.GetRequiredService<SessionsService>();
        var queries = services.GetRequiredService<LibraryQueries>();
        var zipped = (await Task.Run(() => queries.ListAsync(new LibraryFilter()))).FirstOrDefault(e => e.Title == ZippedTitle)
            ?? throw new InvalidOperationException("The zipped PDF from the check before isn't in the library.");
        var pack = await sessions.CreateAsync("Two books");
        await sessions.AddPagesAsync(pack.Id, documentId, 0, 0, "Front matter");
        await sessions.AddPagesAsync(pack.Id, zipped.DocumentId, 1, 1, "The zipped chapter");

        services.GetRequiredService<SessionPages>().Open(pack.Id);
        await WaitUntilAsync(window, () => shell.CurrentPage is SessionReaderViewModel { IsLoaded: true, Current.Heading: "Front matter", Viewer.IsPdf: true },
            () => "The binder didn't open at its first page.");
        var reader = (SessionReaderViewModel)shell.CurrentPage!;
        await WaitUntilAsync(window, () => reader.Viewer.ReadyAhead == zipped.DocumentId,
            () => "The binder didn't open the next page's book ahead of time, with its page drawn.");
        Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "NextItem"), "Next");
        await WaitUntilAsync(window, () => reader is { Current.Heading: "The zipped chapter", Viewer: { IsPdf: true, CurrentPageIndex: 1, Pdf.Seed.PageIndex: 1 } },
            () => $"Next didn't show the book opened ahead from its page drawn ahead ({reader.Counter}, reader {reader.Viewer.Mode}).");
        await WaitUntilAsync(window, () => reader.Viewer.ReadyAhead is null, () => "The binder's last page left a book open ahead of time.");

        // Leave the library as it was for the checks after this one.
        shell.NavigateCommand.Execute(Route.Sessions);
        await Settle(window);
        await sessions.DeleteAsync(pack.Id);
    }
}
