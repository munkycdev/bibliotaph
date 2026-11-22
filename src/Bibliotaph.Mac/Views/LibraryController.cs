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
    BookGrid _grid = null!;
    BookList _list = null!;
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
        _grid = new BookGrid(this)
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

        var bar = FilterBar();
        _list = new BookList(this);
        var listScroll = new NSScrollView { DocumentView = _list, HasVerticalScroller = true, DrawsBackground = false };

        var root = new NSView();
        foreach (var view in new NSView[] { heading, count, bar, issue, scroll, listScroll, empty })
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
            bar.TopAnchor.ConstraintEqualTo(heading.BottomAnchor, 8),
            bar.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor, 24),
            bar.TrailingAnchor.ConstraintLessThanOrEqualTo(root.TrailingAnchor, -24),
            issue.TopAnchor.ConstraintEqualTo(bar.BottomAnchor, 6),
            issue.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor, 24),
            issue.TrailingAnchor.ConstraintLessThanOrEqualTo(root.TrailingAnchor, -24),
            scroll.TopAnchor.ConstraintEqualTo(issue.BottomAnchor, 8),
            scroll.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            scroll.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            scroll.BottomAnchor.ConstraintEqualTo(root.BottomAnchor),
            listScroll.TopAnchor.ConstraintEqualTo(scroll.TopAnchor),
            listScroll.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor, 16),
            listScroll.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor, -16),
            listScroll.BottomAnchor.ConstraintEqualTo(root.BottomAnchor),
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
            nameof(LibraryViewModel.EmptyActionText), nameof(LibraryViewModel.Layout)], () =>
        {
            empty.Hidden = !library.IsEmpty;
            scroll.Hidden = library.IsEmpty || library.Layout == Bibliotaph.Index.LibraryLayout.List;
            listScroll.Hidden = library.IsEmpty || library.Layout != Bibliotaph.Index.LibraryLayout.List;
            emptyTitle.StringValue = library.EmptyTitle;
            emptyMessage.StringValue = library.EmptyMessage;
            emptyAction.Title = library.EmptyActionText ?? "";
            emptyAction.Hidden = library.EmptyActionText is null || library.EmptyCommand is null;
        });
        emptyAction.Activated += (_, _) =>
        {
            if (library.EmptyCommand is { } command && command.CanExecute(null)) command.Execute(null);
        };

        DialogSheet.Follow(_bindings, library.Collections, () => View.Window);
        DialogSheet.Follow(_bindings, library.Sessions, () => View.Window);

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

    void Reload()
    {
        _grid.ReloadData();
        _list.ReloadData();
    }

    /// <summary>
    /// Sort, the filters most used (kind, game system, type, folder), Clear, and grid or list: what the Windows
    /// Library's bar offers first, each the page's own choice.
    /// </summary>
    NSStackView FilterBar()
    {
        var filters = library.Filters;
        var sort = ChoicePopup.Create(_bindings, library, nameof(LibraryViewModel.SortChoices), () => library.SortChoices, () => library.SortChoice, c => library.SortChoice = c);
        _bindings.Property(library, nameof(LibraryViewModel.SortChoice), () => sort.SelectItem(IndexOf(library.SortChoices, library.SortChoice)));
        var kind = ChoicePopup.Create(_bindings, filters, nameof(LibraryFilterState.KindChoice), () => filters.KindChoices, () => filters.KindChoice, c => filters.KindChoice = c);
        var system = ChoicePopup.Create(_bindings, filters, nameof(LibraryFilterState.SystemChoice), () => filters.SystemChoices, () => filters.SystemChoice, c => filters.SystemChoice = c);
        var type = ChoicePopup.Create(_bindings, filters, nameof(LibraryFilterState.TypeChoice), () => filters.TypeChoices, () => filters.TypeChoice, c => filters.TypeChoice = c);
        var folder = ChoicePopup.Create(_bindings, filters, nameof(LibraryFilterState.FolderChoice), () => filters.FolderChoices, () => filters.FolderChoice, c => filters.FolderChoice = c);
        var clear = NSButton.CreateButton("Clear filters", () => filters.ClearFiltersCommand.Execute(null));
        clear.ControlSize = NSControlSize.Small;
        clear.BezelStyle = NSBezelStyle.Inline;
        _bindings.Property(filters, nameof(LibraryFilterState.IsFiltering), () => clear.Hidden = !filters.IsFiltering);
        filters.Changed += OnFiltersChanged;
        _bindings.Add(() => filters.Changed -= OnFiltersChanged);
        void OnFiltersChanged(object? sender, EventArgs e) => Bindings.OnMain(() => clear.Hidden = !filters.IsFiltering);
        var layout = NSSegmentedControl.FromImages(
            [NSImage.GetSystemSymbol("square.grid.2x2", "Grid")!, NSImage.GetSystemSymbol("list.bullet", "List")!], NSSegmentSwitchTracking.SelectOne, () => { });
        layout.ControlSize = NSControlSize.Small;
        layout.Activated += (_, _) => library.Layout = layout.SelectedSegment == 1 ? Bibliotaph.Index.LibraryLayout.List : Bibliotaph.Index.LibraryLayout.Grid;
        _bindings.Property(library, nameof(LibraryViewModel.Layout), () => layout.SelectedSegment = library.Layout == Bibliotaph.Index.LibraryLayout.List ? 1 : 0);
        var bar = NSStackView.FromViews([sort, kind, system, type, folder, clear, layout]);
        bar.Spacing = 8;
        return bar;
    }

    static nint IndexOf<T>(IReadOnlyList<Choice<T>> list, Choice<T> choice)
    {
        for (var i = 0; i < list.Count; i++)
            if (Equals(list[i], choice)) return i;
        return -1;
    }

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

    /// <summary>A book's menu, as its card's on Windows: open it, its details, add it to a collection or a binder, and so on.</summary>
    internal NSMenu? MenuFor(LibraryItemViewModel item)
    {
        var menu = new NSMenu();
        void Add(string title, System.Windows.Input.ICommand command, object? parameter = null)
        {
            if (!command.CanExecute(parameter ?? item)) return;
            menu.AddItem(new NSMenuItem(title, (_, _) => command.Execute(parameter ?? item)));
        }
        Add("Open", library.OpenFromCardCommand);
        Add("Open in a new window", library.OpenBookInNewWindowCommand);
        Add("Show details", library.OpenDetailsCommand);
        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(Submenu("Add to collection", ((ICollectionChoices)library).MenuChoices
            .Select(choice => new NSMenuItem(choice.Label, (_, _) => library.AddToCollection(new CollectionRequest(item, choice))))));
        menu.AddItem(Submenu("Add to binder", ((ISessionChoices)library).MenuChoices
            .Select(choice => new NSMenuItem(choice.Label, (_, _) => library.AddToSession(new SessionRequest(item, choice))))));
        Add(item.FavoriteAction, library.ToggleFavoriteCommand);
        menu.AddItem(NSMenuItem.SeparatorItem);
        if (item.ShowOpenOnline) Add(item.OnlineAction, library.OpenOnlineCommand);
        if (item.CanOpenElsewhere) Add("Open in another app", library.OpenElsewhereCommand);
        Add("Remove from library…", library.RemoveFromLibraryCommand);
        if (menu.Count > 0 && menu.ItemAt(menu.Count - 1)!.IsSeparatorItem) menu.RemoveItemAt(menu.Count - 1);
        return menu;
    }

    static NSMenuItem Submenu(string title, IEnumerable<NSMenuItem> items)
    {
        var menu = new NSMenu(title);
        foreach (var item in items) menu.AddItem(item);
        return new NSMenuItem(title) { Submenu = menu, Enabled = menu.Count > 0 };
    }

    /// <summary>
    /// The Library as a list, as the Windows app's: a row a book with its byline, system, kind, levels and size, in the
    /// order the bar's sort gives. A click shows its details, a double-click opens it, and a right-click gives its menu.
    /// </summary>
    sealed class BookList : NSTableView
    {
        readonly LibraryController _owner;
        // A table holds its data source weakly.
        readonly ListSource _source;

        public BookList(LibraryController owner)
        {
            _owner = owner;
            UsesAlternatingRowBackgroundColors = true;
            RowHeight = 22;
            ColumnAutoresizingStyle = NSTableViewColumnAutoresizingStyle.FirstColumnOnly;
            foreach (var (id, title, width) in new[] { ("title", "Title", 320.0), ("byline", "By", 200.0), ("system", "System", 150.0),
                ("kind", "Kind", 130.0), ("levels", "Levels", 90.0), ("size", "Size", 170.0) })
                AddColumn(new NSTableColumn(id) { Title = title, Width = (nfloat)width });
            _source = new ListSource(this);
            DataSource = _source;
            Delegate = _source;
            DoubleAction = new ObjCRuntime.Selector("openRow:");
            Target = this;
        }

        public BookList(ObjCRuntime.NativeHandle handle) : base(handle)
        {
            _owner = null!;
            _source = null!;
        }

        internal LibraryController Owner => _owner;

        sealed class ListSource(BookList list) : NSTableViewDelegate, INSTableViewDataSource
        {
        [Export("numberOfRowsInTableView:")]
        public nint GetRowCount(NSTableView tableView) => list.Owner.Library.Items.Count;

        public override NSView GetViewForItem(NSTableView tableView, NSTableColumn tableColumn, nint row)
        {
            var item = list.Owner.Library.Items[(int)row];
            var text = tableColumn.Identifier switch
            {
                "title" => item.Title,
                "byline" => item.Byline,
                "system" => item.SystemLabel,
                "kind" => item.KindLabel,
                "levels" => item.LevelsLabel,
                _ => item.SizeLabel,
            };
            var label = NSTextField.CreateLabel(text);
            label.LineBreakMode = NSLineBreakMode.TruncatingTail;
            if (tableColumn.Identifier != "title") label.TextColor = NSColor.SecondaryLabel;
            if (item.IsAway) label.TextColor = NSColor.TertiaryLabel;
            return label;
        }

        public override void SelectionDidChange(NSNotification notification)
        {
            var library = list.Owner.Library;
            if (list.SelectedRow < 0 || list.SelectedRow >= library.Items.Count) return;
            var item = library.Items[(int)list.SelectedRow];
            if (library.ActivateCommand.CanExecute(item)) library.ActivateCommand.Execute(item);
        }
        }

        [Export("openRow:")]
        void OpenRow(NSObject sender)
        {
            if (ClickedRow < 0 || ClickedRow >= _owner.Library.Items.Count) return;
            var item = _owner.Library.Items[(int)ClickedRow];
            if (_owner.Library.OpenFromCardCommand.CanExecute(item)) _owner.Library.OpenFromCardCommand.Execute(item);
        }

        public override NSMenu? MenuForEvent(NSEvent theEvent)
        {
            var row = GetRow(ConvertPointFromView(theEvent.LocationInWindow, null));
            return row >= 0 && row < _owner.Library.Items.Count ? _owner.MenuFor(_owner.Library.Items[(int)row]) : null;
        }
    }

    /// <summary>The grid, which gives a right-click on a cover that book's menu.</summary>
    sealed class BookGrid(LibraryController owner) : NSCollectionView
    {
        public override NSMenu? MenuForEvent(NSEvent theEvent)
        {
            var point = ConvertPointFromView(theEvent.LocationInWindow, null);
            if (GetIndexPath(point) is not { } path || (int)path.Item >= owner.Library.Items.Count) return null;
            return owner.MenuFor(owner.Library.Items[(int)path.Item]);
        }
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

        /// <summary>A click shows the book's details, as on Windows; a double-click opens it.</summary>
        public override void ItemsSelected(NSCollectionView collectionView, NSSet indexPaths)
        {
            if (indexPaths.AnyObject is not NSIndexPath path || (int)path.Item >= owner.Library.Items.Count) return;
            var item = owner.Library.Items[(int)path.Item];
            if (owner.Library.ActivateCommand.CanExecute(item)) owner.Library.ActivateCommand.Execute(item);
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
