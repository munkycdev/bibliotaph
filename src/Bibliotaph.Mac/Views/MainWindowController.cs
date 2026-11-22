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
    readonly ToolbarDelegate _toolbar;
    readonly Bindings _bindings = new();
    readonly DevicesWindow _devices;
    PageViewModel? _shown;

    public MainWindowController(IServiceProvider services) : base(MakeWindow())
    {
        _navigation = services.GetRequiredService<INavigationService>();
        _shell = services.GetRequiredService<ShellViewModel>();
        var folders = new FolderAdding(services);
        _devices = new DevicesWindow(services);
        _sidebar = new SidebarController(_shell.Activity, folders, services.GetRequiredService<SourceRootStore>(), services.GetRequiredService<WebShelfCache>());
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

        var window = Window!;
        window.ContentViewController = split;
        _toolbar = new ToolbarDelegate(_shell, folders);
        window.Toolbar = new NSToolbar("main") { Delegate = _toolbar, DisplayMode = NSToolbarDisplayMode.Icon };
        window.SetContentSize(new CGSize(1320, 840));
        window.Center();
        window.ReleaseWhenClosed(false);
        window.FrameAutosaveName = "BibliotaphMain";

        services.GetRequiredService<MacReaderWindows>().MainWindow = window;
        _navigation.Navigated += OnNavigated;
        _bindings.Add(() => _navigation.Navigated -= OnNavigated);
    }

    public void FocusSearch() => _toolbar.FocusSearch();

    public void AddFolder() => _toolbar.AddFolder();

    public void ShowDevices() => _devices.Open();

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
        _shown?.Unload();
        _shown = page;
        _sidebar.Show(page as LibraryViewModel);
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

/// <summary>The toolbar: the sidebar's toggle, Add folder, and search, which searches the whole library as typing pauses.</summary>
sealed class ToolbarDelegate(ShellViewModel shell, FolderAdding folders) : NSToolbarDelegate
{
    const string Search = "search";
    const string Add = "add";
    NSSearchToolbarItem? _search;

    static string[] Items =>
    [
        NSToolbar.NSToolbarToggleSidebarItemIdentifier,
        NSToolbar.NSToolbarSidebarTrackingSeparatorItemIdentifier,
        Add,
        NSToolbar.NSToolbarFlexibleSpaceItemIdentifier,
        Search,
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
        if (itemIdentifier != Search) return null!;
        var item = new NSSearchToolbarItem(Search);
        item.SearchField.PlaceholderString = shell.SearchPlaceholder;
        item.SearchField.Changed += (_, _) => shell.SearchText = item.SearchField.StringValue;
        item.SearchField.Activated += (_, _) => shell.SubmitSearchCommand.Execute(null);
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
