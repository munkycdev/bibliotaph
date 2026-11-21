using AppKit;
using CoreGraphics;
using Foundation;

namespace Bibliotaph.MacSpike;

/// <summary>
/// One window: a split view with AppKit's own sidebar and inspector items, which macOS draws as floating glass, and a
/// unified toolbar whose items macOS groups into glass capsules. Nothing here draws glass itself.
/// </summary>
sealed class MainWindowController : NSWindowController
{
    readonly LibraryViewModel _library;
    readonly ToolbarDelegate _toolbar;

    public MainWindowController() : base(MakeWindow())
    {
        var web = new WebShelfClient(new HttpClient(), new KeychainDeviceKey());
        _library = new LibraryViewModel(web, url => NSWorkspace.SharedWorkspace.OpenUrl(new NSUrl(url.AbsoluteUri)));
        var covers = new Covers(web);

        var split = new NSSplitViewController();
        var sidebar = NSSplitViewItem.CreateSidebar(new SidebarController(_library));
        sidebar.MinimumThickness = 200;
        sidebar.MaximumThickness = 320;
        var content = NSSplitViewItem.FromViewController(new LibraryController(_library, covers));
        var inspector = NSSplitViewItem.CreateInspector(new InspectorController(_library, covers));
        inspector.MinimumThickness = 260;
        inspector.MaximumThickness = 360;
        split.AddSplitViewItem(sidebar);
        split.AddSplitViewItem(content);
        split.AddSplitViewItem(inspector);

        var window = Window!;
        window.ContentViewController = split;
        _toolbar = new ToolbarDelegate(_library);
        window.Toolbar = new NSToolbar("library") { Delegate = _toolbar, DisplayMode = NSToolbarDisplayMode.Icon };
        window.SetContentSize(new CGSize(1320, 820));
        window.Center();
        window.ReleaseWhenClosed(false);
        window.FrameAutosaveName = "BibliotaphLibrary";

        Ui.Watch(_library, nameof(LibraryViewModel.Subtitle), () => window.Subtitle = _library.Subtitle);
        _ = _library.StartAsync();
    }

    public void FocusSearch() => _toolbar.FocusSearch();

    public void Reload() => _ = _library.LoadAsync();

    public void Disconnect() => _ = _library.DisconnectAsync();

    static NSWindow MakeWindow() => new(new CGRect(0, 0, 1320, 820),
        NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Miniaturizable | NSWindowStyle.Resizable | NSWindowStyle.FullSizeContentView,
        NSBackingStore.Buffered, false)
    {
        Title = "Library",
        ToolbarStyle = NSWindowToolbarStyle.Unified,
    };
}

sealed class ToolbarDelegate(LibraryViewModel library) : NSToolbarDelegate
{
    const string Search = "search";
    NSSearchToolbarItem? _search;

    static string[] Items =>
    [
        NSToolbar.NSToolbarToggleSidebarItemIdentifier,
        NSToolbar.NSToolbarSidebarTrackingSeparatorItemIdentifier,
        NSToolbar.NSToolbarFlexibleSpaceItemIdentifier,
        Search,
        NSToolbar.NSToolbarInspectorTrackingSeparatorItemIdentifier,
        NSToolbar.NSToolbarFlexibleSpaceItemIdentifier,
        NSToolbar.NSToolbarToggleInspectorItemIdentifier,
    ];

    public override string[] AllowedItemIdentifiers(NSToolbar toolbar) => Items;

    public override string[] DefaultItemIdentifiers(NSToolbar toolbar) => Items;

    public override NSToolbarItem WillInsertItem(NSToolbar toolbar, string itemIdentifier, bool willBeInserted)
    {
        if (itemIdentifier != Search) return null!;
        var item = new NSSearchToolbarItem(Search);
        item.SearchField.PlaceholderString = "Search books";
        item.SearchField.Changed += (_, _) => library.SearchText = item.SearchField.StringValue;
        _search = item;
        return item;
    }

    public void FocusSearch() => _search?.BeginSearchInteraction();
}
