using AppKit;
using Bibliotaph.Catalog;
using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Presentation.ViewModels;
using Bibliotaph.Processing;
using Foundation;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// A source list: the pages the Mac has, what is pinned (books, Favorites, collections, binders and saved searches),
/// then each library folder, which narrows the Library to it as its folder filter does on Windows. Under it, what
/// indexing is doing.
/// </summary>
public sealed class SidebarController(LibraryActivity activity, FolderAdding adding, SourceRootStore roots, WebShelfCache shelf,
    INavigationService navigation, ShellViewModel shell) : NSViewController
{
    /// <summary>
    /// A page (<paramref name="Route"/>), a library folder (<paramref name="RootId"/>, in the Library), a pin
    /// (<paramref name="Pin"/>), or a heading.
    /// </summary>
    sealed record Row(bool IsHeader, string Title, string Symbol, long? RootId, Route Route = Route.Library, PinnedItemViewModel? Pin = null);

    readonly Bindings _bindings = new();
    Bindings _page = new();
    LibraryViewModel? _library;
    List<Row> _rows = [];
    IReadOnlyList<SourceRoot> _folders = [];
    NSTableView _table = null!;
    Source? _source;
    bool _selecting;
    PageViewModel? _shownPage;
    long? _pendingRoot;
    string _reviewCount = "";

    public override void LoadView()
    {
        _table = new PinMenus(this)
        {
            Style = NSTableViewStyle.SourceList,
            HeaderView = null!,
            FloatsGroupRows = false,
            RowSizeStyle = NSTableViewRowSizeStyle.Default,
        };
        _table.AddColumn(new NSTableColumn("main"));
        _source = new Source(this);
        _table.DataSource = _source;
        _table.Delegate = _source;
        var scroll = new NSScrollView { DocumentView = _table, HasVerticalScroller = true, DrawsBackground = false };

        // What indexing is doing; a click opens Settings › Processing, which shows it all.
        var status = new StatusLabel { Editable = false, Selectable = false, Bordered = false, DrawsBackground = false };
        status.Cell.Wraps = true;
        status.Font = NSFont.SystemFontOfSize(11)!;
        status.TextColor = NSColor.SecondaryLabel;
        status.MaximumNumberOfLines = 3;
        status.ToolTip = "Show what Bibliotaph is doing in Settings › Processing";
        status.AddGestureRecognizer(new NSClickGestureRecognizer(() => StatusClicked?.Invoke(this, EventArgs.Empty)));

        var root = new NSView();
        foreach (var view in new NSView[] { scroll, status })
        {
            view.TranslatesAutoresizingMaskIntoConstraints = false;
            root.AddSubview(view);
        }
        NSLayoutConstraint.ActivateConstraints(
        [
            scroll.TopAnchor.ConstraintEqualTo(root.TopAnchor),
            scroll.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            scroll.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            scroll.BottomAnchor.ConstraintEqualTo(status.TopAnchor, -8),
            status.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor, 16),
            status.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor, -12),
            status.BottomAnchor.ConstraintEqualTo(root.BottomAnchor, -14),
        ]);
        View = root;

        _bindings.Property(activity, nameof(LibraryActivity.Summary), () => status.StringValue = activity.StatusName);
        _bindings.Property(activity, nameof(LibraryActivity.OfflineText), () => status.StringValue = activity.StatusName);
        _bindings.Property(activity, nameof(LibraryActivity.ReviewCount), () =>
        {
            _reviewCount = activity.ReviewCount > 0 ? activity.ReviewCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) : "";
            var row = _rows.FindIndex(r => r.Route == Route.NeedsReview);
            if (row >= 0) _table.ReloadData(NSIndexSet.FromIndex(row), NSIndexSet.FromIndex(0));
        });
        EventHandler refreshed = (_, _) => Bindings.OnMain(() => ReloadAsync().FireAndForget());
        adding.Added += refreshed;
        _bindings.Add(() => adding.Added -= refreshed);
        _bindings.Collection(shell.Pinned, _ => ShowRows());
        ReloadAsync().FireAndForget();
    }

    /// <summary>The status line was clicked.</summary>
    public event EventHandler? StatusClicked;

    /// <summary>A line that can be clicked, with the pointing hand over it as a link has.</summary>
    sealed class StatusLabel : NSTextField
    {
        public override void ResetCursorRects() => AddCursorRect(Bounds, NSCursor.PointingHandCursor);
    }

    /// <summary>The page showing, which the sidebar marks; on the Library, the folder it narrows to.</summary>
    public void Show(PageViewModel? page)
    {
        _page.Dispose();
        _page = new Bindings();
        _shownPage = page;
        _library = page as LibraryViewModel;
        if (page is not null) _page.Property(page, nameof(PageViewModel.PinKey), SelectCurrent);
        if (_library is { } library)
            _page.Property(library.Filters, nameof(LibraryFilterState.FolderChoice), () =>
            {
                // A folder chosen on another page is applied once the Library has listed its folders.
                if (_pendingRoot is { } root && library.Filters.FolderChoices.FirstOrDefault(c => c.Value == new FolderKey(root, "")) is { } choice)
                {
                    _pendingRoot = null;
                    library.Filters.FolderChoice = choice;
                }
                SelectCurrent();
            });
        SelectCurrent();
    }

    async Task ReloadAsync()
    {
        _folders = await roots.ListAsync();
        ShowRows();
    }

    void ShowRows()
    {
        var folders = _folders;
        _rows =
        [
            new Row(false, "Home", "house", null, Route.Home),
            new Row(false, "All books", "books.vertical", null),
            new Row(false, "Collections", "square.stack", null, Route.Collections),
            new Row(false, "Binders", "book.closed", null, Route.Sessions),
            new Row(false, "Needs review", "tray", null, Route.NeedsReview),
            new Row(false, "Summary", "chart.bar.xaxis", null, Route.Summary),
            .. shell.Pinned.Count == 0 ? (IEnumerable<Row>)[] :
                [new Row(true, "Pinned", "", null), .. shell.Pinned.Select(p => new Row(false, p.Label, Symbol(p.Icon), null, Pin: p))],
            new Row(true, "Library folders", "", null),
            .. folders.Select(f => new Row(false, shelf.NameOf(f.Path), WebLibraryPaths.IsWebPath(f.Path) ? "globe" : "folder", f.Id)),
        ];
        _table.ReloadData();
        SelectCurrent();
    }

    /// <summary>The SF Symbol for a pin's icon, which is named for the Windows app's Lucide icon.</summary>
    static string Symbol(string icon) => icon switch
    {
        "Heart" => "heart",
        "Folders" => "square.stack",
        "NotebookTabs" => "book.closed",
        "SlidersHorizontal" => "line.3.horizontal.decrease.circle",
        _ => "book",
    };

    void SelectCurrent()
    {
        if (_table is null) return;
        // A page that is pinned is marked as its pin, as on Windows.
        if (_shownPage?.PinKey is { } key && _rows.FindIndex(r => r.Pin?.Key == key) is >= 0 and var pinned)
        {
            _selecting = true;
            _table.SelectRow(pinned, false);
            _selecting = false;
            return;
        }
        var root = _library?.Filters.FolderChoice.Value is { Folder.Length: 0 } folder ? folder.RootId : (long?)null;
        var route = _shownPage?.NavRoute ?? Route.Library;
        var index = route == Route.Library
            ? _rows.FindIndex(r => !r.IsHeader && r.Pin is null && r.Route == Route.Library && r.RootId == root)
            : _rows.FindIndex(r => r.Route == route && !r.IsHeader && r.Pin is null);
        _selecting = true;
        if (index >= 0) _table.SelectRow(index, false);
        else _table.DeselectAll(this);
        _selecting = false;
    }

    void Choose(Row row)
    {
        if (_selecting) return;
        if (row.Pin is { } pin)
        {
            shell.OpenPinCommand.Execute(pin);
            return;
        }
        if (row.Route != Route.Library || _library is not { } library)
        {
            _pendingRoot = row.RootId;
            navigation.NavigateTo(row.Route);
            return;
        }
        var filters = library.Filters;
        filters.FolderChoice = row.RootId is { } id
            ? filters.FolderChoices.FirstOrDefault(c => c.Value == new FolderKey(id, "")) ?? filters.FolderChoices[0]
            : filters.FolderChoices[0];
    }

    /// <summary>A pin's right-click menu, as on Windows: open it, unpin it, move it, and rename or delete a saved search.</summary>
    NSMenu? MenuFor(nint at)
    {
        if (at < 0 || at >= _rows.Count || _rows[(int)at].Pin is not { } pin) return null;
        var menu = new NSMenu();
        void Add(string title, System.Windows.Input.ICommand command)
        {
            if (command.CanExecute(pin)) menu.AddItem(new NSMenuItem(title, (_, _) => command.Execute(pin)));
        }
        Add("Open", shell.OpenPinCommand);
        Add("Unpin", shell.UnpinCommand);
        Add("Move up", shell.MovePinUpCommand);
        Add("Move down", shell.MovePinDownCommand);
        if (pin.IsSavedSearch)
        {
            menu.AddItem(NSMenuItem.SeparatorItem);
            Add("Rename…", shell.RenameViewCommand);
            Add("Delete", shell.DeleteViewCommand);
        }
        return menu;
    }

    sealed class PinMenus(SidebarController owner) : NSTableView
    {
        public override NSMenu? MenuForEvent(NSEvent theEvent) => owner.MenuFor(GetRow(ConvertPointFromView(theEvent.LocationInWindow, null)));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _page.Dispose();
            _bindings.Dispose();
        }
        base.Dispose(disposing);
    }

    sealed class Source(SidebarController owner) : NSTableViewDelegate, INSTableViewDataSource
    {
        [Export("numberOfRowsInTableView:")]
        public nint GetRowCount(NSTableView tableView) => owner._rows.Count;

        public override NSView GetViewForItem(NSTableView tableView, NSTableColumn tableColumn, nint row)
        {
            var item = owner._rows[(int)row];
            return item.IsHeader ? Header(item.Title) : Cell(item, owner);
        }

        public override bool IsGroupRow(NSTableView tableView, nint row) => owner._rows[(int)row].IsHeader;

        public override bool ShouldSelectRow(NSTableView tableView, nint row) => !owner._rows[(int)row].IsHeader;

        public override void SelectionDidChange(NSNotification notification)
        {
            var row = (int)owner._table.SelectedRow;
            if (row >= 0 && row < owner._rows.Count) owner.Choose(owner._rows[row]);
        }

        static NSTableCellView Header(string title)
        {
            var text = NSTextField.CreateLabel(title);
            var cell = new NSTableCellView { TextField = text };
            Pin(cell, text, null);
            return cell;
        }

        static NSTableCellView Cell(Row row, SidebarController owner)
        {
            var image = new NSImageView { Image = NSImage.GetSystemSymbol(row.Symbol, null)! };
            var text = NSTextField.CreateLabel(row.Title);
            text.LineBreakMode = NSLineBreakMode.TruncatingMiddle;
            var cell = new NSTableCellView { ImageView = image, TextField = text };
            Pin(cell, text, image);
            if (row.Pin is { } pin)
            {
                cell.ToolTip = pin.Tip;
                if (pin.IsDimmed) text.TextColor = NSColor.TertiaryLabel;
            }
            if (row.Route == Route.NeedsReview) Count(cell, owner._reviewCount);
            return cell;
        }

        /// <summary>How many suggestions and files wait in Needs review, beside its name.</summary>
        static void Count(NSView cell, string value)
        {
            var count = NSTextField.CreateLabel(value);
            count.TextColor = NSColor.SecondaryLabel;
            count.Font = NSFont.MonospacedDigitSystemFontOfSize(11, NSFontWeight.Regular)!;
            count.TranslatesAutoresizingMaskIntoConstraints = false;
            cell.AddSubview(count);
            NSLayoutConstraint.ActivateConstraints(
            [
                count.TrailingAnchor.ConstraintEqualTo(cell.TrailingAnchor, -6),
                count.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
            ]);
        }

        static void Pin(NSView cell, NSTextField text, NSImageView? image)
        {
            NSView[] views = image is null ? [text] : [image, text];
            foreach (var view in views)
            {
                view.TranslatesAutoresizingMaskIntoConstraints = false;
                cell.AddSubview(view);
            }
            var constraints = new List<NSLayoutConstraint>
            {
                text.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
                text.LeadingAnchor.ConstraintEqualTo(image?.TrailingAnchor ?? cell.LeadingAnchor, image is null ? 2 : 6),
                text.TrailingAnchor.ConstraintLessThanOrEqualTo(cell.TrailingAnchor, -6),
            };
            if (image is not null)
            {
                constraints.Add(image.LeadingAnchor.ConstraintEqualTo(cell.LeadingAnchor, 2));
                constraints.Add(image.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor));
                constraints.Add(image.WidthAnchor.ConstraintEqualTo(18));
            }
            NSLayoutConstraint.ActivateConstraints([.. constraints]);
        }
    }
}
