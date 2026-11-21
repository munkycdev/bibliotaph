using AppKit;
using CoreGraphics;
using Foundation;
using ObjCRuntime;

namespace Bibliotaph.MacSpike;

/// <summary>The cover grid, or, until this Mac is connected, a panel that connects it.</summary>
sealed class LibraryController(LibraryViewModel library, Covers covers) : NSViewController
{
    LibraryViewModel Library => library;

    Covers Images => covers;

    NSCollectionView _grid = null!;
    NSScrollView _scroll = null!;
    GridSource? _source;

    public override void LoadView()
    {
        var root = new NSView();

        var layout = new NSCollectionViewFlowLayout
        {
            ItemSize = new CGSize(156, 262),
            MinimumInteritemSpacing = 18,
            MinimumLineSpacing = 22,
            SectionInset = new NSEdgeInsets(20, 24, 24, 24),
        };
        _grid = new NSCollectionView
        {
            CollectionViewLayout = layout,
            Selectable = true,
            AllowsMultipleSelection = false,
            BackgroundColors = [NSColor.Clear],
        };
        _grid.RegisterClassForItem(typeof(CoverItem), CoverItem.Id);
        _source = new GridSource(this);
        _grid.DataSource = _source;
        _grid.Delegate = _source;
        _scroll = new NSScrollView { DocumentView = _grid, HasVerticalScroller = true, DrawsBackground = false };

        var connect = ConnectPanel();
        foreach (var view in new NSView[] { _scroll, connect })
        {
            view.TranslatesAutoresizingMaskIntoConstraints = false;
            root.AddSubview(view);
        }
        NSLayoutConstraint.ActivateConstraints(
        [
            _scroll.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            _scroll.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            _scroll.TopAnchor.ConstraintEqualTo(root.TopAnchor),
            _scroll.BottomAnchor.ConstraintEqualTo(root.BottomAnchor),
            connect.CenterXAnchor.ConstraintEqualTo(root.CenterXAnchor),
            connect.CenterYAnchor.ConstraintEqualTo(root.CenterYAnchor),
            connect.WidthAnchor.ConstraintLessThanOrEqualTo(420),
        ]);
        View = root;

        Ui.Watch(library, nameof(LibraryViewModel.Books), () =>
        {
            _grid.ReloadData();
            _grid.SelectionIndexPaths = new NSSet<NSIndexPath>();
        });
        Ui.Watch(library, nameof(LibraryViewModel.State), () =>
        {
            var showsGrid = library.State is LibraryState.Ready or LibraryState.Loading;
            _scroll.Hidden = !showsGrid;
            connect.Hidden = showsGrid;
        });
    }

    NSStackView ConnectPanel()
    {
        var title = NSTextField.CreateLabel("Connect this Mac to bibliotaph.online");
        title.Font = NSFont.SystemFontOfSize(20, NSFontWeight.Semibold);
        var body = NSTextField.CreateWrappingLabel(
            "Your library comes from your web library, as it does on a connected PC. Approve this Mac in the browser page that opens.");
        body.TextColor = NSColor.SecondaryLabel;
        body.Alignment = NSTextAlignment.Center;
        var code = NSTextField.CreateLabel("");
        code.Font = NSFont.MonospacedSystemFont(28, NSFontWeight.Medium);
        var problem = NSTextField.CreateWrappingLabel("");
        problem.TextColor = NSColor.SystemRed;
        var button = NSButton.CreateButton("Connect…", () => _ = library.ConnectAsync());
        button.BezelStyle = NSBezelStyle.Push;
        button.KeyEquivalent = "\r";
        var spinner = new NSProgressIndicator { Style = NSProgressIndicatorStyle.Spinning, ControlSize = NSControlSize.Small };

        var stack = NSStackView.FromViews([title, body, code, spinner, button, problem]);
        stack.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        stack.Alignment = NSLayoutAttribute.CenterX;
        stack.Spacing = 14;

        Ui.Watch(library, nameof(LibraryViewModel.ConnectCode), () =>
        {
            code.StringValue = library.ConnectCode ?? "";
            code.Hidden = library.ConnectCode is null;
        });
        Ui.Watch(library, nameof(LibraryViewModel.Problem), () =>
        {
            problem.StringValue = library.Problem ?? "";
            problem.Hidden = library.Problem is null;
        });
        Ui.Watch(library, nameof(LibraryViewModel.State), () =>
        {
            var busy = library.State is LibraryState.Connecting or LibraryState.Starting;
            button.Enabled = !busy;
            spinner.Hidden = !busy;
            if (busy) spinner.StartAnimation(null);
            else spinner.StopAnimation(null);
        });
        return stack;
    }

