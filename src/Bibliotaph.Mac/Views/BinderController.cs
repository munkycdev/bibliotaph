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
        var steps = NSStackView.FromViews([counter, new NSView(), edit, previous, next]);

        _table = new NSTableView { Style = NSTableViewStyle.SourceList, HeaderView = null!, UsesAutomaticRowHeights = true };
        _table.AddColumn(new NSTableColumn("item"));
        var source = new Source(this);
        _table.DataSource = source;
        _table.Delegate = source;
        var list = new NSScrollView { DocumentView = _table, HasVerticalScroller = true, DrawsBackground = false };

        var side = new NSView();
        foreach (var view in new NSView[] { eyebrow, title, steps, list })
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
            list.TopAnchor.ConstraintEqualTo(steps.BottomAnchor, 6),
            list.LeadingAnchor.ConstraintEqualTo(side.LeadingAnchor),
            list.TrailingAnchor.ConstraintEqualTo(side.TrailingAnchor),
            list.BottomAnchor.ConstraintEqualTo(side.BottomAnchor),
            side.WidthAnchor.ConstraintEqualTo(260),
        ]);

        var reader = new ReaderController(binder.Viewer, null, titleInBar: true);
        AddChildViewController(reader);
        var split = new NSSplitView { IsVertical = true, DividerStyle = NSSplitViewDividerStyle.Thin };
        split.AddArrangedSubview(side);
        split.AddArrangedSubview(reader.View);
        View = split;

        _bindings.Properties(binder, [nameof(SessionReaderViewModel.Pack), nameof(SessionReaderViewModel.Current)], () =>
        {
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
            if (owner._rows[(int)row] is SessionSectionRow section) return Text(section.Name.ToUpperInvariant(), NSFont.SystemFontOfSize(10, NSFontWeight.Semibold)!, NSColor.SecondaryLabel);
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
