using AppKit;
using Bibliotaph.Catalog;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Mac.Platform;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Presentation.ViewModels;
using Bibliotaph.Processing;
using CoreGraphics;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// The main window: AppKit's own sidebar beside the page the shell navigated to, under a unified toolbar. The page
/// views follow navigation as the Windows window's content does, loading the new page and unloading the old one.
/// </summary>
public sealed class MainWindowController : NSWindowController
{
    readonly INavigationService _navigation;
    readonly ShellViewModel _shell;
    readonly SidebarController _sidebar;
    readonly PageHost _pages;
    readonly InspectorController _inspector;
    readonly ToolbarDelegate _toolbar;
    readonly Bindings _bindings = new();
    Bindings _reader = new();
    readonly IServiceProvider _services;
    readonly SettingsWindow _settings;
    readonly Presentation.Services.SettingsLinks _links;
    readonly List<OnlineBrowserWindow> _browsers = [];
    readonly WebLibrary _web;
    readonly SourceRootStore _roots;
    readonly TourPopover _tour;
    Bindings _title = new();
    PageViewModel? _shown;

    public MainWindowController(IServiceProvider services) : base(MakeWindow())
    {
        _services = services;
        _navigation = services.GetRequiredService<INavigationService>();
        _shell = services.GetRequiredService<ShellViewModel>();
        var folders = new FolderAdding(services);
        _settings = new SettingsWindow(services, folders);
        _links = services.GetRequiredService<Presentation.Services.SettingsLinks>();
        var online = services.GetRequiredService<Platform.MacOnlineLibraries>();
        online.SignOut = OnlineBrowserWindow.SignOutAsync;
        var browserLog = Microsoft.Extensions.Logging.LoggerFactoryExtensions.CreateLogger<OnlineBrowserWindow>(services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>());
        online.BrowserRequested += (_, request) =>
        {
            // Kept until it closes, as nothing else holds the window.
            var browser = new OnlineBrowserWindow(request, online.Downloads, browserLog);
            _browsers.Add(browser);
            browser.Window!.WillClose += (_, _) => _browsers.Remove(browser);
            browser.Show();
        };
        _sidebar = new SidebarController(_shell.Activity, services.GetRequiredService<INavigationService>(), _shell);
        // A folder just added is a new choice in the Library's folder filter, which it lists as it loads.
        folders.Added += (_, _) => Bindings.OnMain(() =>
        {
            if (_shown is LibraryViewModel library) _ = LoadAsync(library);
        });
        _pages = new PageHost(services.GetRequiredService<MacCovers>(), _navigation, services);

        var split = new NSSplitViewController();
        var sidebar = NSSplitViewItem.CreateSidebar(_sidebar);
        sidebar.MinimumThickness = 210;
        sidebar.MaximumThickness = 340;
        split.AddSplitViewItem(sidebar);
        split.AddSplitViewItem(NSSplitViewItem.FromViewController(_pages));
        _inspector = new InspectorController(services.GetRequiredService<MacCovers>());
        var details = NSSplitViewItem.CreateInspector(_inspector);
        details.MinimumThickness = 280;
        details.MaximumThickness = 420;
        details.Collapsed = true;
        split.AddSplitViewItem(details);
        _inspector.Showing += (_, showing) => ((NSSplitViewItem)details.Animator).Collapsed = !showing;

        var window = Window!;
        window.ContentViewController = split;
        _toolbar = new ToolbarDelegate(_shell, folders, services.GetRequiredService<SearchGuideViewModel>());
        window.Toolbar = new NSToolbar("main") { Delegate = _toolbar, DisplayMode = NSToolbarDisplayMode.Icon };
        window.SetContentSize(new CGSize(1320, 840));
        // The sidebar, one column of covers and the reader's bar; the pages' own rows wrap to fit anything wider.
        window.ContentMinSize = new CGSize(720, 480);
        window.Center();
        window.ReleaseWhenClosed(false);
        // Tab goes through the controls as they are now, as pages and panes change them.
        window.AutorecalculatesKeyViewLoop = true;
        // Not the first name it had: frames saved under that one were stretched by a Library that couldn't narrow.
        window.FrameAutosaveName = "BibliotaphMainWindow";
        // The Windows app's page colour, under the pages; the sidebar and toolbar keep their glass.
        window.BackgroundColor = Palette.Background;

        services.GetRequiredService<MacReaderWindows>().MainWindow = window;
        _web = services.GetRequiredService<WebLibrary>();
        _roots = services.GetRequiredService<SourceRootStore>();
        _sidebar.StatusClicked += (_, _) => _settings.Open(Presentation.Services.SettingsSection.Processing);
        _tour = new TourPopover(services.GetRequiredService<TourViewModel>(), anchor => anchor == Core.TourAnchor.Search
            ? _toolbar.SearchField
            : _sidebar.Anchor(anchor) ?? _pages.Anchor(anchor));
        _bindings.Add(_tour.Dispose);
        _navigation.Navigated += OnNavigated;
        _bindings.Add(() => _navigation.Navigated -= OnNavigated);
        // The mouse's back button and a swipe to the right go back, as the mouse's back button does on Windows and a
        // swipe does in Finder and Safari.
        var back = NSEvent.AddLocalMonitorForEventsMatchingMask(NSEventMask.OtherMouseDown | NSEventMask.EventSwipe, theEvent =>
        {
            if (theEvent.Window != Window) return theEvent;
            var goBack = theEvent.Type == NSEventType.OtherMouseDown ? theEvent.ButtonNumber == 3 : theEvent.DeltaX > 0;
            if (!goBack || !_shell.CanGoBack) return theEvent;
            GoBack();
            return null!;
        });
        _bindings.Add(() => { if (back is not null) NSEvent.RemoveMonitor(back); });
    }

