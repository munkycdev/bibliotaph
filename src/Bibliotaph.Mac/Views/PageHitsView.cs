using AppKit;
using Bibliotaph.Index;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.ViewModels;
using Foundation;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// A search's "Inside documents": each book with the pages the words were found on, as on Windows. A page is quoted
/// around its hits, which stand out, with Open Page, the current binder's +, and a window of its own; the book's title
/// opens its details.
/// </summary>
sealed class PageHitsView : NSScrollView
{
    readonly LibraryViewModel _library;
    readonly MacCovers _covers;
    readonly NSStackView _list;

    public PageHitsView(LibraryViewModel library, MacCovers covers)
    {
        _library = library;
        _covers = covers;
        HasVerticalScroller = true;
        DrawsBackground = false;
        _list = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 0,
            EdgeInsets = new NSEdgeInsets(4, 24, 24, 24),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        var document = new Flipped { TranslatesAutoresizingMaskIntoConstraints = false };
        document.AddSubview(_list);
        DocumentView = document;
        NSLayoutConstraint.ActivateConstraints(
        [
            _list.TopAnchor.ConstraintEqualTo(document.TopAnchor),
            _list.LeadingAnchor.ConstraintEqualTo(document.LeadingAnchor),
            _list.TrailingAnchor.ConstraintEqualTo(document.TrailingAnchor),
            _list.BottomAnchor.ConstraintEqualTo(document.BottomAnchor),
            document.WidthAnchor.ConstraintEqualTo(ContentView.WidthAnchor),
        ]);
    }

    public void Show(IReadOnlyList<DocumentHitsViewModel> books)
    {
        foreach (var view in _list.ArrangedSubviews) view.RemoveFromSuperview();
        foreach (var book in books)
        {
            var row = Book(book);
            _list.AddArrangedSubview(row);
            row.WidthAnchor.ConstraintEqualTo(_list.WidthAnchor, 1, -48).Active = true;
        }
        ContentView.ScrollToPoint(CoreGraphics.CGPoint.Empty);
        ReflectScrolledClipView(ContentView);
    }

    NSStackView Book(DocumentHitsViewModel book)
    {
        var cover = new NSImageView { ImageScaling = NSImageScale.ProportionallyUpOrDown, WantsLayer = true };
        cover.Layer!.CornerRadius = 4;
        cover.Layer.MasksToBounds = true;
        cover.Layer.BackgroundColor = NSColor.QuaternarySystemFill.CGColor;
        cover.WidthAnchor.ConstraintEqualTo(60).Active = true;
        cover.HeightAnchor.ConstraintEqualTo(83).Active = true;
        if (book.Item.CoverName is { } name)
        {
            cover.Image = _covers.TryGet(name)!;
            if (cover.Image is null) _ = LoadAsync(cover, name);
        }

        var title = NSButton.CreateButton(book.Item.Title, () => _library.OpenDetailsCommand.Execute(book.Item));
        title.Bordered = false;
        title.Font = NSFont.SystemFontOfSize(14, NSFontWeight.Semibold)!;
        title.ContentTintColor = NSColor.Label;
        title.ToolTip = $"Details for {book.Item.Title}";
        var meta = Small(book.Item.Meta);
        var pages = Column(title, meta);
        pages.Spacing = 2;
        foreach (var page in book.Pages)
        {
            var hit = Page(page);
            pages.AddArrangedSubview(hit);
            hit.WidthAnchor.ConstraintEqualTo(pages.WidthAnchor).Active = true;
        }
        if (book.HasMore)
        {
            var more = Small(book.More);
            pages.AddArrangedSubview(more);
            pages.SetCustomSpacing(10, pages.ArrangedSubviews[^2]);
        }

        // The cover at the leading edge and the book's pages filling the rest, pinned rather than stacked, so the pages
        // always take the whole width.
        var row = new NSView();
        foreach (var view in new NSView[] { cover, pages })
        {
            view.TranslatesAutoresizingMaskIntoConstraints = false;
            row.AddSubview(view);
        }
        NSLayoutConstraint.ActivateConstraints(
        [
            cover.TopAnchor.ConstraintEqualTo(row.TopAnchor, 18),
            cover.LeadingAnchor.ConstraintEqualTo(row.LeadingAnchor),
            cover.BottomAnchor.ConstraintLessThanOrEqualTo(row.BottomAnchor, -18),
            pages.TopAnchor.ConstraintEqualTo(row.TopAnchor, 18),
            pages.LeadingAnchor.ConstraintEqualTo(cover.TrailingAnchor, 18),
            pages.TrailingAnchor.ConstraintEqualTo(row.TrailingAnchor),
            pages.BottomAnchor.ConstraintEqualTo(row.BottomAnchor, -18),
        ]);
        var line = new NSBox { BoxType = NSBoxType.NSBoxSeparator };
        var block = NSStackView.FromViews([row, line]);
        block.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        block.Spacing = 0;
        row.WidthAnchor.ConstraintEqualTo(block.WidthAnchor).Active = true;
        line.WidthAnchor.ConstraintEqualTo(block.WidthAnchor).Active = true;
        return block;
    }

