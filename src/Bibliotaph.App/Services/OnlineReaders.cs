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
/// The online reader (online libraries plan, O2b, choices 19 to 22): a book owned online opens in a window built like
/// the PDF reader, around the service's own page, signed in through Bibliotaph's browser. Settings &gt; Library can send
/// them to the everyday browser instead. Bibliotaph copies no text from the page, so the reader has no search, page
/// numbers or page notes.
/// </summary>
public sealed class OnlineReaders(OnlineBrowsers browsers, ReaderWindows windows, INavigationService navigation, IShellLauncher launcher,
    FavoritesService favorites, PinDirectory pins, NotesService notes, OnlineImports imports, SettingsStore settings, ILogger<OnlineReaders> log)
{
    bool? _inApp;

    internal OnlineBrowsers Browsers => browsers;

    internal IShellLauncher Launcher => launcher;

    internal FavoritesService Favorites => favorites;

    internal PinDirectory Pins => pins;

    internal NotesService Notes => notes;

    internal ILogger Log => log;

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

    /// <summary>
    /// Opens a book owned online: in the main window's online reader, or in the everyday browser when the setting says
    /// so or Bibliotaph's browser can't run here. Returns what to tell the user when it couldn't, or null.
    /// </summary>
    public async Task<string?> OpenAsync(OnlineReaderRequest request)
    {
        if (!await OpensInAppAsync() || OnlineBrowsers.RuntimeVersion is null) return OpenInBrowser(request.Link);
        navigation.Show(Create(request));
        ReaderWindows.BringMainForward();
        return null;
    }

    /// <summary>A reader for <paramref name="request"/>; <paramref name="poppedOut"/> for a window of its own.</summary>
    public OnlineReaderViewModel Create(OnlineReaderRequest request, bool poppedOut = false) =>
        new(request, this, SignInSites(request.Link)) { IsPoppedOut = poppedOut };

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

    /// <summary>
    /// Sites a service signs in on besides its own, which the reader follows too (D&amp;D Beyond signs in on Wizards of
    /// the Coast's site).
    /// </summary>
    IReadOnlyList<string> SignInSites(string link) =>
        imports.Readers.FirstOrDefault(r => SiteOf(r.LibraryPage.AbsoluteUri) is { } site && site == SiteOf(link))?.SignInSites ?? [];

    /// <summary>A page's site: its host without "www.", lowercase; null for a link that isn't on the web.</summary>
    public static string? SiteOf(string link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? (uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host).ToLowerInvariant()
            : null;
}
