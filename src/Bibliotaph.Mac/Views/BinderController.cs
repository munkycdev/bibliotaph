using AppKit;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.ViewModels;
using Foundation;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// A binder at the table: its pages in order down the side, by section, and the reader beside them showing the one
/// chosen, with the previous and next item a click away. The binder's reader is the shared one, kept to the item.
/// </summary>
public sealed class BinderController(SessionReaderViewModel binder) : NSViewController
{
    readonly Bindings _bindings = new();

    internal SessionReaderViewModel Binder => binder;
    NSTableView _table = null!;
    List<object> _rows = [];
    bool _selecting;

    public override void LoadView()
    {
        var title = NSTextField.CreateLabel("");
        title.Font = NSFont.SystemFontOfSize(15, NSFontWeight.Semibold)!;
        title.LineBreakMode = NSLineBreakMode.TruncatingTail;
        var eyebrow = NSTextField.CreateLabel("");
        eyebrow.Font = NSFont.SystemFontOfSize(10, NSFontWeight.Semibold)!;
        eyebrow.TextColor = NSColor.SecondaryLabel;
        var counter = NSTextField.CreateLabel("");
        counter.TextColor = NSColor.SecondaryLabel;
        var previous = NSButton.CreateButton(NSImage.GetSystemSymbol("chevron.up", "Previous item")!, () => { });
        var next = NSButton.CreateButton(NSImage.GetSystemSymbol("chevron.down", "Next item")!, () => { });
        _bindings.Command(previous, binder.PreviousItemCommand);
        _bindings.Command(next, binder.NextItemCommand);
        var edit = NSButton.CreateButton("Edit binder", () => { });
        edit.ControlSize = NSControlSize.Small;
        _bindings.Command(edit, binder.EditCommand);
        var more = NSButton.CreateButton(NSImage.GetSystemSymbol("ellipsis.circle", "More")!, () => { });
        more.Bordered = false;
        more.ToolTip = "More";
        more.Activated += (_, _) => MoreMenu().PopUpMenu(null, new CoreGraphics.CGPoint(0, more.Bounds.Height + 4), more);
        var steps = NSStackView.FromViews([counter, new NSView(), edit, more, previous, next]);
        // Back to the item, once the pages have moved off it, as Windows has it.
        var backToItem = NSButton.CreateButton("Back to the item", () => { });
        backToItem.ControlSize = NSControlSize.Small;
        backToItem.Image = NSImage.GetSystemSymbol("arrow.uturn.backward", null)!;
        backToItem.ImagePosition = NSCellImagePosition.ImageLeading;
        _bindings.Command(backToItem, binder.BackToItemCommand);
        var notes = ItemNotes();

        _table = new NSTableView { Style = NSTableViewStyle.SourceList, HeaderView = null!, UsesAutomaticRowHeights = true };
        _table.AddColumn(new NSTableColumn("item"));
        var source = new Source(this);
        _table.DataSource = source;
        _table.Delegate = source;
        var list = new NSScrollView { DocumentView = _table, HasVerticalScroller = true, DrawsBackground = false };

        var side = new NSView();
        foreach (var view in new NSView[] { eyebrow, title, steps, backToItem, list, notes })
        {
            view.TranslatesAutoresizingMaskIntoConstraints = false;
            side.AddSubview(view);
        }
        NSLayoutConstraint.ActivateConstraints(
        [
            eyebrow.TopAnchor.ConstraintEqualTo(side.SafeAreaLayoutGuide.TopAnchor, 12),
            eyebrow.LeadingAnchor.ConstraintEqualTo(side.LeadingAnchor, 14),
            title.TopAnchor.ConstraintEqualTo(eyebrow.BottomAnchor, 2),
            title.LeadingAnchor.ConstraintEqualTo(side.LeadingAnchor, 14),
            title.TrailingAnchor.ConstraintLessThanOrEqualTo(side.TrailingAnchor, -10),
            steps.TopAnchor.ConstraintEqualTo(title.BottomAnchor, 6),
            steps.LeadingAnchor.ConstraintEqualTo(side.LeadingAnchor, 14),
            steps.TrailingAnchor.ConstraintEqualTo(side.TrailingAnchor, -10),
            backToItem.TopAnchor.ConstraintEqualTo(steps.BottomAnchor, 4),
            backToItem.LeadingAnchor.ConstraintEqualTo(side.LeadingAnchor, 14),
            list.TopAnchor.ConstraintEqualTo(backToItem.BottomAnchor, 4),
            list.LeadingAnchor.ConstraintEqualTo(side.LeadingAnchor),
            list.TrailingAnchor.ConstraintEqualTo(side.TrailingAnchor),
            notes.TopAnchor.ConstraintEqualTo(list.BottomAnchor, 6),
            notes.LeadingAnchor.ConstraintEqualTo(side.LeadingAnchor, 14),
            notes.TrailingAnchor.ConstraintEqualTo(side.TrailingAnchor, -10),
            notes.BottomAnchor.ConstraintEqualTo(side.BottomAnchor, -10),
            side.WidthAnchor.ConstraintEqualTo(260),
        ]);

        var reader = new ReaderController(binder.Viewer, null, titleInBar: true);
        AddChildViewController(reader);
        var split = new NSSplitView { IsVertical = true, DividerStyle = NSSplitViewDividerStyle.Thin };
        split.AddArrangedSubview(side);
        split.AddArrangedSubview(reader.View);
        View = split;

        _bindings.Properties(binder, [nameof(SessionReaderViewModel.Pack), nameof(SessionReaderViewModel.Current), nameof(SessionReaderViewModel.IsOutsideItem)], () =>
        {
            backToItem.Hidden = !binder.IsOutsideItem;
            title.StringValue = binder.Title;
            eyebrow.StringValue = binder.Eyebrow;
            counter.StringValue = binder.Counter;
            SelectCurrent();
        });
        _bindings.Collection(binder.Rows, _ => Reload());
        Reload();
    }

