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
        _sidebar = new SidebarController(_shell.Activity, folders, services.GetRequiredService<SourceRootStore>(), services.GetRequiredService<WebShelfCache>(),
            services.GetRequiredService<INavigationService>(), _shell);
        // A folder just added is a new choice in the Library's folder filter, which it lists as it loads.
        folders.Added += (_, _) => Bindings.OnMain(() =>
        {
            if (_shown is LibraryViewModel library) _ = LoadAsync(library);
        });
        _pages = new PageHost(services.GetRequiredService<MacCovers>(), _navigation);

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
        _toolbar = new ToolbarDelegate(_shell, folders);
        window.Toolbar = new NSToolbar("main") { Delegate = _toolbar, DisplayMode = NSToolbarDisplayMode.Icon };
        window.SetContentSize(new CGSize(1320, 840));
        window.Center();
        window.ReleaseWhenClosed(false);
        // Tab goes through the controls as they are now, as pages and panes change them.
        window.AutorecalculatesKeyViewLoop = true;
        window.FrameAutosaveName = "BibliotaphMain";
        // The Windows app's page colour, under the pages; the sidebar and toolbar keep their glass.
        window.BackgroundColor = Palette.Background;

        services.GetRequiredService<MacReaderWindows>().MainWindow = window;
        _web = services.GetRequiredService<WebLibrary>();
        _roots = services.GetRequiredService<SourceRootStore>();
        _sidebar.StatusClicked += (_, _) => _settings.Open(Presentation.Services.SettingsSection.Processing);
        _navigation.Navigated += OnNavigated;
        _bindings.Add(() => _navigation.Navigated -= OnNavigated);
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

    public void ToggleAsk() => _shell.PlainSearch.ToggleCommand.Execute(null);

    /// <summary>The Library shown, for the View menu's layout and sort.</summary>
    public LibraryViewModel? Library => _shown as LibraryViewModel;

    public void AddFolder() => _toolbar.AddFolder();

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
        Window!.Title = page?.Title ?? "Bibliotaph";
        if (page is not null) _ = LoadAsync(page);
    });

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
/// The toolbar: the sidebar's toggle and Add folder at the leading edge; Ask, search, which searches the whole library as
/// typing pauses, and the inspector's toggle at the trailing edge.
/// </summary>
sealed class ToolbarDelegate(ShellViewModel shell, FolderAdding folders) : NSToolbarDelegate
{
    const string Search = "search";
    const string Add = "add";
    const string Ask = "ask";
    NSSearchToolbarItem? _search;
    AskPopover? _askPopover;

    static string[] Items =>
    [
        NSToolbar.NSToolbarToggleSidebarItemIdentifier,
        NSToolbar.NSToolbarSidebarTrackingSeparatorItemIdentifier,
        Add,
        NSToolbar.NSToolbarFlexibleSpaceItemIdentifier,
        Ask,
        Search,
        // The inspector's own toggle sits over it, at the trailing edge, as the Mac's apps put it.
        NSToolbar.NSToolbarInspectorTrackingSeparatorItemIdentifier,
        NSToolbar.NSToolbarToggleInspectorItemIdentifier,
    ];

    public override string[] AllowedItemIdentifiers(NSToolbar toolbar) => Items;

    public override string[] DefaultItemIdentifiers(NSToolbar toolbar) => Items;

    public override NSToolbarItem WillInsertItem(NSToolbar toolbar, string itemIdentifier, bool willBeInserted)
    {
        if (itemIdentifier == Add)
        {
            var add = new NSToolbarItem(Add)
            {
                Label = "Add folder",
                ToolTip = "Add a folder of books to your library",
                Image = NSImage.GetSystemSymbol("folder.badge.plus", "Add folder")!,
                Bordered = true,
            };
            add.Activated += (_, _) => AddFolder();
            return add;
        }
        if (itemIdentifier == Ask)
        {
            var ask = new NSToolbarItem(Ask)
            {
                Label = "Ask",
                ToolTip = "Ask in plain English: Return reads it as a search, Option-Return answers it from your books",
                Image = NSImage.GetSystemSymbol("sparkle.magnifyingglass", "Ask")!,
                Bordered = true,
            };
            ask.Activated += (_, _) => shell.PlainSearch.ToggleCommand.Execute(null);
            return ask;
        }
        if (itemIdentifier != Search) return null!;
        var item = new NSSearchToolbarItem(Search);
        _askPopover = new AskPopover(shell.PlainSearch, () => item.SearchField.StringValue);
        item.SearchField.PlaceholderString = shell.SearchPlaceholder;
        item.SearchField.Changed += (_, _) => shell.SearchText = item.SearchField.StringValue;
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
                else item.SearchField.Window?.MakeFirstResponder(item.SearchField);
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

    public void AddFolder() => folders.PickAsync().FireAndForget();
}