    /// <summary>
    /// While a book is open, its details wait in the inspector, folded, for the inspector's toggle to show them beside
    /// the pages, as the Library shows a book's. They follow the reader to the next book it opens.
    /// </summary>
    void FollowReader(ViewerViewModel? reader)
    {
        _reader.Dispose();
        _reader = new Bindings();
        if (reader is null) return;
        long? shown = null;
        _reader.Properties(reader, [nameof(ViewerViewModel.Pdf), nameof(ViewerViewModel.Ebook), nameof(ViewerViewModel.Image), nameof(ViewerViewModel.Video)], () =>
        {
            if (reader.Here()?.DocumentId is not { } id || id == shown) return;
            shown = id;
            LoadDetailsAsync(reader, id).FireAndForget();
        });
    }

    async Task LoadDetailsAsync(ViewerViewModel reader, long documentId)
    {
        var cards = await _services.GetRequiredService<Bibliotaph.Index.LibraryQueries>().GetDocumentCardsAsync([documentId]);
        if (!cards.TryGetValue(documentId, out var entry)) return;
        var details = await _services.GetRequiredService<InspectorFactory>().LoadAsync(new LibraryItemViewModel(entry));
        Bindings.OnMain(() =>
        {
            if (ReferenceEquals(_shown, reader)) _inspector.Hold(details);
        });
    }

    public void FocusSearch() => _toolbar.FocusSearch();

    /// <summary>The toolbar's search field.</summary>
    public NSSearchField? SearchBox => _toolbar.SearchField as NSSearchField;

    public void ToggleAsk() => _shell.PlainSearch.ToggleCommand.Execute(null);

    /// <summary>The Library shown, for the View menu's layout and sort.</summary>
    public LibraryViewModel? Library => _shown as LibraryViewModel;

    public void AddFolder() => _toolbar.AddFolder();

    /// <summary>Add a Book I Own Elsewhere, on the Library, which shows first if another page is.</summary>
    public async Task AddElsewhereAsync()
    {
        if (Library is null) _navigation.NavigateTo(Presentation.Services.Route.Library);
        // The Library loads as it shows; its command waits for it.
        for (var tries = 0; Library is null && tries < 20; tries++) await Task.Delay(50);
        if (Library is { } library && library.AddElsewhereCommand.CanExecute(null)) library.AddElsewhereCommand.Execute(null);
    }

    /// <summary>Find with no book in reach searches the library, as the search field does.</summary>
    [Foundation.Export("readerFind:")]
    void ReaderFind(Foundation.NSObject sender) => FocusSearch();

    /// <summary>Settings > Library with the picker for a folder in connected storage, as Windows' Add menu has it.</summary>
    public void AddFromStorage() => _services.GetRequiredService<LibraryFolders>().RequestStoragePick();

    /// <summary>Light, dark or as macOS, for the View menu.</summary>
    public MacTheme Theme => _services.GetRequiredService<MacTheme>();

