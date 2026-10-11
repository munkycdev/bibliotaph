using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// A book owned online, read in Bibliotaph (online libraries plan, O2b): the service's own page in the browser, with
/// the reader's heart, pin, the book's own note, Pop out and Open in your browser around it. The view drives the
/// browser and tells this model which page is in view. Links within the service's site (and the sites it signs in
/// on) stay here; a link anywhere else opens in the everyday browser (choice 22).
/// </summary>
public sealed partial class OnlineReaderViewModel(OnlineReaderRequest request, OnlineReaders readers, IReadOnlyList<string> signInSites)
    : PageViewModel
{
    readonly string? _site = OnlineReaders.SiteOf(request.Link);
    string? _savedNote;

    public OnlineReaderRequest Request { get; } = request;

    public override Route Route => Route.OnlineReader;

    public override string Title => Request.Title;

    public override string Section => "Library";

    public override Route? SectionRoute => Route.Library;

    public override Route NavRoute => Route.Library;

    /// <summary>In a window of its own rather than the main window.</summary>
    public bool IsPoppedOut { get; init; }

    public bool IsInMainWindow => !IsPoppedOut;

    internal OnlineBrowsers Browsers => readers.Browsers;

    /// <summary>The page to open: where the reader was, or the book's page.</summary>
    public Uri Start => Uri.TryCreate(Request.At ?? Request.Link, UriKind.Absolute, out var start) ? start : new Uri("about:blank");

    /// <summary>"On D&amp;D Beyond", under the title.</summary>
    public string PlaceLine => Request.Place is { } place ? $"On {place}" : _site ?? "";

    /// <summary>The page in view, which Pop out, Open in your browser and binders use.</summary>
    [ObservableProperty]
    public partial string Address { get; set; } = request.At ?? request.Link;

    /// <summary>What the reader has to say: the browser couldn't start, a link went to the everyday browser.</summary>
    [ObservableProperty]
    public partial string? Notice { get; set; }

    [ObservableProperty]
    public partial bool CanGoBack { get; set; }

    [ObservableProperty]
    public partial bool CanGoForward { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavoriteAction))]
    public partial bool IsFavorite { get; private set; }

    public string FavoriteAction => IsFavorite ? "Remove from favorites" : "Add to favorites";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinAction))]
    public partial bool IsPinned { get; private set; }

    public string PinAction => IsPinned ? "Unpin from the sidebar" : "Pin to the sidebar";

    /// <summary>The book's own note, beside the page while <see cref="ShowNote"/> is on.</summary>
    [ObservableProperty]
    public partial string NoteText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoteLabel))]
    public partial bool ShowNote { get; set; }

    public string NoteLabel => ShowNote ? "Hide note" : "Note";

    public override async Task LoadAsync()
    {
        readers.Pins.Changed -= OnPinsChanged;
        readers.Pins.Changed += OnPinsChanged;
        ShowPinned();
        try
        {
            IsFavorite = await Task.Run(() => readers.Favorites.IsFavoriteAsync(Request.EntryId));
            _savedNote = await Task.Run(() => readers.Notes.GetEntryNoteAsync(Request.EntryId));
            NoteText = _savedNote ?? "";
            if (NoteText.Length > 0) ShowNote = true;
        }
        catch (Exception ex)
        {
            readers.Log.LogError(ex, "Loading the online reader's book {EntryId} failed", Request.EntryId.Value);
        }
    }

    public override void Unload()
    {
        readers.Pins.Changed -= OnPinsChanged;
        _ = SaveNoteAsync();
    }

    void OnPinsChanged(object? sender, EventArgs e) => ShowPinned();

    void ShowPinned() => IsPinned = readers.Pins.IsPinned(PinTarget.Entry(Request.EntryId));

    [RelayCommand]
    async Task ToggleFavorite()
    {
        var favorite = !IsFavorite;
        IsFavorite = favorite;
        try
        {
            await Task.Run(() => readers.Favorites.SetAsync([Request.EntryId], favorite));
        }
        catch (Exception ex)
        {
            readers.Log.LogError(ex, "Marking entry {EntryId} as a favorite failed", Request.EntryId.Value);
            IsFavorite = !favorite;
            Notice = "The heart couldn't be saved.";
        }
    }

    [RelayCommand]
    Task TogglePin() => readers.Pins.ToggleAsync(PinTarget.Entry(Request.EntryId));

    [RelayCommand]
    void ToggleNote() => ShowNote = !ShowNote;

    /// <summary>Keeps the note once it changes, as the details do, when the note box loses focus or the reader closes.</summary>
    [RelayCommand]
    public async Task SaveNoteAsync()
    {
        var text = string.IsNullOrWhiteSpace(NoteText) ? null : NoteText.Trim();
        if (text == _savedNote) return;
        try
        {
            await Task.Run(() => readers.Notes.SetEntryNoteAsync(Request.EntryId, text));
            _savedNote = text;
        }
        catch (Exception ex)
        {
            readers.Log.LogError(ex, "Saving the note of entry {EntryId} failed", Request.EntryId.Value);
            Notice = "The note couldn't be saved.";
        }
    }

    [RelayCommand]
    void OpenInBrowser() => Notice = readers.OpenInBrowser(Address);

    [RelayCommand]
    Task PopOut() => readers.PopOutAsync(this);

    [RelayCommand]
    void ReturnToMainWindow() => readers.ReturnToMainWindow(this);

    /// <summary>This book at the page in view, for a reader in another window.</summary>
    public OnlineReaderRequest Here() => Request with { At = Address };

    /// <summary>
    /// Whether the reader follows a link: one on the book's site (or a site its service signs in on) stays; the
    /// smoke test's made-up page is a file.
    /// </summary>
    public bool Follows(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is "about" or "data" or "blob") return true;
        if (uri.IsFile) return Start.IsFile;
        if (OnlineReaders.SiteOf(address) is not { } site || _site is null) return false;
        return OnSite(site, _site) || signInSites.Any(s => OnSite(site, s));
    }

    static bool OnSite(string host, string site) => host == site || host.EndsWith("." + site, StringComparison.Ordinal);

    /// <summary>A link the reader doesn't follow: it opens in the everyday browser, and the reader says so.</summary>
    public void OpenOutside(string address)
    {
        Notice = readers.OpenInBrowser(address) ?? $"{OnlineReaders.SiteOf(address) ?? "That link"} opened in your browser.";
    }
}
