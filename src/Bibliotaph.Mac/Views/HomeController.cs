using AppKit;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Presentation.ViewModels;
using CoreGraphics;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// Home: what the library holds, the binder being prepared, what's pinned, and the books opened and added most
/// recently, each a click from opening. A new library with no folders offers Add folder.
/// </summary>
public sealed class HomeController(HomeViewModel home, MacCovers covers) : NSViewController, ITourAnchors
{
    NSButton? _addFolder;
    readonly List<WrapView> _shelves = [];

    readonly Bindings _bindings = new();

    /// <summary>Add a folder before any is in, and the first books once they are.</summary>
    public NSView? Anchor(Core.TourAnchor anchor) => anchor switch
    {
        Core.TourAnchor.AddFolder => _addFolder,
        Core.TourAnchor.FirstBooks => _shelves.FirstOrDefault(s => !s.Hidden && s.Subviews.Length > 0)?.Subviews[0],
        _ => null,
    };

    public override void LoadView()
    {
        var (root, content) = PageParts.ScrollingPage();
        var totals = PageParts.Secondary("");
        var addFolder = NSButton.CreateButton("Add a folder of books…", () => { });
        _bindings.Command(addFolder, home.AddFolderCommand);
        _addFolder = addFolder;
        var start = NSStackView.FromViews([PageParts.Secondary("Add the folders where your books are. Bibliotaph reads them where they are and never changes them."), addFolder]);
        start.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        start.Alignment = NSLayoutAttribute.Leading;

        var preparing = new NSStackView { Orientation = NSUserInterfaceLayoutOrientation.Vertical, Alignment = NSLayoutAttribute.Leading, Spacing = 8 };
        var pinsTitle = PageParts.Section("Pinned");
        var pins = PageParts.Wrap();
        var openedTitle = PageParts.Section("Recently opened");
        var opened = PageParts.Wrap();
        var addedTitle = PageParts.Section("Recently added");
        var added = PageParts.Wrap();

        foreach (var view in new NSView[] { PageParts.Heading("Home"), totals, start, preparing, pinsTitle, pins, openedTitle, opened, addedTitle, added })
            content.AddArrangedSubview(view);
        foreach (var grid in new[] { pins, opened, added }) grid.WidthAnchor.ConstraintEqualTo(content.WidthAnchor, 1, -48).Active = true;
        _shelves.AddRange([opened, added, pins]);
        View = root;

        _bindings.Properties(home, [nameof(HomeViewModel.TotalsText), nameof(HomeViewModel.HasBooks)], () =>
        {
            totals.StringValue = home.TotalsText;
            start.Hidden = home.HasBooks;
        });
        _bindings.Property(home, nameof(HomeViewModel.ContinuePreparing), () =>
        {
            foreach (var view in preparing.ArrangedSubviews) view.RemoveFromSuperview();
            preparing.Hidden = home.ContinuePreparing is null;
            if (home.ContinuePreparing is not { } card) return;
            preparing.AddArrangedSubview(PageParts.Section("Continue preparing"));
            preparing.AddArrangedSubview(new ShelfCard(card.Title, $"{card.DateLabel} · {card.CountLabel}", card.Covers, covers,
                () => home.OpenSessionCommand.Execute(card)));
        });
        void Pins()
        {
            // A pinned book shows its cover; Favorites, a collection, a binder or a saved search is a button by name.
            pins.SetItems(home.Pins.Items.Select(pin => pin.Book is not null
                ? (NSView)new BookCard(pin.Label, pin.Book, covers, () => home.OpenPinCommand.Execute(pin))
                : PinButton(pin)));
            pinsTitle.Hidden = home.Pins.Items.Count == 0;
        }
        _bindings.Collection(home.Pins.Items, _ => Pins());
        Pins();
        Books(opened, openedTitle, home.RecentlyOpened);
        Books(added, addedTitle, home.RecentlyAdded);
    }

    NSButton PinButton(PinnedItemViewModel pin)
    {
        var symbol = pin.Target.Kind switch
        {
            Bibliotaph.Core.PinKind.SavedSearch => "magnifyingglass",
            Bibliotaph.Core.PinKind.Binder => "book.closed",
            Bibliotaph.Core.PinKind.Collection => "square.stack",
            Bibliotaph.Core.PinKind.Favorites => "heart",
            _ => "pin",
        };
        var button = NSButton.CreateButton(pin.Label, NSImage.GetSystemSymbol(symbol, pin.Label)!, () => home.OpenPinCommand.Execute(pin));
        button.ImagePosition = NSCellImagePosition.ImageLeading;
        return button;
    }

    void Books(WrapView grid, NSView title, System.Collections.ObjectModel.ObservableCollection<LibraryItemViewModel> books)
    {
        void Show()
        {
            grid.SetItems(books.Select(book => (NSView)new BookCard(book.Title, book, covers, () =>
            {
                if (home.OpenBookCommand.CanExecute(book)) home.OpenBookCommand.Execute(book);
            })));
            title.Hidden = books.Count == 0;
        }
        _bindings.Collection(books, _ => Show());
        Show();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _bindings.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>A book's cover and title, smaller than the Library's, which opens it with a click.</summary>
sealed class BookCard : NSView
{
    readonly Action _open;

    public BookCard(string title, LibraryItemViewModel? book, MacCovers covers, Action open)
    {
        _open = open;
        var cover = new NSImageView { ImageScaling = NSImageScale.ProportionallyUpOrDown, WantsLayer = true };
        cover.Layer!.CornerRadius = 5;
        cover.Layer.MasksToBounds = true;
        cover.Layer.BackgroundColor = NSColor.QuaternarySystemFill.CGColor;
        if (book?.CoverName is { } name)
        {
            cover.Image = covers.TryGet(name)!;
            if (cover.Image is null) _ = LoadAsync(cover, name, covers);
        }
        var label = NSTextField.CreateWrappingLabel(title);
        label.Font = NSFont.SystemFontOfSize(11, NSFontWeight.Medium)!;
        label.MaximumNumberOfLines = 2;
        label.PreferredMaxLayoutWidth = 112;
        foreach (var view in new NSView[] { cover, label })
        {
            view.TranslatesAutoresizingMaskIntoConstraints = false;
            AddSubview(view);
        }
        NSLayoutConstraint.ActivateConstraints(
        [
            WidthAnchor.ConstraintEqualTo(112),
            cover.TopAnchor.ConstraintEqualTo(TopAnchor),
            cover.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            cover.WidthAnchor.ConstraintEqualTo(112),
            cover.HeightAnchor.ConstraintEqualTo(150),
            label.TopAnchor.ConstraintEqualTo(cover.BottomAnchor, 6),
            label.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            label.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            label.BottomAnchor.ConstraintEqualTo(BottomAnchor),
        ]);
        ToolTip = title;
    }

    static async Task LoadAsync(NSImageView view, string name, MacCovers covers)
    {
        var image = await covers.LoadAsync(name);
        Bindings.OnMain(() => view.Image = image!);
    }

    public override void MouseUp(NSEvent theEvent)
    {
        if (Bounds.Contains(ConvertPointFromView(theEvent.LocationInWindow, null))) _open();
    }

    public override void ResetCursorRects() => AddCursorRect(Bounds, NSCursor.PointingHandCursor);
}