    /// <summary>Where the page is, its buttons, the words around the hits, and where its text came from.</summary>
    NSStackView Page(PageHitViewModel page)
    {
        var where = NSTextField.CreateLabel(page.Where);
        where.Font = NSFont.SystemFontOfSize(11, NSFontWeight.Medium)!;
        where.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        // Option-click opens it in a window of its own, as the button beside it does.
        var open = NSButton.CreateButton("Open Page", () =>
        {
            var own = NSApplication.SharedApplication.CurrentEvent?.ModifierFlags.HasFlag(NSEventModifierMask.AlternateKeyMask) == true;
            (own ? _library.OpenPageInNewWindowCommand : _library.OpenPageCommand).Execute(page);
        });
        open.ControlSize = NSControlSize.Small;
        open.ToolTip = "Open the book at this page (Option-click opens it in a window of its own)";
        var add = Icon("plus", "Add this page to the current binder", () => _library.AddPageToSessionCommand.Execute(page));
        var window = Icon("macwindow.badge.plus", "Open in a window of its own", () => _library.OpenPageInNewWindowCommand.Execute(page));
        var head = NSStackView.FromViews([where, open, add, window]);
        head.Spacing = 6;
        head.Distribution = NSStackViewDistribution.Fill;

        var snippet = NSTextField.CreateWrappingLabel("");
        snippet.AttributedStringValue = Snippet(page.Snippet);
        snippet.Selectable = true;
        var source = Small(page.Source);
        var chip = new Tinted(source, Palette.Soft, 6, new NSEdgeInsets(1, 6, 1, 6));
        var column = Column(head, snippet, chip);
        column.Spacing = 5;
        column.EdgeInsets = new NSEdgeInsets(10, 0, 0, 0);
        head.WidthAnchor.ConstraintEqualTo(column.WidthAnchor).Active = true;
        snippet.WidthAnchor.ConstraintEqualTo(column.WidthAnchor).Active = true;
        return column;
    }

    /// <summary>The page's words, with the search's hits marked as the index marked them, in bold on the soft green.</summary>
    static NSMutableAttributedString Snippet(string text)
    {
        var plain = new NSStringAttributes { Font = NSFont.SystemFontOfSize(12)!, ForegroundColor = NSColor.SecondaryLabel };
        var hit = new NSStringAttributes { Font = NSFont.SystemFontOfSize(12, NSFontWeight.Semibold)!, ForegroundColor = NSColor.Label, BackgroundColor = Palette.Soft };
        var result = new NSMutableAttributedString();
        var inHit = false;
        var start = 0;
        void Flush(int end)
        {
            if (end > start) result.Append(new NSAttributedString(text[start..end], inHit ? hit : plain));
        }
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != LibraryQueries.HitStart && text[i] != LibraryQueries.HitEnd) continue;
            Flush(i);
            inHit = text[i] == LibraryQueries.HitStart;
            start = i + 1;
        }
        Flush(text.Length);
        return result;
    }

    static NSButton Icon(string symbol, string tip, Action action)
    {
        var button = NSButton.CreateButton(NSImage.GetSystemSymbol(symbol, tip)!, action);
        button.Bordered = false;
        button.ContentTintColor = NSColor.SecondaryLabel;
        button.ToolTip = tip;
        return button;
    }

    static NSTextField Small(string text)
    {
        var label = NSTextField.CreateLabel(text);
        label.Font = NSFont.SystemFontOfSize(11)!;
        label.TextColor = NSColor.SecondaryLabel;
        label.LineBreakMode = NSLineBreakMode.TruncatingTail;
        return label;
    }

    static NSStackView Column(params NSView[] views)
    {
        var column = NSStackView.FromViews(views);
        column.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        column.Alignment = NSLayoutAttribute.Leading;
        return column;
    }

    async Task LoadAsync(NSImageView view, string name)
    {
        var image = await _covers.LoadAsync(name);
        Bindings.OnMain(() => view.Image = image!);
    }

    sealed class Flipped : NSView
    {
        public override bool IsFlipped => true;
    }
}
