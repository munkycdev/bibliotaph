using AppKit;
using Foundation;

namespace Bibliotaph.MacSpike;

/// <summary>A source list: All books, then each web library folder with its count.</summary>
sealed class SidebarController(LibraryViewModel library) : NSViewController
{
    LibraryViewModel Library => library;

    sealed record Row(bool IsHeader, string Title, string Symbol, long? FolderId, int Count);

    List<Row> _rows = [];
    NSTableView _table = null!;
    Source? _source;

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
        View = new NSScrollView { DocumentView = _table, HasVerticalScroller = true, DrawsBackground = false };

        Ui.Watch(library, nameof(LibraryViewModel.Folders), Reload);
    }

    void Reload()
    {
        var folders = library.Folders;
        _rows = folders.Count == 0 ? [] :
        [
            new Row(false, folders[0].Name, "books.vertical", null, folders[0].Count),
            new Row(true, "Library folders", "", null, 0),
            .. folders.Skip(1).Select(f => new Row(false, f.Name, "folder", f.Id, f.Count)),
        ];
        _table.ReloadData();
        var selected = _rows.FindIndex(r => !r.IsHeader && r.FolderId == library.SelectedFolderId);
        if (selected >= 0) _table.SelectRow(selected, false);
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
            if (row >= 0 && row < owner._rows.Count) owner.Library.SelectedFolderId = owner._rows[row].FolderId;
        }

        static NSTableCellView Header(string title)
        {
            var text = NSTextField.CreateLabel(title);
            var cell = new NSTableCellView { TextField = text };
            Pin(cell, text, null, null);
            return cell;
        }

        static NSTableCellView Cell(Row row)
        {
            var image = new NSImageView { Image = NSImage.GetSystemSymbol(row.Symbol, null)! };
            var text = NSTextField.CreateLabel(row.Title);
            text.LineBreakMode = NSLineBreakMode.TruncatingTail;
            var count = NSTextField.CreateLabel(row.Count.ToString("N0"));
            count.TextColor = NSColor.SecondaryLabel;
            count.Font = NSFont.MonospacedDigitSystemFontOfSize(11, NSFontWeight.Regular);
            var cell = new NSTableCellView { ImageView = image, TextField = text };
            Pin(cell, text, image, count);
            return cell;
        }

        static void Pin(NSView cell, NSTextField text, NSImageView? image, NSTextField? count)
        {
            NSView[] views = image is null ? [text] : count is null ? [image, text] : [image, text, count];
            foreach (var view in views)
            {
                view.TranslatesAutoresizingMaskIntoConstraints = false;
                cell.AddSubview(view);
            }
            var constraints = new List<NSLayoutConstraint>
            {
                text.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
                text.LeadingAnchor.ConstraintEqualTo(image?.TrailingAnchor ?? cell.LeadingAnchor, image is null ? 2 : 6),
            };
            if (image is not null)
            {
                constraints.Add(image.LeadingAnchor.ConstraintEqualTo(cell.LeadingAnchor, 2));
                constraints.Add(image.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor));
                constraints.Add(image.WidthAnchor.ConstraintEqualTo(18));
            }
            if (count is not null)
            {
                constraints.Add(count.TrailingAnchor.ConstraintEqualTo(cell.TrailingAnchor, -6));
                constraints.Add(count.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor));
                constraints.Add(text.TrailingAnchor.ConstraintLessThanOrEqualTo(count.LeadingAnchor, -6));
            }
            NSLayoutConstraint.ActivateConstraints([.. constraints]);
        }
    }
}