    void Reload()
    {
        _rows = [.. binder.Rows];
        _table.ReloadData();
        SelectCurrent();
    }

    void SelectCurrent()
    {
        var index = binder.Current is { } current ? _rows.IndexOf(current) : -1;
        _selecting = true;
        if (index >= 0) _table.SelectRow(index, false);
        else _table.DeselectAll(this);
        _selecting = false;
    }

    /// <summary>The notes on the item in hand's pages, under the list while notes are shown.</summary>
    NSStackView ItemNotes()
    {
        var heading = NSTextField.CreateLabel("Notes on this item");
        heading.Font = NSFont.SystemFontOfSize(11, NSFontWeight.Semibold)!;
        heading.TextColor = NSColor.SecondaryLabel;
        var column = NSStackView.FromViews([heading]);
        column.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        column.Alignment = NSLayoutAttribute.Leading;
        column.Spacing = 4;
        void Show()
        {
            foreach (var view in column.ArrangedSubviews.Skip(1)) view.RemoveFromSuperview();
            foreach (var note in binder.ItemNotes.Take(6))
            {
                var line = NSTextField.CreateWrappingLabel($"{note.Pages} · {note.Text}");
                line.Font = NSFont.SystemFontOfSize(11)!;
                line.MaximumNumberOfLines = 3;
                column.AddArrangedSubview(line);
                line.WidthAnchor.ConstraintLessThanOrEqualTo(column.WidthAnchor).Active = true;
            }
            column.Hidden = !binder.HasItemNotes || !binder.ShowNotes;
        }
        _bindings.Collection(binder.ItemNotes, _ => Show());
        _bindings.Properties(binder, [nameof(SessionReaderViewModel.HasItemNotes), nameof(SessionReaderViewModel.ShowNotes)], Show);
        return column;
    }

    /// <summary>What's done less often at the table: rename the item, add the page in view, pin the binder, notes, its window.</summary>
    NSMenu MoreMenu()
    {
        var menu = new NSMenu();
        void Item(string title, System.Windows.Input.ICommand command, object? parameter = null)
        {
            if (command.CanExecute(parameter)) menu.AddItem(new NSMenuItem(title, (_, _) => command.Execute(parameter)));
        }
        if (binder.Current is { } current) menu.AddItem(new NSMenuItem("Rename the Item…", (_, _) => Rename(current)));
        Item("Add This Page After It", binder.AddPageCommand);
        Item(binder.ShowNotes ? "Hide Notes" : "Show Notes", binder.ToggleNotesCommand);
        Item(binder.PinLabel, binder.TogglePinCommand);
        Item("Open in a Window of Its Own", binder.PopOutCommand);
        Item("Return to the Main Window", binder.ReturnToMainWindowCommand);
        return menu;
    }

