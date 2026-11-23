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

        var bar = new NSStackView { Orientation = NSUserInterfaceLayoutOrientation.Vertical, Alignment = NSLayoutAttribute.Leading, Spacing = 6 };
        foreach (var part in new NSView[] { FilterBar(), OrganizeBar(), UndoLine(), SavedSearchLine() }) bar.AddArrangedSubview(part);
        var pageActions = PageActions();
        _list = new BookList(this);
        _list.ShowSort(library.SortChoice.Value);
        var listScroll = new NSScrollView { DocumentView = _list, HasVerticalScroller = true, DrawsBackground = false };

        var root = new NSView();
        foreach (var view in new NSView[] { heading, count, pageActions, bar, issue, scroll, listScroll, empty })
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
            count.TrailingAnchor.ConstraintLessThanOrEqualTo(pageActions.LeadingAnchor, -12),
            pageActions.CenterYAnchor.ConstraintEqualTo(heading.CenterYAnchor),
            pageActions.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor, -24),
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
        OrganizeSheets.Follow(_bindings, library.Selection, () => View.Window);
        NSAlert? naming = null;
        _bindings.Property(library.SmartViews, nameof(SmartViewsViewModel.ViewDialog), () =>
        {
            // A dialog closed by its view model rather than its buttons takes its sheet with it.
            if (naming?.Window.SheetParent is { } parent) parent.EndSheet(naming.Window);
            naming = library.SmartViews.ViewDialog is { } dialog && View.Window is { } host ? NameSheet(dialog, host) : null;
        });
        _bindings.Properties(library.Selection.Books, [nameof(BookSelection.IsActive), nameof(BookSelection.Count)], ShowTicks);
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

    internal bool Organizing => library.Selection.Books.IsActive;

    /// <summary>The ticks on the covers and rows shown, as Organize mode starts, ends, or a book is ticked.</summary>
    void ShowTicks()
    {
        if (_grid is null) return;
        if (Organizing) _grid.DeselectAll(null);
        foreach (var item in _grid.VisibleItems)
            if (item is CoverItem cover) cover.ShowTick(Organizing);
        _list.ShowTicks(Organizing);
    }

    /// <summary>
    /// A click on a book in Organize mode ticks it, and with Shift every book from the last one clicked, as on Windows;
    /// otherwise it shows the book's details.
    /// </summary>
    internal void Clicked(LibraryItemViewModel item)
    {
        var selection = library.Selection;
        var shift = NSApplication.SharedApplication.CurrentEvent?.ModifierFlags.HasFlag(NSEventModifierMask.ShiftKeyMask) == true;
        if (Organizing && shift && selection.SelectRangeCommand.CanExecute(item)) selection.SelectRangeCommand.Execute(item);
        else if (library.ActivateCommand.CanExecute(item)) library.ActivateCommand.Execute(item);
    }

    /// <summary>
    /// Organize mode's bar, as the Windows Library's: how many are ticked, what to do to them all, which cards to show
    /// while organizing, and Clear. Done takes Organize's place in the filter bar.
    /// </summary>
    NSStackView OrganizeBar()
    {
        var selection = library.Selection;
        var books = selection.Books;
        var summary = NSTextField.CreateLabel("");
        summary.Font = NSFont.SystemFontOfSize(12, NSFontWeight.Medium)!;
        var edit = Small("Edit metadata…", selection.EditSelectedCommand);
        var toCollection = Small("Add to collection", null);
        toCollection.Activated += (_, _) => Pop(toCollection, ((ICollectionChoices)library).MenuChoices.Select(choice =>
            new NSMenuItem(choice.Label, (_, _) => library.AddSelectedToCollectionCommand.Execute(new CollectionRequest(library, choice)))));
        var toBinder = Small("Add to binder", null);
        toBinder.Activated += (_, _) => Pop(toBinder, ((ISessionChoices)library).MenuChoices.Select(choice =>
            new NSMenuItem(choice.Label, (_, _) => library.AddSelectedToSessionCommand.Execute(new SessionRequest(library, choice)))));
        var join = Small("Same book…", selection.JoinSelectedCommand);
        var pack = Small("Pack together…", selection.PackSelectedCommand);
        var removeParts = Small("Remove adventures", selection.RemoveSelectedPartsCommand);
        var removeFromCollection = Small("Remove from collection", library.RemoveSelectedFromCollectionCommand);
        var export = Small("Export these…", selection.ExportSelectedCommand);
        var clear = Small("Clear", selection.ClearSelectionCommand);
        var actions = NSStackView.FromViews([summary, edit, toCollection, toBinder, join, pack, removeParts, removeFromCollection, export, clear]);
        actions.Spacing = 8;
        actions.SetCustomSpacing(16, summary);

        var links = ChoicePopup.Create(_bindings, selection, nameof(LibrarySelectionViewModel.LinkChoice), () => selection.LinkChoices,
            () => selection.LinkChoice, c => selection.LinkChoice = c);
        var keep = Small("", selection.KeepShownCommand);
        var organize = NSStackView.FromViews([NSTextField.CreateLabel("Show"), links, keep]);
        organize.Spacing = 8;

        var bar = new NSStackView { Orientation = NSUserInterfaceLayoutOrientation.Vertical, Alignment = NSLayoutAttribute.Leading, Spacing = 6 };
        bar.AddArrangedSubview(actions);
        bar.AddArrangedSubview(organize);
        _bindings.Properties(books, [nameof(BookSelection.IsActive), nameof(BookSelection.Summary), nameof(BookSelection.HasAny)], () =>
        {
            bar.Hidden = !books.IsActive;
            // With nothing ticked yet the bar says nothing: ticking books is as on any Mac, and needs no telling.
            summary.StringValue = books.HasAny ? books.Summary : "";
            summary.Hidden = !books.HasAny;
            toCollection.Enabled = toBinder.Enabled = books.HasAny;
        });
        _bindings.Properties(selection, [nameof(LibrarySelectionViewModel.CanJoinSelected), nameof(LibrarySelectionViewModel.CanPackSelected),
            nameof(LibrarySelectionViewModel.CanRemoveSelectedParts), nameof(LibrarySelectionViewModel.ShowsSuggested), nameof(LibrarySelectionViewModel.KeepLabel)], () =>
        {
            join.Hidden = !selection.CanJoinSelected;
            pack.Hidden = !selection.CanPackSelected;
            removeParts.Hidden = !selection.CanRemoveSelectedParts;
            keep.Hidden = !selection.ShowsSuggested;
            keep.Title = selection.KeepLabel;
        });
        _bindings.Property(library, nameof(LibraryViewModel.IsCollection), () => removeFromCollection.Hidden = !library.IsCollection);
        return bar;
    }

    /// <summary>
    /// The page's own actions beside its heading: Pin, as a symbol that shows whether it is pinned, and a More menu
    /// with a collection's New collection, Rename, Move to and Delete, or a saved search's Rename and Delete.
    /// </summary>
    NSStackView PageActions()
    {
        var views = library.SmartViews;
        var pin = NSButton.CreateButton(NSImage.GetSystemSymbol("pin", "Pin")!, () => library.TogglePinCommand.Execute(null));
        pin.BezelStyle = NSBezelStyle.Toolbar;
        var more = NSButton.CreateButton(NSImage.GetSystemSymbol("ellipsis.circle", "More")!, () => { });
        more.BezelStyle = NSBezelStyle.Toolbar;
        more.ToolTip = "More";
        more.Activated += (_, _) =>
        {
            var items = new List<NSMenuItem>();
            void Add(string title, System.Windows.Input.ICommand command)
            {
                // What can't be done here is left out rather than dimmed, as the Mac's menus do.
                if (command.CanExecute(null)) items.Add(new NSMenuItem(title, (_, _) => command.Execute(null)));
            }
            if (library.ShowsCollectionActions)
            {
                Add("New Collection Inside…", library.NewSubCollectionCommand);
                Add("Rename…", library.RenameCollectionCommand);
                Add("Move To…", library.MoveCollectionCommand);
                items.Add(NSMenuItem.SeparatorItem);
                Add("Delete Collection", library.DeleteCollectionCommand);
            }
            else if (views.IsView)
            {
                Add("Rename…", views.RenameViewCommand);
                items.Add(NSMenuItem.SeparatorItem);
                Add("Delete Saved Search", views.DeleteViewCommand);
            }
            Pop(more, items);
        };
        var actions = NSStackView.FromViews([pin, more]);
        actions.Spacing = 4;
        void Show()
        {
            var collection = library.ShowsCollectionActions;
            pin.Hidden = !(collection || views.IsView || library.IsFavoritesPage);
            more.Hidden = !(collection || views.IsView);
            // Filled while pinned; its tip says what pressing it does.
            var pinned = library.PinLabel != "Pin";
            pin.Image = NSImage.GetSystemSymbol(pinned ? "pin.fill" : "pin", library.PinLabel)!;
            pin.ToolTip = library.PinLabel;
            pin.ContentTintColor = pinned ? NSColor.ControlAccent : null!;
        }
        _bindings.Properties(library, [nameof(LibraryViewModel.ShowsCollectionActions), nameof(LibraryViewModel.IsFavoritesPage),
            nameof(LibraryViewModel.PinLabel), nameof(LibraryViewModel.Heading)], Show);
        _bindings.Property(views, nameof(SmartViewsViewModel.IsView), Show);
        return actions;
    }

    /// <summary>
    /// A saved search changed since it was saved, with Update and Save as new; a note about it; and what the last change
    /// to the saved searches did, with its Undo.
    /// </summary>
    NSStackView SavedSearchLine()
    {
        var views = library.SmartViews;
        var changed = NSTextField.CreateLabel("This saved search has changed.");
        changed.Font = NSFont.SystemFontOfSize(12)!;
        var update = Small("Update saved search", views.UpdateViewCommand);
        var saveNew = Small("Save as new…", views.SaveViewAsNewCommand);
        var note = NSTextField.CreateWrappingLabel("");
        note.Font = NSFont.SystemFontOfSize(12)!;
        note.TextColor = NSColor.SecondaryLabel;
        var message = NSTextField.CreateLabel("");
        message.Font = NSFont.SystemFontOfSize(12)!;
        var undo = Small("Undo", views.ViewUndo.UndoCommand);
        var line = NSStackView.FromViews([changed, update, saveNew, note, message, undo]);
        line.Spacing = 8;
        void Show()
        {
            changed.Hidden = update.Hidden = saveNew.Hidden = !views.IsViewChanged;
            note.StringValue = views.ViewNote ?? "";
            note.Hidden = views.ViewNote is null;
            message.StringValue = views.ViewUndo.Message ?? "";
            message.Hidden = views.ViewUndo.Message is null;
            undo.Hidden = !views.ViewUndo.HasUndo;
            line.Hidden = changed.Hidden && note.Hidden && message.Hidden;
        }
        _bindings.Properties(views, [nameof(SmartViewsViewModel.IsViewChanged), nameof(SmartViewsViewModel.ViewNote)], Show);
        _bindings.Properties(views.ViewUndo, [nameof(UndoSlot.Message), nameof(UndoSlot.HasUndo)], Show);
        return line;
    }

    /// <summary>Saved Searches: each one, opened by choosing it, with a pin on those pinned; then saving the search showing.</summary>
    void ShowSavedSearches(NSButton under)
    {
        var views = library.SmartViews;
        views.ShowSavedSearchRows();
        var items = new List<NSMenuItem>();
        foreach (var row in views.SavedSearches)
        {
            var item = new NSMenuItem(row.Name, (_, _) => views.OpenSavedSearchCommand.Execute(row));
            if (row.IsPinned) item.Image = NSImage.GetSystemSymbol("pin.fill", "Pinned")!;
            items.Add(item);
        }
        if (views.CanSaveView)
        {
            if (items.Count > 0) items.Add(NSMenuItem.SeparatorItem);
            items.Add(new NSMenuItem("Save Current Search…", (_, _) => views.SaveViewCommand.Execute(null)));
        }
        Pop(under, items);
    }

    /// <summary>Save search, Save as new and Rename's name, as a sheet.</summary>
    static NSAlert NameSheet(SmartViewDialogViewModel dialog, NSWindow host)
    {
        var alert = new NSAlert { MessageText = dialog.Heading };
        var name = new NSTextField(new CGRect(0, 0, 280, 24)) { StringValue = dialog.Name, PlaceholderString = "Name" };
        alert.AccessoryView = name;
        alert.Window.InitialFirstResponder = name;
        alert.AddButton(dialog.ActionText).KeyEquivalent = "\r";
        alert.AddButton("Cancel").KeyEquivalent = "\u001b";
        // A question that can wait doesn't stop the app quitting.
        alert.Window.PreventsApplicationTerminationWhenModal = false;
        alert.BeginSheet(host, response =>
        {
            dialog.Name = name.StringValue;
            if ((long)response == (long)NSAlertButtonReturn.First && dialog.SaveCommand.CanExecute(null)) dialog.SaveCommand.Execute(null);
            else dialog.CancelCommand.Execute(null);
        });
        return alert;
    }

    /// <summary>What the last change to many books did, with its Undo.</summary>
    NSStackView UndoLine()
    {
        var undo = library.Selection.Undo;
        var message = NSTextField.CreateWrappingLabel("");
        message.Font = NSFont.SystemFontOfSize(12)!;
        var button = Small("Undo", undo.UndoCommand);
        var line = NSStackView.FromViews([message, button]);
        line.Spacing = 10;
        _bindings.Properties(undo, [nameof(UndoSlot.Message), nameof(UndoSlot.HasUndo)], () =>
        {
            message.StringValue = undo.Message ?? "";
            line.Hidden = undo.Message is null;
            button.Hidden = !undo.HasUndo;
        });
        return line;
    }

    NSButton Small(string title, System.Windows.Input.ICommand? command)
    {
        var button = NSButton.CreateButton(title, () => { });
        button.ControlSize = NSControlSize.Small;
        if (command is not null) _bindings.Command(button, command);
        return button;
    }

    static void Pop(NSButton under, IEnumerable<NSMenuItem> items)
    {
        var menu = new NSMenu();
        foreach (var item in items) menu.AddItem(item);
        if (menu.Count == 0) menu.AddItem(new NSMenuItem("None yet") { Enabled = false });
        menu.PopUpMenu(null, new CGPoint(0, under.Bounds.Height + 4), under);
    }

    /// <summary>
    /// Sort, the filters most used (kind, game system, type, folder), Clear, and grid or list: what the Windows
    /// Library's bar offers first, each the page's own choice.
    /// </summary>
    NSStackView FilterBar()
    {
        var filters = library.Filters;
        var sort = ChoicePopup.Create(_bindings, library, nameof(LibraryViewModel.SortChoices), () => library.SortChoices, () => library.SortChoice, c => library.SortChoice = c);
        _bindings.Property(library, nameof(LibraryViewModel.SortChoice), () =>
        {
            sort.SelectItem(IndexOf(library.SortChoices, library.SortChoice));
            _list?.ShowSort(library.SortChoice.Value);
        });
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
        // Organize turns into Done where it stands, as Select does on the iPhone, so leaving is where entering was.
        var organize = Small("Organize", null);
        // As wide as Organize either way, so Done doesn't pull the buttons after it along.
        organize.WidthAnchor.ConstraintGreaterThanOrEqualTo(organize.FittingSize.Width).Active = true;
        organize.Activated += (_, _) =>
        {
            var command = library.Selection.Books.IsActive ? library.Selection.StopSelectingCommand : library.Selection.StartSelectingCommand;
            if (command.CanExecute(null)) command.Execute(null);
        };
        _bindings.Property(library.Selection.Books, nameof(BookSelection.IsActive), () =>
        {
            var active = library.Selection.Books.IsActive;
            organize.Title = active ? "Done" : "Organize";
            organize.TintProminence = active ? NSTintProminence.Primary : NSTintProminence.Automatic;
        });
        var views = library.SmartViews;
        // One menu for the saved searches and saving this one, as two buttons of text would run together.
        var saved = Small("Saved Searches", null);
        saved.Image = NSImage.GetSystemSymbol("chevron.down", null)!;
        saved.ImagePosition = NSCellImagePosition.ImageTrailing;
        saved.Activated += (_, _) => ShowSavedSearches(saved);
        _bindings.Properties(views, [nameof(SmartViewsViewModel.CanSaveView), nameof(SmartViewsViewModel.HasSavedSearches)],
            () => saved.Hidden = !views.CanSaveView && !views.HasSavedSearches);
        var bar = NSStackView.FromViews([sort, kind, system, type, folder, clear, layout, organize, saved]);
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
            AddColumn(new NSTableColumn("tick") { Title = "", Width = 22, Hidden = true });
            foreach (var (id, title, width) in new[] { ("title", "Title", 320.0), ("byline", "By", 200.0), ("system", "System", 150.0),
                ("kind", "Kind", 130.0), ("levels", "Levels", 90.0), ("size", "Size", 170.0) })
            {
                var column = new NSTableColumn(id) { Title = title, Width = (nfloat)width };
                // A heading that is one of the Library's sorts sorts by it when clicked, as the Mac's tables do.
                if (SortOf(id) is not null) column.SortDescriptorPrototype = new NSSortDescriptor(id, true);
                AddColumn(column);
            }
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

        bool _showingSort;

        static Bibliotaph.Index.LibrarySort? SortOf(string column) => column switch
        {
            "title" => Bibliotaph.Index.LibrarySort.Title,
            "byline" => Bibliotaph.Index.LibrarySort.Publisher,
            _ => null,
        };

        /// <summary>The heading of the sort chosen in the bar, if it has a column, shows it.</summary>
        public void ShowSort(Bibliotaph.Index.LibrarySort sort)
        {
            _showingSort = true;
            var column = TableColumns().FirstOrDefault(c => SortOf(c.Identifier) == sort);
            SortDescriptors = column?.SortDescriptorPrototype is { } prototype ? [prototype] : [];
            _showingSort = false;
        }

        void SortBy(string column)
        {
            var library = _owner.Library;
            if (_showingSort || SortOf(column) is not { } sort || library.SortChoices.FirstOrDefault(c => c.Value == sort) is not { } choice) return;
            library.SortChoice = choice;
        }

        sealed class ListSource(BookList list) : NSTableViewDelegate, INSTableViewDataSource
        {
        [Export("numberOfRowsInTableView:")]
        public nint GetRowCount(NSTableView tableView) => list.Owner.Library.Items.Count;

        public override NSView GetViewForItem(NSTableView tableView, NSTableColumn tableColumn, nint row)
        {
            var item = list.Owner.Library.Items[(int)row];
            if (tableColumn.Identifier == "tick") return CoverItem.Tick(item.IsSelected);
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

        [Export("tableView:sortDescriptorsDidChange:")]
        public void SortDescriptorsChanged(NSTableView tableView, NSSortDescriptor[] oldDescriptors)
        {
            if (list.SortDescriptors is [{ Key: { } key }, ..]) list.SortBy(key);
        }

        public override void SelectionDidChange(NSNotification notification)
        {
            var library = list.Owner.Library;
            if (list.SelectedRow < 0 || list.SelectedRow >= library.Items.Count) return;
            list.Owner.Clicked(library.Items[(int)list.SelectedRow]);
            if (list.Owner.Organizing) list.DeselectAll(null);
        }
        }

        [Export("openRow:")]
        void OpenRow(NSObject sender)
        {
            if (ClickedRow < 0 || ClickedRow >= _owner.Library.Items.Count) return;
            var item = _owner.Library.Items[(int)ClickedRow];
            if (_owner.Library.OpenFromCardCommand.CanExecute(item)) _owner.Library.OpenFromCardCommand.Execute(item);
        }

        public override void KeyDown(NSEvent theEvent)
        {
            if (!BookGrid.OrganizeKey(_owner, theEvent)) base.KeyDown(theEvent);
        }

        public override bool PerformKeyEquivalent(NSEvent theEvent) => BookGrid.OrganizeKey(_owner, theEvent) || base.PerformKeyEquivalent(theEvent);

        /// <summary>A tick column, there in Organize mode only.</summary>
        public void ShowTicks(bool organizing)
        {
            if (FindTableColumn((NSString)"tick") is { } column) column.Hidden = !organizing;
            ReloadData();
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
        public override void KeyDown(NSEvent theEvent)
        {
            if (!OrganizeKey(owner, theEvent)) base.KeyDown(theEvent);
        }

        public override bool PerformKeyEquivalent(NSEvent theEvent) => OrganizeKey(owner, theEvent) || base.PerformKeyEquivalent(theEvent);

        /// <summary>In Organize mode, Esc leaves it and Cmd-A ticks every book shown, as Ctrl+A does on Windows.</summary>
        internal static bool OrganizeKey(LibraryController owner, NSEvent theEvent)
        {
            if (!owner.Organizing) return false;
            var selection = owner.Library.Selection;
            if (theEvent.KeyCode == 53)
            {
                selection.StopSelectingCommand.Execute(null);
                return true;
            }
            if (theEvent.ModifierFlags.HasFlag(NSEventModifierMask.CommandKeyMask) && theEvent.CharactersIgnoringModifiers == "a")
            {
                if (selection.SelectAllCommand.CanExecute(null)) selection.SelectAllCommand.Execute(null);
                return true;
            }
            return false;
        }
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
            item.ShowTick(owner.Organizing);
            return item;
        }

        /// <summary>A click shows the book's details, as on Windows; a double-click opens it.</summary>
        public override void ItemsSelected(NSCollectionView collectionView, NSSet indexPaths)
        {
            if (indexPaths.AnyObject is not NSIndexPath path || (int)path.Item >= owner.Library.Items.Count) return;
            owner.Clicked(owner.Library.Items[(int)path.Item]);
            // In Organize mode a click is a tick, so nothing stays selected for the next click on it to miss.
            if (owner.Organizing) collectionView.DeselectAll(null);
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
    NSImageView _tick = null!;
    LibraryItemViewModel? _book;
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

        _tick = Tick(false);

        var root = new NSView();
        foreach (var view in new NSView[] { _frame, _initial, _cover, _tick, _title, _meta })
        {
            view.TranslatesAutoresizingMaskIntoConstraints = false;
            (view == _cover || view == _initial || view == _tick ? _frame : root).AddSubview(view);
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
            _tick.TopAnchor.ConstraintEqualTo(_frame.TopAnchor, 8),
            _tick.LeadingAnchor.ConstraintEqualTo(_frame.LeadingAnchor, 8),
            _tick.WidthAnchor.ConstraintEqualTo(22),
            _tick.HeightAnchor.ConstraintEqualTo(22),
            _title.TopAnchor.ConstraintEqualTo(_frame.BottomAnchor, 8),
            _title.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            _title.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            _meta.TopAnchor.ConstraintEqualTo(_title.BottomAnchor, 2),
            _meta.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            _meta.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
        ]);
        View = root;
    }

    /// <summary>A tick, filled when the book is ticked, on a cover or a row in Organize mode.</summary>
    internal static NSImageView Tick(bool ticked)
    {
        var view = new NSImageView { WantsLayer = true };
        view.Layer!.ShadowOpacity = 0.6f;
        view.Layer.ShadowRadius = 2;
        view.Layer.ShadowOffset = CGSize.Empty;
        SetTick(view, ticked);
        return view;
    }

    // A white check on the accent, so it reads on a blue cover as on any other.
    static void SetTick(NSImageView view, bool ticked)
    {
        var image = NSImage.GetSystemSymbol(ticked ? "checkmark.circle.fill" : "circle", ticked ? "Ticked" : "Not ticked")!;
        view.SymbolConfiguration = NSImageSymbolConfiguration.Create(17, NSFontWeight.Semibold);
        view.Image = ticked ? image.GetImage(NSImageSymbolConfiguration.Create([NSColor.White, NSColor.ControlAccent]))! : image;
        view.ContentTintColor = ticked ? null! : NSColor.White;
    }

    public void ShowTick(bool organizing)
    {
        var ticked = _book?.IsSelected == true;
        _tick.Hidden = !organizing;
        SetTick(_tick, ticked);
        if (_frame.Layer is { } layer) layer.BorderWidth = ticked && organizing ? 3 : Selected ? 3 : 0;
    }

    public void Show(LibraryItemViewModel book, MacCovers covers)
    {
        _book = book;
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
