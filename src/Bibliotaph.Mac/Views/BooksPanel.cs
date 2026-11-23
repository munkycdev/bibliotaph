using AppKit;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Presentation.ViewModels;
using Bibliotaph.Processing;
using CoreGraphics;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// Organize's Books panel beside the covers, as on Windows: the books in the folder of the card ticked or picked up
/// last, those dropped on lately, and any found by their title. Each is a place to drop maps and handouts, and its
/// Goes With gives it the ticked cards, for the keyboard and for a book scrolled out of view.
/// </summary>
sealed class BooksPanel : NSView
{
    readonly OrganizePanelViewModel _panel;
    readonly LibrarySelectionViewModel _selection;
    readonly Func<OrganizeDrag?> _dragging;
    readonly NSStackView _rows;
    readonly Bindings _bindings = new();

    public BooksPanel(LibrarySelectionViewModel selection, Func<OrganizeDrag?> dragging)
    {
        _selection = selection;
        _panel = selection.Panel;
        _dragging = dragging;
        WantsLayer = true;
        Layer!.CornerRadius = 8;

        var title = NSTextField.CreateLabel("Books");
        title.Font = NSFont.SystemFontOfSize(11, NSFontWeight.Semibold)!;
        title.TextColor = NSColor.SecondaryLabel;
        var close = NSButton.CreateButton(NSImage.GetSystemSymbol("xmark", "Close the Books panel")!, () => _panel.CloseCommand.Execute(null));
        close.Bordered = false;
        close.ToolTip = "Close the Books panel";
        var head = NSStackView.FromViews([title, new NSView(), close]);
        var find = new NSSearchField { PlaceholderString = "Find a book by its title", SendsSearchStringImmediately = true };
        find.Changed += (_, _) => _panel.Query = find.StringValue;
        _rows = new NSStackView { Orientation = NSUserInterfaceLayoutOrientation.Vertical, Alignment = NSLayoutAttribute.Leading, Spacing = 5, TranslatesAutoresizingMaskIntoConstraints = false };
        var document = new FlippedView { TranslatesAutoresizingMaskIntoConstraints = false };
        document.AddSubview(_rows);
        var scroll = new NSScrollView { HasVerticalScroller = true, DrawsBackground = false, DocumentView = document };
        foreach (var view in new NSView[] { head, find, scroll })
        {
            view.TranslatesAutoresizingMaskIntoConstraints = false;
            AddSubview(view);
        }
        NSLayoutConstraint.ActivateConstraints(
        [
            head.TopAnchor.ConstraintEqualTo(TopAnchor, 12),
            head.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 14),
            head.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -10),
            find.TopAnchor.ConstraintEqualTo(head.BottomAnchor, 8),
            find.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 14),
            find.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -14),
            scroll.TopAnchor.ConstraintEqualTo(find.BottomAnchor, 10),
            scroll.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 14),
            scroll.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -14),
            scroll.BottomAnchor.ConstraintEqualTo(BottomAnchor, -12),
            _rows.TopAnchor.ConstraintEqualTo(document.TopAnchor),
            _rows.LeadingAnchor.ConstraintEqualTo(document.LeadingAnchor),
            _rows.TrailingAnchor.ConstraintEqualTo(document.TrailingAnchor),
            _rows.BottomAnchor.ConstraintEqualTo(document.BottomAnchor),
            document.WidthAnchor.ConstraintEqualTo(scroll.ContentView.WidthAnchor),
        ]);
        _bindings.Properties(_panel, [nameof(OrganizePanelViewModel.Found), nameof(OrganizePanelViewModel.NearBooks), nameof(OrganizePanelViewModel.Recent),
            nameof(OrganizePanelViewModel.IsFinding), nameof(OrganizePanelViewModel.NoneFound), nameof(OrganizePanelViewModel.HasNear),
            nameof(OrganizePanelViewModel.ShowsHint)], Show);
        _bindings.Property(selection.Books, nameof(BookSelection.HasAny), Show);
    }

    public override void UpdateLayer() => Layer!.BackgroundColor = NSColor.QuaternarySystemFill.CGColor;

    public override bool WantsUpdateLayer => true;

    void Show()
    {
        foreach (var view in _rows.ArrangedSubviews) view.RemoveFromSuperview();
        if (_panel.IsFinding)
        {
            Heading("Found");
            if (_panel.NoneFound) Note("No book has those words in its title.");
            foreach (var book in _panel.Found) Add(new BookRow(book, this));
            return;
        }
        if (_panel.HasNear)
        {
            Heading("In This Folder");
            if (!_panel.HasNearBooks) Note("No books beside it.");
            foreach (var book in _panel.NearBooks) Add(new BookRow(book, this));
        }
        if (_panel.HasRecent)
        {
            Heading("Dropped On Lately");
            foreach (var book in _panel.Recent) Add(new BookRow(book, this));
        }
        if (_panel.ShowsHint) Note("Tick or drag a map, and the books in its folder show here. Or find a book by its title.");
    }

    void Heading(string text)
    {
        var label = NSTextField.CreateLabel(text);
        label.Font = NSFont.SystemFontOfSize(10, NSFontWeight.Semibold)!;
        label.TextColor = NSColor.SecondaryLabel;
        if (_rows.ArrangedSubviews.Length > 0) _rows.SetCustomSpacing(12, _rows.ArrangedSubviews[^1]);
        _rows.AddArrangedSubview(label);
    }

    void Note(string text)
    {
        var label = NSTextField.CreateWrappingLabel(text);
        label.Font = NSFont.SystemFontOfSize(11)!;
        label.TextColor = NSColor.SecondaryLabel;
        Add(label);
    }

    void Add(NSView view)
    {
        _rows.AddArrangedSubview(view);
        view.WidthAnchor.ConstraintEqualTo(_rows.WidthAnchor).Active = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _bindings.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>A book to drop cards on, with Goes With for the ticked ones.</summary>
    sealed class BookRow : NSView
    {
        readonly SourceBookChoice _book;
        readonly BooksPanel _owner;

        public BookRow(SourceBookChoice book, BooksPanel owner)
        {
            _book = book;
            _owner = owner;
            WantsLayer = true;
            Layer!.CornerRadius = 5;
            Layer.BorderWidth = 1;
            var title = NSTextField.CreateLabel(book.Title);
            title.Font = NSFont.SystemFontOfSize(12, NSFontWeight.Medium)!;
            title.LineBreakMode = NSLineBreakMode.TruncatingTail;
            var detail = NSTextField.CreateLabel(book.Detail);
            detail.Font = NSFont.SystemFontOfSize(11)!;
            detail.TextColor = NSColor.SecondaryLabel;
            detail.LineBreakMode = NSLineBreakMode.TruncatingTail;
            detail.Hidden = book.Detail.Length == 0;
            var text = NSStackView.FromViews([title, detail]);
            text.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
            text.Alignment = NSLayoutAttribute.Leading;
            text.Spacing = 1;
            text.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
            text.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
            var goes = NSButton.CreateButton("Goes With", () => owner._panel.LinkCommand.Execute(book));
            goes.ControlSize = NSControlSize.Small;
            goes.Image = NSImage.GetSystemSymbol("link", null)!;
            goes.ImagePosition = NSCellImagePosition.ImageLeading;
            goes.ToolTip = $"The ticked cards go with {book.Title}";
            goes.Enabled = owner._selection.Books.HasAny;
            var line = NSStackView.FromViews([text, goes]);
            line.Spacing = 8;
            line.EdgeInsets = new NSEdgeInsets(7, 10, 7, 8);
            line.TranslatesAutoresizingMaskIntoConstraints = false;
            AddSubview(line);
            NSLayoutConstraint.ActivateConstraints(
            [
                line.TopAnchor.ConstraintEqualTo(TopAnchor),
                line.BottomAnchor.ConstraintEqualTo(BottomAnchor),
                line.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
                line.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            ]);
            RegisterForDraggedTypes([LibraryController.CardType]);
            Marked(false);
        }

        void Marked(bool over)
        {
            Layer!.BorderColor = over ? NSColor.ControlAccent.CGColor : NSColor.Separator.CGColor;
            Layer.BorderWidth = over ? 2 : 1;
            Layer.BackgroundColor = NSColor.ControlBackground.CGColor;
        }

        public override NSDragOperation DraggingEntered(INSDraggingInfo sender)
        {
            var ok = _owner._dragging() is not null;
            Marked(ok);
            return ok ? NSDragOperation.Generic : NSDragOperation.None;
        }

        public override void DraggingExited(INSDraggingInfo? sender) => Marked(false);

        public override bool PerformDragOperation(INSDraggingInfo sender)
        {
            Marked(false);
            if (_owner._dragging() is not { } drag) return false;
            _owner._selection.DropOnBookAsync(drag, _book).FireAndForget();
            return true;
        }
    }

    sealed class FlippedView : NSView
    {
        public override bool IsFlipped => true;
    }
}
