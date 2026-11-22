using AppKit;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Presentation.ViewModels;
using CoreGraphics;
using Foundation;
using ObjCRuntime;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// The Library: its heading and count, then the cover grid, or what to do when it's empty. A double-click opens a
/// book, as on Windows.
/// </summary>
public sealed class LibraryController(LibraryViewModel library, MacCovers covers) : NSViewController
{
    readonly Bindings _bindings = new();
    NSCollectionView _grid = null!;
    GridSource? _source;

    internal LibraryViewModel Library => library;

    internal MacCovers Covers => covers;

    public override void LoadView()
    {
        var heading = NSTextField.CreateLabel("");
        heading.Font = NSFont.SystemFontOfSize(22, NSFontWeight.Bold)!;
        var count = NSTextField.CreateLabel("");
        count.TextColor = NSColor.SecondaryLabel;
        var issue = NSTextField.CreateWrappingLabel("");
        issue.TextColor = NSColor.SystemOrange;

        var layout = new NSCollectionViewFlowLayout
        {
            ItemSize = new CGSize(156, 262),
            MinimumInteritemSpacing = 18,
            MinimumLineSpacing = 22,
            SectionInset = new NSEdgeInsets(8, 24, 24, 24),
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
        var scroll = new NSScrollView { DocumentView = _grid, HasVerticalScroller = true, DrawsBackground = false };
        var doubleClick = new NSClickGestureRecognizer(OpenClicked) { NumberOfClicksRequired = 2, DelaysPrimaryMouseButtonEvents = false };
        _grid.AddGestureRecognizer(doubleClick);

        var emptyTitle = NSTextField.CreateLabel("");
        emptyTitle.Font = NSFont.SystemFontOfSize(17, NSFontWeight.Semibold)!;
        var emptyMessage = NSTextField.CreateWrappingLabel("");
        emptyMessage.TextColor = NSColor.SecondaryLabel;
        emptyMessage.Alignment = NSTextAlignment.Center;
        var emptyAction = NSButton.CreateButton("", () => { });
        var empty = NSStackView.FromViews([emptyTitle, emptyMessage, emptyAction]);
        empty.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        empty.Spacing = 10;

        var root = new NSView();
        foreach (var view in new NSView[] { heading, count, issue, scroll, empty })
        {
            view.TranslatesAutoresizingMaskIntoConstraints = false;
            root.AddSubview(view);
        }
        NSLayoutConstraint.ActivateConstraints(
        [
            // Below the toolbar, which the full-size content view draws under.
            heading.TopAnchor.ConstraintEqualTo(root.SafeAreaLayoutGuide.TopAnchor, 16),
            heading.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor, 24),
            count.FirstBaselineAnchor.ConstraintEqualTo(heading.FirstBaselineAnchor),
            count.LeadingAnchor.ConstraintEqualTo(heading.TrailingAnchor, 10),
            count.TrailingAnchor.ConstraintLessThanOrEqualTo(root.TrailingAnchor, -24),
            issue.TopAnchor.ConstraintEqualTo(heading.BottomAnchor, 6),
            issue.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor, 24),
            issue.TrailingAnchor.ConstraintLessThanOrEqualTo(root.TrailingAnchor, -24),
            scroll.TopAnchor.ConstraintEqualTo(issue.BottomAnchor, 8),
            scroll.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            scroll.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            scroll.BottomAnchor.ConstraintEqualTo(root.BottomAnchor),
            empty.CenterXAnchor.ConstraintEqualTo(root.CenterXAnchor),
            empty.CenterYAnchor.ConstraintEqualTo(root.CenterYAnchor),
            empty.WidthAnchor.ConstraintLessThanOrEqualTo(440),
        ]);
        View = root;

        _bindings.Property(library, nameof(LibraryViewModel.Heading), () => heading.StringValue = library.Heading);
        _bindings.Properties(library, [nameof(LibraryViewModel.CountText), nameof(LibraryViewModel.CountLabel)],
            () => count.StringValue = library.CountText.Length == 0 ? "" : $"{library.CountText} {library.CountLabel}");
        _bindings.Property(library, nameof(LibraryViewModel.IssueText), () =>
        {
            issue.StringValue = library.IssueText ?? "";
            issue.Hidden = library.IssueText is null;
        });
        _bindings.Properties(library, [nameof(LibraryViewModel.IsEmpty), nameof(LibraryViewModel.EmptyTitle), nameof(LibraryViewModel.EmptyMessage),
            nameof(LibraryViewModel.EmptyActionText)], () =>
        {
            empty.Hidden = !library.IsEmpty;
            scroll.Hidden = library.IsEmpty;
            emptyTitle.StringValue = library.EmptyTitle;
            emptyMessage.StringValue = library.EmptyMessage;
            emptyAction.Title = library.EmptyActionText ?? "";
            emptyAction.Hidden = library.EmptyActionText is null || library.EmptyCommand is null;
        });
        emptyAction.Activated += (_, _) =>
        {
            if (library.EmptyCommand is { } command && command.CanExecute(null)) command.Execute(null);
        };

