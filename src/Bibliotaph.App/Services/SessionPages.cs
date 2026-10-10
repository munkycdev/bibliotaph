using Bibliotaph.App.ViewModels;
using Bibliotaph.App.Views;

namespace Bibliotaph.App.Services;

/// <summary>
/// Opens a binder (Binders plan, choices 1 to 3): its reader, from Binders, Home or a new binder; its list page for
/// editing; and its pop-out window. A binder is in one place at a time: opening one that's popped out brings its
/// window forward.
/// </summary>
public sealed class SessionPages(INavigationService navigation, ReaderWindows windows, Func<SessionPackViewModel> createPack,
    Func<SessionReaderViewModel> createReader)
{
    /// <summary>The binder's reader, at <paramref name="itemId"/> if given. Nothing happens when it is already the page shown.</summary>
    public void Open(long packId, long? itemId = null, ViewerRequest? at = null)
    {
        if (WindowOf(packId) is { } window)
        {
            ReaderWindows.BringForward(window);
            return;
        }
        if (navigation.Current is SessionReaderViewModel current && current.PackId == packId) return;
        var page = createReader();
        page.PackId = packId;
        page.StartAt(itemId, at);
        navigation.Show(page);
    }

    /// <summary>Edit binder: the list page, in the main window, brought forward when asked from a popped-out binder.</summary>
    public void Edit(long packId)
    {
        ReaderWindows.BringMainForward();
        if (navigation.Current is SessionPackViewModel current && current.PackId == packId) return;
        var page = createPack();
        page.PackId = packId;
        navigation.Show(page);
    }

    /// <summary>
    /// Done on the list page: back to the binder's reader, or its window when it's popped out. Back is used when the
    /// reader is what the list page was opened from, so the history doesn't fill with copies of it.
    /// </summary>
    public void FinishEditing(long packId)
    {
        if (WindowOf(packId) is { } window)
        {
            if (!navigation.GoBack()) navigation.NavigateTo(Route.Sessions);
            ReaderWindows.BringForward(window);
            return;
        }
        if (navigation.Previous is SessionReaderViewModel reader && reader.PackId == packId) navigation.GoBack();
        else Open(packId);
    }

    /// <summary>Pop out: the binder moves to a window of its own at the same item and page; the main window goes Back.</summary>
    public async Task PopOutAsync(SessionReaderViewModel reader)
    {
        await reader.SaveNotesAsync();
        var (itemId, at) = reader.Here();
        if (ReferenceEquals(navigation.Current, reader) && !navigation.GoBack()) navigation.NavigateTo(Route.Sessions);
        if (WindowOf(reader.PackId) is { } open)
        {
            ReaderWindows.BringForward(open);
            return;
        }
        var page = createReader();
        page.PackId = reader.PackId;
        page.IsPoppedOut = true;
        page.StartAt(itemId, at);
        await windows.HostAsync(page);
    }

    /// <summary>Return to main window: the binder comes back to the main window at the same item and page, and its window closes.</summary>
    public async Task ReturnToMainWindowAsync(SessionReaderViewModel reader)
    {
        await reader.SaveNotesAsync();
        var (itemId, at) = reader.Here();
        // The main window comes forward first, so closing the pop-out doesn't hand the focus to another app.
        ReaderWindows.BringMainForward();
        windows.Close(reader);
        Open(reader.PackId, itemId, at);
    }

    /// <summary>The Binders page, as after a binder was deleted.</summary>
    public void OpenList() => navigation.NavigateUp(Route.Sessions);

    ReaderWindow? WindowOf(long packId) => windows.Find(p => p is SessionReaderViewModel { PackId: var id } && id == packId);
}
