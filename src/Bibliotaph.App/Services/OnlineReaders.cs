using Bibliotaph.App.ViewModels;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Processing;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.Services;

/// <summary>A book owned online, to read in the online reader: its card, its title, and its page on the service.</summary>
public sealed record OnlineReaderRequest(EntryId EntryId, string Title, string Link)
{
    /// <summary>The place the link is on, as the badge names it: "D&amp;D Beyond".</summary>
    public string? Place { get; init; }

    /// <summary>The page to open instead of <see cref="Link"/>: the one in view when a reader pops out or comes back.</summary>
    public string? At { get; init; }
}

/// <summary>
/// The online reader: a book owned online opens in a window built like the PDF reader, around the service's own page,
/// signed in through Bibliotaph's browser. Settings &gt; Library can send them to the everyday browser instead.
/// Bibliotaph copies no text from the page, so the reader has no search, page numbers or page notes.
/// </summary>
public sealed class OnlineReaders(OnlineBrowsers browsers, ReaderWindows windows, INavigationService navigation, IShellLauncher launcher,
    FavoritesService favorites, PinDirectory pins, NotesService notes, SettingsStore settings, Func<SessionActions> sessionActions,
    ILogger<OnlineReaders> log)
{
    bool? _inApp;
    bool? _readingMode;

    internal OnlineBrowsers Browsers => browsers;

    internal IShellLauncher Launcher => launcher;

    internal FavoritesService Favorites => favorites;

    internal PinDirectory Pins => pins;

    internal NotesService Notes => notes;

    internal ILogger Log => log;

    /// <summary>Add this page's binder actions, one set per reader.</summary>
    internal SessionActions NewSessionActions() => sessionActions();

    /// <summary>Whether books owned online open in Bibliotaph (the default) rather than the everyday browser.</summary>
    public async Task<bool> OpensInAppAsync()
    {
        if (_inApp is { } known) return known;
        try
        {
            _inApp = await settings.GetAsync(SettingKeys.OnlineReader) != "browser";
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Reading the online reader setting failed");
            _inApp = true;
        }
        return _inApp.Value;
    }

    public async Task SetOpensInAppAsync(bool inApp)
    {
        await settings.SetAsync(SettingKeys.OnlineReader, inApp ? "app" : "browser");
        _inApp = inApp;
    }

    /// <summary>Whether the online reader shows a service's pages in Reading mode: on unless the user turned it off.</summary>
    public async Task<bool> ReadingModeAsync()
    {
        if (_readingMode is { } known) return known;
        try
        {
            _readingMode = await settings.GetAsync(SettingKeys.OnlineReadingMode) != "False";
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Reading the Reading mode setting failed");
            _readingMode = true;
        }
        return _readingMode.Value;
    }

    public async Task SetReadingModeAsync(bool on)
    {
        _readingMode = on;
        try
        {
            await settings.SetAsync(SettingKeys.OnlineReadingMode, on ? "True" : "False");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Saving the Reading mode setting failed");
        }
    }

    /// <summary>
    /// Opens a book owned online: in the main window's online reader, or in the everyday browser when the setting says
    /// so or Bibliotaph's browser can't run here. Returns what to tell the user when it couldn't, or null.
    /// </summary>
    public async Task<string?> OpenAsync(OnlineReaderRequest request)
    {
        if (!await OpensInAppAsync() || OnlineBrowsers.RuntimeVersion is null) return OpenInBrowser(request.At ?? request.Link);
        navigation.Show(Create(request));
        ReaderWindows.BringMainForward();
        return null;
    }

    /// <summary>Opens a book owned online in a reader window of its own (a binder's Open in new window), or the everyday browser as <see cref="OpenAsync"/> does.</summary>
    public async Task<string?> OpenInWindowAsync(OnlineReaderRequest request)
    {
        if (!await OpensInAppAsync() || OnlineBrowsers.RuntimeVersion is null) return OpenInBrowser(request.At ?? request.Link);
        await windows.HostAsync(Create(request, poppedOut: true));
        return null;
    }

    /// <summary>
    /// A reader for <paramref name="request"/>; <paramref name="poppedOut"/> for a window of its own, <paramref name="inBinder"/>
    /// for a binder's reader, which hosts it in place of its file reader.
    /// </summary>
    public OnlineReaderViewModel Create(OnlineReaderRequest request, bool poppedOut = false, bool inBinder = false) =>
        new(request, this, OnlinePages.ServiceOf(request.Link)) { IsPoppedOut = poppedOut, IsInBinder = inBinder };

    /// <summary>Moves the main window's online reader to a window of its own, at the page in view; the main window goes Back.</summary>
    public async Task PopOutAsync(OnlineReaderViewModel reader)
    {
        if (ReferenceEquals(navigation.Current, reader) && !navigation.GoBack()) navigation.NavigateTo(Route.Library);
        await windows.HostAsync(Create(reader.Here(), poppedOut: true));
    }

    /// <summary>Moves a popped-out online reader back to the main window, at the page in view, and closes its window.</summary>
    public void ReturnToMainWindow(OnlineReaderViewModel reader)
    {
        navigation.Show(Create(reader.Here()));
        ReaderWindows.BringMainForward();
        windows.Close(reader);
    }

    /// <summary>Opens a page in the everyday browser. Returns what to tell the user when Windows couldn't.</summary>
    public string? OpenInBrowser(string link)
    {
        if (Core.Metadata.StoreLink.WebAddress(link) is not { } address) return "This book has no web link to open.";
        return launcher.Open(address) is { } error ? $"Windows couldn't open its page: {error}" : null;
    }

    /// <inheritdoc cref="OnlinePages.SiteOf"/>
    public static string? SiteOf(string link) => OnlinePages.SiteOf(link);
}
