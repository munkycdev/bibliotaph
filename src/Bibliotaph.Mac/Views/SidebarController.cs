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
/// and under them what indexing is doing. Library folders are in Settings › Library and the Library's folder filter,
/// not here.
/// </summary>
public sealed class SidebarController(LibraryActivity activity, INavigationService navigation, ShellViewModel shell) : NSViewController
{
    /// <summary>
    /// A page (<paramref name="Route"/>), a pin (<paramref name="Pin"/>), or a heading.
    /// </summary>
    sealed record Row(bool IsHeader, string Title, string Symbol, Route Route = Route.Library, PinnedItemViewModel? Pin = null);

    readonly Bindings _bindings = new();
    Bindings _page = new();
    LibraryViewModel? _library;
    List<Row> _rows = [];
    NSTableView _table = null!;
    NSTextField _status = null!;
    Source? _source;
    bool _selecting;
    PageViewModel? _shownPage;
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
        _status = status;
        // An update downloaded in the background: it waits for this click, as a game may be under way.
        var update = NSButton.CreateButton("Restart to Update", () => shell.RestartToUpdateCommand.Execute(null));
        update.ControlSize = NSControlSize.Small;
        update.TintProminence = NSTintProminence.Primary;
        update.ToolTip = "Quit Bibliotaph, update it and open it again";
        var ready = NSTextField.CreateLabel("");
        ready.Font = NSFont.SystemFontOfSize(11)!;
        ready.TextColor = NSColor.SecondaryLabel;
        var updateLine = NSStackView.FromViews([update, ready]);
        updateLine.Spacing = 6;
        var foot = NSStackView.FromViews([updateLine, status]);
        foot.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        foot.Alignment = NSLayoutAttribute.Leading;
        foot.Spacing = 8;
        _bindings.Properties(shell.Updates, [nameof(IUpdateService.IsReady), nameof(IUpdateService.ReadyText)], () =>
        {
            updateLine.Hidden = !shell.Updates.IsReady;
            ready.StringValue = shell.Updates.ReadyText;
        });

        var root = new NSView();
        foreach (var view in new NSView[] { scroll, foot })
        {
            view.TranslatesAutoresizingMaskIntoConstraints = false;
            root.AddSubview(view);
        }
        NSLayoutConstraint.ActivateConstraints(
        [
            scroll.TopAnchor.ConstraintEqualTo(root.TopAnchor),
            scroll.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            scroll.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            scroll.BottomAnchor.ConstraintEqualTo(foot.TopAnchor, -8),
            foot.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor, 16),
            foot.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor, -12),
            foot.BottomAnchor.ConstraintEqualTo(root.BottomAnchor, -14),
            status.WidthAnchor.ConstraintEqualTo(foot.WidthAnchor),
        ]);
        View = root;

        _bindings.Property(activity, nameof(LibraryActivity.Summary), () => status.StringValue = activity.StatusName);
        _bindings.Property(activity, nameof(LibraryActivity.OfflineText), () => status.StringValue = activity.StatusName);
        // The web library out of reach is said there too, as Windows says it under its status.
        _bindings.Property(activity, nameof(LibraryActivity.WebText), () => status.StringValue = activity.StatusName);
        _bindings.Property(activity, nameof(LibraryActivity.ReviewCount), () =>
        {
            _reviewCount = activity.ReviewCount > 0 ? activity.ReviewCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) : "";
            var row = _rows.FindIndex(r => r.Route == Route.NeedsReview);
            if (row >= 0) _table.ReloadData(NSIndexSet.FromIndex(row), NSIndexSet.FromIndex(0));
        });
        _bindings.Collection(shell.Pinned, _ => ShowRows());
        ShowRows();
    }

    /// <summary>
    /// What the tour points at in the sidebar: the status line, which opens Settings, and the rows for the Library and
    /// the pages beside it.
    /// </summary>
    public NSView? Anchor(Core.TourAnchor anchor)
    {
        if (anchor is Core.TourAnchor.Status or Core.TourAnchor.Settings) return _status;
        Route? route = anchor switch
        {
            Core.TourAnchor.Library => Route.Library,
            Core.TourAnchor.NeedsReview => Route.NeedsReview,
            Core.TourAnchor.Collections => Route.Collections,
            Core.TourAnchor.Binders => Route.Sessions,
            _ => null,
        };
        if (route is null) return null;
        var row = _rows.FindIndex(r => !r.IsHeader && r.Pin is null && r.Route == route);
        return row < 0 ? null : _table.GetRowView(row, false);
    }

    /// <summary>The status line was clicked.</summary>
    public event EventHandler? StatusClicked;

    /// <summary>A line that can be clicked, with the pointing hand over it as a link has.</summary>
    sealed class StatusLabel : NSTextField
    {
        public override void ResetCursorRects() => AddCursorRect(Bounds, NSCursor.PointingHandCursor);
    }

    /// <summary>The page showing, which the sidebar marks.</summary>
    public void Show(PageViewModel? page)
    {
        _page.Dispose();
        _page = new Bindings();
        _shownPage = page;
        _library = page as LibraryViewModel;
        if (page is not null) _page.Property(page, nameof(PageViewModel.PinKey), SelectCurrent);
        SelectCurrent();
    }

    void ShowRows()
    {
        _rows =
        [
            new Row(false, "Home", "house", Route.Home),
            new Row(false, "All books", "books.vertical"),
            new Row(false, "Collections", "square.stack", Route.Collections),
            new Row(false, "Binders", "book.closed", Route.Sessions),
            new Row(false, "Needs review", "tray", Route.NeedsReview),
            new Row(false, "Summary", "chart.bar.xaxis", Route.Summary),
            .. shell.Pinned.Count == 0 ? (IEnumerable<Row>)[] :
                [new Row(true, "Pinned", ""), .. shell.Pinned.Select(p => new Row(false, p.Label, Symbol(p.Icon), Pin: p))],
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
        var route = _shownPage?.NavRoute ?? Route.Library;
        var index = _rows.FindIndex(r => r.Route == route && !r.IsHeader && r.Pin is null);
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
            navigation.NavigateTo(row.Route);
            return;
        }
        // All books, clicked on the Library: every book, whatever folder it was narrowed to.
        library.Filters.FolderChoice = library.Filters.FolderChoices[0];
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
