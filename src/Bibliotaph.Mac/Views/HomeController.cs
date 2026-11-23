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
public sealed class HomeController(HomeViewModel home, MacCovers covers) : NSViewController, ITourAnchors, IWindowTitle
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

    /// <summary>Home, with what the library holds under it.</summary>
    public void FollowTitle(Bindings bindings, Action<string, string> show) =>
        bindings.Property(home, nameof(HomeViewModel.TotalsText), () => show("Home", home.TotalsText));

    /// <summary>A card that offers something once, as Windows' Home does, with what to do about it.</summary>
    static Tinted Offer(string heading, string text, NSView[] buttons)
    {
        var title = NSTextField.CreateWrappingLabel(heading);
        title.Font = NSFont.SystemFontOfSize(14, NSFontWeight.Semibold)!;
        var body = PageParts.Secondary(text);
        body.WidthAnchor.ConstraintLessThanOrEqualTo(640).Active = true;
        var row = NSStackView.FromViews(buttons);
        row.Spacing = 8;
        var column = NSStackView.FromViews([title, body, row]);
        column.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        column.Alignment = NSLayoutAttribute.Leading;
        column.Spacing = 8;
        return new Tinted(column, Palette.Panel, 10, new NSEdgeInsets(14, 16, 14, 16));
    }

    /// <summary>A shelf's heading, with the way to all of it at the trailing end.</summary>
    (NSStackView Line, NSButton Link) SectionLine(string title, string link, System.Windows.Input.ICommand command)
    {
        var button = Link(link, "arrow.right", command);
        button.ImagePosition = NSCellImagePosition.ImageTrailing;
        var line = NSStackView.FromViews([PageParts.Section(title), new NSView(), button]);
        line.WidthAnchor.ConstraintGreaterThanOrEqualTo(480).Active = true;
        return (line, button);
    }

    NSButton Link(string title, string symbol, System.Windows.Input.ICommand command)
    {
        var button = NSButton.CreateButton(title, () => { });
        _bindings.Command(button, command);
        button.Bordered = false;
        button.ContentTintColor = NSColor.ControlAccent;
        button.Font = NSFont.SystemFontOfSize(12)!;
        button.Image = NSImage.GetSystemSymbol(symbol, null)!;
        button.ImagePosition = NSCellImagePosition.ImageLeading;
        return button;
    }

    NSButton Button(string title, System.Windows.Input.ICommand command, bool primary = false)
    {
        var button = NSButton.CreateButton(title, () => { });
        _bindings.Command(button, command);
        if (primary) button.TintProminence = NSTintProminence.Primary;
        return button;
    }

    public override void LoadView()
    {
        var (root, content) = PageParts.ScrollingPage();
        var addFolder = NSButton.CreateButton("Add a folder of books…", () => { });
        _bindings.Command(addFolder, home.AddFolderCommand);
        _addFolder = addFolder;
        var start = NSStackView.FromViews([PageParts.Secondary("Add the folders where your books are. Bibliotaph reads them where they are and never changes them."), addFolder]);
        start.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        start.Alignment = NSLayoutAttribute.Leading;

        // Folders added but nothing read yet: what's happening, and the folders.
        var indexing = NSStackView.FromViews([PageParts.Section("Indexing your library."),
            PageParts.Secondary("Your books show here as Bibliotaph reads them. It works only while the app is open."), Button("Manage Folders…", home.ManageFoldersCommand)]);
        indexing.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        indexing.Alignment = NSLayoutAttribute.Leading;
        // The library's size, and the way to the Summary page that says more, as Windows has under its welcome.
        var totals = PageParts.Secondary("");
        var summary = Link(Bibliotaph.Core.SummaryText.SeeIt, "chart.bar", home.OpenSummaryCommand);
        var totalsLine = NSStackView.FromViews([totals, summary]);
        totalsLine.Spacing = 8;

        var preparing = new NSStackView { Orientation = NSUserInterfaceLayoutOrientation.Vertical, Alignment = NSLayoutAttribute.Leading, Spacing = 8 };
        // Each shelf's heading has the way to the whole of it at its end, as on Windows.
        var (pinsTitle, _) = SectionLine("Pinned", "All collections", home.BrowseCollectionsCommand);
        var pins = PageParts.Wrap();
        var (openedTitle, _) = SectionLine("Recently opened", "Browse your library", home.BrowseLibraryCommand);
        var opened = PageParts.Wrap();
        var (addedTitle, addedBrowse) = SectionLine("Recently added", "Browse your library", home.BrowseLibraryCommand);
        var added = PageParts.Wrap();

        var ai = Offer("Fill in the catalog with AI?",
            "An AI model running on this Mac can suggest each book's system, type, levels and more, quoting the page it found them on. Bibliotaph can download one and run it itself, or use one you already run, such as Ollama or LM Studio. You review the suggestions. It is off until you set it up, and you can do that later in Settings.",
            [Button("Set Up AI…", home.SetUpAiCommand, primary: true), Button("Not Now", home.AiLaterCommand)]);
        var coversText = PageParts.Secondary("");
        var coversButtons = new NSStackView { Spacing = 8 };
        var coversHeading = NSTextField.CreateWrappingLabel("");
        coversHeading.Font = NSFont.SystemFontOfSize(14, NSFontWeight.Semibold)!;
        var coversColumn = NSStackView.FromViews([coversHeading, coversText, coversButtons]);
        coversColumn.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        coversColumn.Alignment = NSLayoutAttribute.Leading;
        coversColumn.Spacing = 8;
        var coversCard = new Tinted(coversColumn, Palette.Panel, 10, new NSEdgeInsets(14, 16, 14, 16));

        foreach (var view in new NSView[] { start, indexing, totalsLine, ai, coversCard, preparing, pinsTitle, pins, openedTitle, opened, addedTitle, added })
            content.AddArrangedSubview(view);
        foreach (var card in new NSView[] { ai, coversCard }) card.WidthAnchor.ConstraintLessThanOrEqualTo(content.WidthAnchor, 1, -48).Active = true;
        foreach (var text in new NSView[] { coversText, coversHeading }) text.WidthAnchor.ConstraintLessThanOrEqualTo(640).Active = true;
        _bindings.Property(home, nameof(HomeViewModel.ShowAiStep), () => ai.Hidden = !home.ShowAiStep);
        _bindings.Properties(home, [nameof(HomeViewModel.MissingCoversHeading), nameof(HomeViewModel.MissingCoversHow), nameof(HomeViewModel.MissingCoversButtons)], () =>
        {
            coversCard.Hidden = home.MissingCoversHeading is null;
            coversHeading.StringValue = home.MissingCoversHeading ?? "";
            coversText.StringValue = home.MissingCoversHow;
            foreach (var view in coversButtons.ArrangedSubviews) view.RemoveFromSuperview();
            foreach (var store in home.MissingCoversButtons)
                coversButtons.AddArrangedSubview(NSButton.CreateButton(store.Label, () => home.OpenMissingCoversCommand.Execute(store)));
            coversButtons.AddArrangedSubview(Button("Not Now", home.CoversLaterCommand));
        });
        foreach (var grid in new[] { pins, opened, added }) grid.WidthAnchor.ConstraintEqualTo(content.WidthAnchor, 1, -48).Active = true;
        _shelves.AddRange([opened, added, pins]);
        View = root;

        _bindings.Properties(home, [nameof(HomeViewModel.TotalsText), nameof(HomeViewModel.HasBooks), nameof(HomeViewModel.HasFolders),
            nameof(HomeViewModel.HasRecentlyOpened)], () =>
        {
            start.Hidden = home.HasBooks || home.HasFolders;
            indexing.Hidden = home.HasBooks || !home.HasFolders;
            totals.StringValue = home.TotalsText;
            totalsLine.Hidden = !home.HasBooks;
            // One way to the library is enough: under Recently opened when there is one.
            addedBrowse.Hidden = home.HasRecentlyOpened;
        });
        _bindings.Property(home, nameof(HomeViewModel.ContinuePreparing), () =>
        {
            foreach (var view in preparing.ArrangedSubviews) view.RemoveFromSuperview();
            preparing.Hidden = home.ContinuePreparing is null;
            if (home.ContinuePreparing is not { } card) return;
            preparing.AddArrangedSubview(SectionLine("Continue preparing", "All binders", home.BrowseSessionsCommand).Line);
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
