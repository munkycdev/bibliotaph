using AppKit;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Presentation.ViewModels;
using Bibliotaph.Processing;
using Foundation;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// A source list: the pages the Mac has, then each library folder, which narrows the Library to it as its folder
/// filter does on Windows. Under it, what indexing is doing.
/// </summary>
public sealed class SidebarController(LibraryActivity activity, FolderAdding adding, SourceRootStore roots, WebShelfCache shelf,
    INavigationService navigation) : NSViewController
{
    /// <summary>A page (<paramref name="Route"/>), a library folder (<paramref name="RootId"/>, in the Library), or a heading.</summary>
    sealed record Row(bool IsHeader, string Title, string Symbol, long? RootId, Route Route = Route.Library);

    readonly Bindings _bindings = new();
    Bindings _page = new();
    LibraryViewModel? _library;
    List<Row> _rows = [];
    NSTableView _table = null!;
    NSButton _connect = null!;
    Source? _source;
    bool _selecting;
    PageViewModel? _shownPage;
    long? _pendingRoot;
    string _reviewCount = "";

    public override void LoadView()
    {
        _table = new NSTableView
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

        var status = NSTextField.CreateWrappingLabel("");
        status.Font = NSFont.SystemFontOfSize(11)!;
        status.TextColor = NSColor.SecondaryLabel;
        status.MaximumNumberOfLines = 3;

        _connect = NSButton.CreateButton("Connect to your web library…", () => ConnectRequested?.Invoke(this, EventArgs.Empty));
        _connect.ControlSize = NSControlSize.Small;
        _connect.Hidden = true;

        var root = new NSView();
        foreach (var view in new NSView[] { scroll, _connect, status })
        {
            view.TranslatesAutoresizingMaskIntoConstraints = false;
            root.AddSubview(view);
        }
        NSLayoutConstraint.ActivateConstraints(
        [
            scroll.TopAnchor.ConstraintEqualTo(root.TopAnchor),
            scroll.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            scroll.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            scroll.BottomAnchor.ConstraintEqualTo(_connect.TopAnchor, -8),
            _connect.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor, 14),
            _connect.BottomAnchor.ConstraintEqualTo(status.TopAnchor, -8),
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
        ReloadAsync().FireAndForget();
    }

    /// <summary>Connect to your web library… was chosen.</summary>
    public event EventHandler? ConnectRequested;

    /// <summary>Whether this Mac is connected to a web library; the sidebar offers to connect it while it isn't.</summary>
    public void ShowConnected(bool connected)
    {
        _ = View;
        _connect.Hidden = connected;
    }

    /// <summary>The page showing, which the sidebar marks; on the Library, the folder it narrows to.</summary>
    public void Show(PageViewModel? page)
    {
        _page.Dispose();
        _page = new Bindings();
        _shownPage = page;
        _library = page as LibraryViewModel;
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
        var folders = await roots.ListAsync();
        _rows =
        [
            new Row(false, "Home", "house", null, Route.Home),
            new Row(false, "All books", "books.vertical", null),
            new Row(false, "Collections", "square.stack", null, Route.Collections),
            new Row(false, "Binders", "book.closed", null, Route.Sessions),
            new Row(false, "Needs review", "tray", null, Route.NeedsReview),
            new Row(true, "Library folders", "", null),
            .. folders.Select(f => new Row(false, shelf.NameOf(f.Path), WebLibraryPaths.IsWebPath(f.Path) ? "globe" : "folder", f.Id)),
        ];
        _table.ReloadData();
        SelectCurrent();
    }

    void SelectCurrent()
    {
        if (_table is null) return;
        var root = _library?.Filters.FolderChoice.Value is { Folder.Length: 0 } folder ? folder.RootId : (long?)null;
        var route = _shownPage?.NavRoute ?? Route.Library;
        var index = route == Route.Library ? _rows.FindIndex(r => !r.IsHeader && r.Route == Route.Library && r.RootId == root) : _rows.FindIndex(r => r.Route == route && !r.IsHeader);
        _selecting = true;
        if (index >= 0) _table.SelectRow(index, false);
        else _table.DeselectAll(this);
        _selecting = false;
    }

    void Choose(Row row)
    {
        if (_selecting) return;
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
