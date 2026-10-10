using System.Windows;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Core;
using Bibliotaph.Processing;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.Services;

/// <summary>
/// What a book's card menu and its details do about its text (slice 4i plan, choices 4 and 6): Open in another app for
/// a file Bibliotaph can't read, and Forget its text and Read it again. The page that runs one refreshes after it.
/// </summary>
public sealed class BookTextActions(OtherApps otherApps, ForgetText forget, ILogger<BookTextActions> log)
{
    /// <summary>Opens the book's file in the default app for it. Says so when it can't.</summary>
    public async Task OpenElsewhereAsync(long documentId)
    {
        if (await otherApps.OpenAsync(documentId) is { } problem) Tell(problem, "Open in another app");
    }

    /// <summary>Forgets the text of every copy of the book. Returns whether it is all gone.</summary>
    public async Task<bool> ForgetAsync(EntryId entryId)
    {
        try
        {
            if (await Task.Run(() => forget.ForgetAsync(entryId))) return true;
            Tell("Bibliotaph was still reading this book, so some of its text is still stored. Choose Forget its text again in a moment.",
                "Forget its text");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Forgetting the text of entry {EntryId} failed", entryId.Value);
            Tell($"Its text couldn't be forgotten: {ex.Message}", "Forget its text");
        }
        return false;
    }

    /// <summary>Reads every copy of the book again, ahead of other work.</summary>
    public async Task ReadAgainAsync(EntryId entryId)
    {
        try
        {
            await Task.Run(() => forget.ReadAgainAsync(entryId));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Reading entry {EntryId} again failed", entryId.Value);
            Tell($"It couldn't be read again: {ex.Message}", "Read it again");
        }
    }

    static void Tell(string message, string title)
    {
        if (Application.Current?.MainWindow is { } owner)
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>For a card: its book's own document, or nothing for a card with no file.</summary>
    public static bool HasFile(LibraryItemViewModel? item) => item is { IsElsewhere: false, DocumentId: > 0 };
}