    /// <summary>The item's name to type over; a blank one shows the book's title again.</summary>
    void Rename(SessionItemRow row)
    {
        var alert = new NSAlert { MessageText = "Rename the item", InformativeText = "A blank name shows the book's title again." };
        var field = new NSTextField(new CoreGraphics.CGRect(0, 0, 280, 24)) { StringValue = row.Label };
        alert.AccessoryView = field;
        alert.AddButton("Rename").KeyEquivalent = "\r";
        alert.AddButton("Cancel").KeyEquivalent = "\u001b";
        alert.Window.InitialFirstResponder = field;
        if (View.Window is not { } host) return;
        alert.BeginSheet(host, response =>
        {
            if ((long)response == (long)NSAlertButtonReturn.First)
            {
                binder.RenameItemCommand.Execute(row);
                row.Draft = field.StringValue;
                SessionReaderViewModel.CommitRename(row);
            }
            alert.Dispose();
        });
    }

    // The Go menu's ⌥⌘→ and ⌥⌘←, as Ctrl+Right and Ctrl+Left step through a binder on Windows.
    [Foundation.Export("binderNextItem:")]
    void NextItem(Foundation.NSObject sender)
    {
        if (binder.NextItemCommand.CanExecute(null)) binder.NextItemCommand.Execute(null);
    }

    [Foundation.Export("binderPreviousItem:")]
    void PreviousItem(Foundation.NSObject sender)
    {
        if (binder.PreviousItemCommand.CanExecute(null)) binder.PreviousItemCommand.Execute(null);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _bindings.Dispose();
        base.Dispose(disposing);
    }

    sealed class Source(BinderController owner) : NSTableViewDelegate, INSTableViewDataSource
    {
        [Export("numberOfRowsInTableView:")]
        public nint GetRowCount(NSTableView tableView) => owner._rows.Count;

        public override NSView GetViewForItem(NSTableView tableView, NSTableColumn tableColumn, nint row)
        {
            if (owner._rows[(int)row] is SessionSectionRow section) return Text(section.Name, NSFont.SystemFontOfSize(11, NSFontWeight.Semibold)!, NSColor.SecondaryLabel);
            if (owner._rows[(int)row] is not SessionItemRow item) return new NSView();
            var heading = Text($"{item.Number}  {item.Heading}", NSFont.SystemFontOfSize(12, NSFontWeight.Medium)!, item.IsDimmed ? NSColor.TertiaryLabel : NSColor.Label);
            var where = Text(item.Reason ?? item.Where, NSFont.SystemFontOfSize(11)!, item.HasReason ? NSColor.SystemOrange : NSColor.SecondaryLabel);
            var stack = NSStackView.FromViews([heading, where]);
            stack.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
            stack.Alignment = NSLayoutAttribute.Leading;
            stack.Spacing = 1;
            stack.EdgeInsets = new NSEdgeInsets(4, 4, 4, 4);
            return stack;
        }

        public override bool IsGroupRow(NSTableView tableView, nint row) => owner._rows[(int)row] is SessionSectionRow;

        public override bool ShouldSelectRow(NSTableView tableView, nint row) => owner._rows[(int)row] is SessionItemRow { CanOpen: true } or SessionItemRow { OpensOnline: true };

        public override void SelectionDidChange(NSNotification notification)
        {
            var row = (int)owner._table.SelectedRow;
            if (owner._selecting || row < 0 || row >= owner._rows.Count || owner._rows[row] is not SessionItemRow item) return;
            if (owner.Binder.OpenItemCommand.CanExecute(item)) owner.Binder.OpenItemCommand.Execute(item);
        }

        static NSTextField Text(string text, NSFont font, NSColor color)
        {
            var label = NSTextField.CreateWrappingLabel(text);
            label.Font = font;
            label.TextColor = color;
            label.MaximumNumberOfLines = 2;
            return label;
        }
    }
}