        // The page replaces its list as it refreshes, and changes it in place as books change.
        var items = library.Items;
        _bindings.Property(library, nameof(LibraryViewModel.Items), () =>
        {
            if (!ReferenceEquals(items, library.Items)) Follow(library.Items);
            Reload();
        });
        Follow(items);

        void Follow(System.Collections.ObjectModel.ObservableCollection<LibraryItemViewModel> list)
        {
            items = list;
            _bindings.Collection(list, _ =>
            {
                if (ReferenceEquals(list, library.Items)) Reload();
            });
        }
    }

    void Reload() => _grid.ReloadData();

    void OpenClicked(NSClickGestureRecognizer recognizer)
    {
        var point = recognizer.LocationInView(_grid);
        if (_grid.GetIndexPath(point) is not { } path || (int)path.Item >= library.Items.Count) return;
        var item = library.Items[(int)path.Item];
        if (library.OpenFromCardCommand.CanExecute(item)) library.OpenFromCardCommand.Execute(item);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _bindings.Dispose();
        base.Dispose(disposing);
    }

    sealed class GridSource(LibraryController owner) : NSCollectionViewDelegate, INSCollectionViewDataSource
    {
        [Export("collectionView:numberOfItemsInSection:")]
        public nint GetNumberofItems(NSCollectionView collectionView, nint section) => owner.Library.Items.Count;

        [Export("collectionView:itemForRepresentedObjectAtIndexPath:")]
        public NSCollectionViewItem GetItem(NSCollectionView collectionView, NSIndexPath indexPath)
        {
            var item = (CoverItem)collectionView.MakeItem(CoverItem.Id, indexPath);
            item.Show(owner.Library.Items[(int)indexPath.Item], owner.Covers);
            return item;
        }
    }
}

/// <summary>A cover with its title and what it is under it; reused as the grid scrolls, so a late cover checks it's still wanted.</summary>
[Register(nameof(CoverItem))]
sealed class CoverItem : NSCollectionViewItem
{
    public const string Id = "cover";

    NSImageView _cover = null!;
    NSView _frame = null!;
    NSTextField _initial = null!;
    NSTextField _title = null!;
    NSTextField _meta = null!;
    string? _coverName;

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
        _initial = NSTextField.CreateLabel("");
        _initial.Font = NSFont.SystemFontOfSize(44, NSFontWeight.Light)!;
        _initial.TextColor = NSColor.TertiaryLabel;
        _title = NSTextField.CreateLabel("");
        _title.Font = NSFont.SystemFontOfSize(12, NSFontWeight.Semibold)!;
        _title.MaximumNumberOfLines = 2;
        _title.LineBreakMode = NSLineBreakMode.TruncatingTail;
        _title.PreferredMaxLayoutWidth = 156;
        _meta = NSTextField.CreateLabel("");
        _meta.Font = NSFont.SystemFontOfSize(11)!;
        _meta.TextColor = NSColor.SecondaryLabel;
        _meta.LineBreakMode = NSLineBreakMode.TruncatingTail;

        var root = new NSView();
        foreach (var view in new NSView[] { _frame, _initial, _cover, _title, _meta })
        {
            view.TranslatesAutoresizingMaskIntoConstraints = false;
            (view == _cover || view == _initial ? _frame : root).AddSubview(view);
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
            _initial.CenterXAnchor.ConstraintEqualTo(_frame.CenterXAnchor),
            _initial.CenterYAnchor.ConstraintEqualTo(_frame.CenterYAnchor),
            _title.TopAnchor.ConstraintEqualTo(_frame.BottomAnchor, 8),
            _title.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            _title.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            _meta.TopAnchor.ConstraintEqualTo(_title.BottomAnchor, 2),
            _meta.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            _meta.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
        ]);
        View = root;
    }

    public void Show(LibraryItemViewModel book, MacCovers covers)
    {
        _coverName = book.CoverName;
        _title.StringValue = book.Title;
        _meta.StringValue = book.Meta;
        _initial.StringValue = book.Initial;
        _cover.AlphaValue = book.IsAway ? 0.45f : 1;
        _cover.Image = (_coverName is null ? null : covers.TryGet(_coverName))!;
        _initial.Hidden = _cover.Image is not null;
        if (_coverName is { } name && _cover.Image is null) LoadAsync(name, covers).FireAndForget();
    }

    async Task LoadAsync(string name, MacCovers covers)
    {
        var image = await covers.LoadAsync(name);
        Bindings.OnMain(() =>
        {
            if (_coverName != name || image is null) return;
            _cover.Image = image;
            _initial.Hidden = true;
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