    /// <summary>An update has downloaded and waits for a restart.</summary>
    public bool UpdateReady => _shell.Updates.IsReady;

    public void RestartToUpdate() => _shell.RestartToUpdateCommand.Execute(null);

    /// <summary>Back to the page before, from the Go menu.</summary>
    public void GoBack()
    {
        if (_shell.GoBackCommand.CanExecute(null)) _shell.GoBackCommand.Execute(null);
    }

    /// <summary>A page the menus open, as Check a Download.</summary>
    public void Go(Presentation.Services.Route route) => _navigation.NavigateTo(route);

    /// <summary>Connecting this Mac to its web library, in Settings.</summary>
    public void ShowDevices() => _settings.Open(Presentation.Services.SettingsSection.Devices);

    public void ShowSettings(Presentation.Services.SettingsSection? section = null) => _settings.Open(section);

    /// <summary>For --screenshot, which pictures the window it was asked to open.</summary>
    public NSWindow? SettingsWindow => _settings.Window;


    /// <summary>
    /// Whether this Mac is connected to a web library, which the sidebar offers to do while it isn't. A first start with
    /// no folders opens Your Web Library, as connecting is how most people's books come to a new device.
    /// </summary>
    public async Task CheckConnectionAsync(bool offer)
    {
        var status = await _web.CheckAsync();
        var connected = status.State != WebLibraryState.NotConnected;
        if (offer && !connected && (await _roots.ListAsync()).Count == 0) Bindings.OnMain(ShowDevices);
    }

    /// <summary>The page shown is told it's gone, so it can save what it has before the host stops.</summary>
    public void Unload()
    {
        Bindings.OnMain(() =>
        {
            _shown?.Unload();
            _shown = null;
        });
        _bindings.Dispose();
    }

    void OnNavigated(object? sender, EventArgs e) => Bindings.OnMain(() =>
    {
        var page = _navigation.Current;
        if (ReferenceEquals(page, _shown)) return;
        // Settings is a window on the Mac: a link to it from a page opens the window, and the page stays.
        if (page is SettingsViewModel && _navigation.CanGoBack)
        {
            _navigation.GoBack();
            // At the section the link asked for.
            var section = _links.TakeRequest();
            _settings.Open(section);
            return;
        }
        // A link in Settings to a page (Files needing attention, what the AI is reading) shows that page in this
        // window, and Settings, done with, closes behind it.
        if (_settings.Window is { IsKeyWindow: true } settings)
        {
            settings.Close();
            Window!.MakeKeyAndOrderFront(null);
        }
        _shown?.Unload();
        _shown = page;
        _sidebar.Show(page);
        _inspector.Follow(page as LibraryViewModel);
        FollowReader(page as ViewerViewModel);
        _pages.Show(page);
        ShowTitle(page);
        if (page is not null) _ = LoadAsync(page);
    });

    /// <summary>
    /// The page's name in the toolbar, as the Mac's apps show it, with what it holds under it, "14 documents"; the page
    /// itself starts with what can be done there.
    /// </summary>
    void ShowTitle(PageViewModel? page)
    {
        _title.Dispose();
        _title = new Bindings();
        var window = Window!;
        void Show(string title, string subtitle)
        {
            window.Title = title.Length > 0 ? title : "Bibliotaph";
            window.Subtitle = subtitle;
        }
        if (_pages.Current is IWindowTitle titled) titled.FollowTitle(_title, Show);
        else if (page is not null) _title.Property(page, nameof(PageViewModel.Title), () => Show(page.Title, ""));
        else Show("Bibliotaph", "");
    }

    static async Task LoadAsync(PageViewModel page)
    {
        try
        {
            await page.LoadAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Loading the {Route} page failed", page.Route);
        }
    }

    static NSWindow MakeWindow() => new(new CGRect(0, 0, 1320, 840),
        NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Miniaturizable | NSWindowStyle.Resizable | NSWindowStyle.FullSizeContentView,
        NSBackingStore.Buffered, false)
    {
        Title = "Bibliotaph",
        ToolbarStyle = NSWindowToolbarStyle.Unified,
    };
}

