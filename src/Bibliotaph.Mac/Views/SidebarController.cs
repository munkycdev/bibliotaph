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
/// A source list: the whole library, then each library folder, which narrows the Library to it as its folder filter
/// does on Windows. Under it, what indexing is doing.
/// </summary>
public sealed class SidebarController(LibraryActivity activity, FolderAdding adding, SourceRootStore roots, WebShelfCache shelf) : NSViewController
{
    sealed record Row(bool IsHeader, string Title, string Symbol, long? RootId);

    readonly Bindings _bindings = new();
    Bindings _page = new();
    LibraryViewModel? _library;
    List<Row> _rows = [];
    NSTableView _table = null!;
    Source? _source;
    bool _selecting;

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
        EventHandler refreshed = (_, _) => Bindings.OnMain(() => ReloadAsync().FireAndForget());
        adding.Added += refreshed;
        _bindings.Add(() => adding.Added -= refreshed);
        ReloadAsync().FireAndForget();
    }

    /// <summary>The Library page the sidebar narrows, or none while another page shows.</summary>
    public void Show(LibraryViewModel? library)
    {
        _page.Dispose();
        _page = new Bindings();
        _library = library;
        if (library is not null)
            _page.Property(library.Filters, nameof(LibraryFilterState.FolderChoice), SelectCurrent);
        SelectCurrent();
    }

    async Task ReloadAsync()
    {
        var folders = await roots.ListAsync();
        _rows =
        [
            new Row(false, "All books", "books.vertical", null),
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
        var index = _rows.FindIndex(r => !r.IsHeader && r.RootId == root);
        _selecting = true;
        if (index >= 0) _table.SelectRow(index, false);
        else _table.DeselectAll(this);
        _selecting = false;
    }

    void Choose(Row row)
    {
        if (_selecting || _library is not { } library) return;
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
            return item.IsHeader ? Header(item.Title) : Cell(item);
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

        static NSTableCellView Cell(Row row)
        {
            var image = new NSImageView { Image = NSImage.GetSystemSymbol(row.Symbol, null)! };
            var text = NSTextField.CreateLabel(row.Title);
            text.LineBreakMode = NSLineBreakMode.TruncatingMiddle;
            var cell = new NSTableCellView { ImageView = image, TextField = text };
            Pin(cell, text, image);
            return cell;
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