    sealed class GridSource(LibraryController owner) : NSCollectionViewDelegate, INSCollectionViewDataSource
    {
        [Export("collectionView:numberOfItemsInSection:")]
        public nint GetNumberofItems(NSCollectionView collectionView, nint section) => owner.Library.Books.Count;

        [Export("collectionView:itemForRepresentedObjectAtIndexPath:")]
        public NSCollectionViewItem GetItem(NSCollectionView collectionView, NSIndexPath indexPath)
        {
            var item = (CoverItem)collectionView.MakeItem(CoverItem.Id, indexPath);
            item.Show(owner.Library.Books[(int)indexPath.Item], owner.Images);
            return item;
        }

        public override void ItemsSelected(NSCollectionView collectionView, NSSet indexPaths)
        {
            var path = indexPaths.AnyObject as NSIndexPath;
            if (path is not null && (int)path.Item < owner.Library.Books.Count) owner.Library.SelectedBook = owner.Library.Books[(int)path.Item];
        }
    }
}

/// <summary>A cover with its title and kind under it; reused as the grid scrolls, so a late cover checks it's still wanted.</summary>
[Register(nameof(CoverItem))]
sealed class CoverItem : NSCollectionViewItem
{
    public const string Id = "cover";

    NSImageView _cover = null!;
    NSView _frame = null!;
    NSTextField _title = null!;
    NSTextField _meta = null!;
    string? _hash;

    public CoverItem()
    {
    }

    public CoverItem(NativeHandle handle) : base(handle)
    {
    }

    public override void LoadView()
    {
        _frame = new NSView { WantsLayer = true };
        _frame.Layer!.CornerRadius = 6;
        _frame.Layer.MasksToBounds = true;
        _frame.Layer.BackgroundColor = NSColor.QuaternarySystemFill.CGColor;
        _frame.Layer.BorderColor = NSColor.ControlAccent.CGColor;
        _cover = new NSImageView { ImageScaling = NSImageScale.ProportionallyUpOrDown };
        _title = NSTextField.CreateLabel("");
        _title.Font = NSFont.SystemFontOfSize(12, NSFontWeight.Semibold);
        _title.MaximumNumberOfLines = 2;
        _title.LineBreakMode = NSLineBreakMode.TruncatingTail;
        _title.PreferredMaxLayoutWidth = 156;
        _meta = NSTextField.CreateLabel("");
        _meta.Font = NSFont.SystemFontOfSize(11);
        _meta.TextColor = NSColor.SecondaryLabel;
        _meta.LineBreakMode = NSLineBreakMode.TruncatingTail;

        var root = new NSView();
        foreach (var view in new NSView[] { _frame, _cover, _title, _meta })
        {
            view.TranslatesAutoresizingMaskIntoConstraints = false;
            (view == _cover ? _frame : root).AddSubview(view);
        }
        NSLayoutConstraint.ActivateConstraints(
        [
            _frame.TopAnchor.ConstraintEqualTo(root.TopAnchor),
            _frame.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            _frame.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            _frame.HeightAnchor.ConstraintEqualTo(208),
            _cover.LeadingAnchor.ConstraintEqualTo(_frame.LeadingAnchor),
            _cover.TrailingAnchor.ConstraintEqualTo(_frame.TrailingAnchor),
            _cover.TopAnchor.ConstraintEqualTo(_frame.TopAnchor),
            _cover.BottomAnchor.ConstraintEqualTo(_frame.BottomAnchor),
            _title.TopAnchor.ConstraintEqualTo(_frame.BottomAnchor, 8),
            _title.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            _title.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            _meta.TopAnchor.ConstraintEqualTo(_title.BottomAnchor, 2),
            _meta.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            _meta.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
        ]);
        View = root;
    }

    public void Show(BookCard book, Covers covers)
    {
        _hash = book.ContentHash;
        _title.StringValue = book.Title;
        _meta.StringValue = $"{book.Kind} · {book.FolderName}";
        _cover.Image = covers.Cached(book.ContentHash)!;
        if (_cover.Image is null) _ = LoadAsync(book.ContentHash, covers);
    }

    async Task LoadAsync(string hash, Covers covers)
    {
        var image = await covers.GetAsync(hash);
        Ui.OnMain(() =>
        {
            if (_hash == hash) _cover.Image = image!;
        });
    }

    public override bool Selected
    {
        get => base.Selected;
        set
        {
            base.Selected = value;
            if (_frame?.Layer is { } layer) layer.BorderWidth = value ? 3 : 0;
        }
    }
}