/// <summary>
/// The toolbar: the sidebar's toggle and Back at the leading edge; search, which searches the whole library as typing pauses, and
/// the inspector's toggle at the trailing edge. Adding a folder is in the File menu and Settings › Library, and asking
/// in plain English in the Edit menu (⌥⌘F).
/// </summary>
sealed class ToolbarDelegate(ShellViewModel shell, FolderAdding folders, SearchGuideViewModel guide) : NSToolbarDelegate
{
    SearchGuidePanel? _guide;
    const string Search = "search";
    const string Back = "back";
    NSSearchToolbarItem? _search;
    AskPopover? _askPopover;

    static string[] Items =>
    [
        NSToolbar.NSToolbarToggleSidebarItemIdentifier,
        NSToolbar.NSToolbarSidebarTrackingSeparatorItemIdentifier,
        Back,
        NSToolbar.NSToolbarFlexibleSpaceItemIdentifier,
        Search,
        // The inspector's own toggle sits over it, at the trailing edge, as the Mac's apps put it.
        NSToolbar.NSToolbarInspectorTrackingSeparatorItemIdentifier,
        NSToolbar.NSToolbarToggleInspectorItemIdentifier,
    ];

    public override string[] AllowedItemIdentifiers(NSToolbar toolbar) => Items;

    public override string[] DefaultItemIdentifiers(NSToolbar toolbar) => Items;

    public override NSToolbarItem WillInsertItem(NSToolbar toolbar, string itemIdentifier, bool willBeInserted)
    {
        if (itemIdentifier == Back)
        {
            // Back through the pages shown, as Windows' title bar has it, beside the page's name as Finder's is.
            var back = new NSToolbarItem(Back)
            {
                Label = "Back",
                ToolTip = "Back to the page before (⌘[)",
                Image = NSImage.GetSystemSymbol("chevron.backward", "Back")!,
                Navigational = true,
                Bordered = true,
                Autovalidates = false,
            };
            back.Activated += (_, _) => shell.GoBackCommand.Execute(null);
            void Enable() => back.Enabled = shell.CanGoBack;
            shell.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ShellViewModel.CanGoBack)) Bindings.OnMain(Enable);
            };
            Enable();
            return back;
        }
        if (itemIdentifier != Search) return null!;
        var item = new NSSearchToolbarItem(Search);
        // A field that says when it takes the keyboard, for the search guide, which Windows shows under its box.
        var field = new GuideSearchField();
        item.SearchField = field;
        _askPopover = new AskPopover(shell.PlainSearch, () => item.SearchField.StringValue);
        item.SearchField.PlaceholderString = shell.SearchPlaceholder;
        void Typed(string text) => shell.SearchText = text;
        _guide = new SearchGuidePanel(guide, field, () => shell.PlainSearch.IsOn, Typed);
        field.Delegate = new GuideFieldDelegate(Typed, () => _guide);
        field.Focused += (_, _) => _guide.Focused();
        item.SearchField.Activated += (_, _) =>
        {
            if (!shell.PlainSearch.IsOn)
            {
                shell.SubmitSearchCommand.Execute(null);
                return;
            }
            // In Ask mode Return reads the request as a search; Option-Return answers it from the library's pages.
            var answer = NSApplication.SharedApplication.CurrentEvent?.ModifierFlags.HasFlag(NSEventModifierMask.AlternateKeyMask) == true;
            var text = item.SearchField.StringValue;
            (answer ? shell.PlainSearch.AnswerAsync(text) : shell.PlainSearch.InterpretAsync(text)).FireAndForget();
            _askPopover.ShowUnder(item.SearchField);
        };
        shell.PlainSearch.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(PlainSearchViewModel.IsOn)) return;
            Bindings.OnMain(() =>
            {
                item.SearchField.PlaceholderString = shell.SearchPlaceholder;
                if (!shell.PlainSearch.IsOn) _askPopover.Close();
                else
                {
                    _guide.Close();
                    item.SearchField.Window?.MakeFirstResponder(item.SearchField);
                }
            });
        };
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.SearchText) && item.SearchField.StringValue != shell.SearchText)
                Bindings.OnMain(() => item.SearchField.StringValue = shell.SearchText);
        };
        _search = item;
        return item;
    }

    public void FocusSearch() => _search?.BeginSearchInteraction();

    /// <summary>The search field, for the tour to point at.</summary>
    public NSView? SearchField => _search?.SearchField;

    public void AddFolder() => folders.PickAsync().FireAndForget();
}
