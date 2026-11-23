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
public sealed class LibraryController(LibraryViewModel library, MacCovers covers) : NSViewController, ITourAnchors, IWindowTitle
{
    readonly Bindings _bindings = new();
    BookGrid _grid = null!;

    /// <summary>"Your library", a collection's or a saved search's name, with how many it holds under it.</summary>
    public void FollowTitle(Bindings bindings, Action<string, string> show) =>
        bindings.Properties(library, [nameof(LibraryViewModel.Heading), nameof(LibraryViewModel.CountText), nameof(LibraryViewModel.CountLabel)],
            () => show(library.Heading, library.CountText.Length == 0 ? "" : $"{library.CountText} {library.CountLabel}"));

    /// <summary>The covers, or the list, as the Library shows its books.</summary>
    public NSView? Anchor(Core.TourAnchor anchor) =>
        anchor == Core.TourAnchor.FirstBooks ? (library.Layout == Bibliotaph.Index.LibraryLayout.List ? _list : _grid.GetItem(0)?.View ?? _grid) : null;

    BookList _list = null!;
    GridSource? _source;

    internal LibraryViewModel Library => library;

    internal MacCovers Covers => covers;

    public override void LoadView()
    {
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
        _grid.RegisterForDraggedTypes([BookGrid.CardType]);
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
        var moreFilters = MoreFilters();
        var activeFilters = ActiveFilters();
        var filterBar = FilterBar();
        foreach (var part in new NSView[] { filterBar, moreFilters, activeFilters, CoverageLine(), ScopeLine(), SubCollections(), OrganizeBar(), UndoLine(), SavedSearchLine() })
            bar.AddArrangedSubview(part);
        var pageActions = PageActions();
        _list = new BookList(this);
        _list.ShowSort(library.SortChoice.Value);
        var listScroll = new NSScrollView { DocumentView = _list, HasVerticalScroller = true, DrawsBackground = false };

        var root = new NSView();
        var hits = new PageHitsView(library, covers);
        // Organize's Books panel takes the trailing side while it's open, and the covers make room for it.
        var books = new BooksPanel(library.Selection, () => _grid.Drag);
        foreach (var view in new NSView[] { pageActions, bar, issue, scroll, listScroll, hits, empty, books })
        {
            view.TranslatesAutoresizingMaskIntoConstraints = false;
            root.AddSubview(view);
        }
        NSLayoutConstraint.ActivateConstraints(
        [
            // Below the toolbar, which the full-size content view draws under.
            // The page's name and count are in the toolbar; the filters start the page, with its own actions beside them.
            bar.TopAnchor.ConstraintEqualTo(root.SafeAreaLayoutGuide.TopAnchor, 12),
            bar.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor, 24),
            bar.TrailingAnchor.ConstraintLessThanOrEqualTo(root.TrailingAnchor, -24),
            filterBar.TrailingAnchor.ConstraintLessThanOrEqualTo(pageActions.LeadingAnchor, -12),
            pageActions.TopAnchor.ConstraintEqualTo(bar.TopAnchor),

            pageActions.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor, -24),
            issue.TopAnchor.ConstraintEqualTo(bar.BottomAnchor, 6),
            issue.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor, 24),
            issue.TrailingAnchor.ConstraintLessThanOrEqualTo(root.TrailingAnchor, -24),
            scroll.TopAnchor.ConstraintEqualTo(issue.BottomAnchor, 8),
            scroll.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            scroll.BottomAnchor.ConstraintEqualTo(root.BottomAnchor),
            books.TopAnchor.ConstraintEqualTo(scroll.TopAnchor, 4),
            books.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor, -20),
            books.BottomAnchor.ConstraintEqualTo(root.BottomAnchor, -16),
            books.WidthAnchor.ConstraintEqualTo(300),
            hits.TopAnchor.ConstraintEqualTo(scroll.TopAnchor),
            hits.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            hits.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            hits.BottomAnchor.ConstraintEqualTo(root.BottomAnchor),
            listScroll.TopAnchor.ConstraintEqualTo(scroll.TopAnchor),
            listScroll.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor, 16),
            listScroll.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor, -16),
            listScroll.BottomAnchor.ConstraintEqualTo(root.BottomAnchor),
            empty.CenterXAnchor.ConstraintEqualTo(root.CenterXAnchor),
            empty.CenterYAnchor.ConstraintEqualTo(root.CenterYAnchor),
            empty.WidthAnchor.ConstraintLessThanOrEqualTo(440),
        ]);
        View = root;
        var wide = scroll.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor);
        var beside = scroll.TrailingAnchor.ConstraintEqualTo(books.LeadingAnchor, -12);
        _bindings.Properties(library.Selection.Panel, [nameof(OrganizePanelViewModel.IsOpen)], () =>
        {
            var open = library.Selection.Panel.IsOpen && library.Selection.Books.IsActive;
            books.Hidden = !open;
            wide.Active = !open;
            beside.Active = open;
        });
        _bindings.Property(library.Selection.Books, nameof(BookSelection.IsActive), () =>
        {
            var open = library.Selection.Panel.IsOpen && library.Selection.Books.IsActive;
            books.Hidden = !open;
            wide.Active = !open;
            beside.Active = open;
        });

        _bindings.Property(library, nameof(LibraryViewModel.Hits), () => hits.Show(library.Hits));
        _bindings.Property(library, nameof(LibraryViewModel.IssueText), () =>
        {
            issue.StringValue = library.IssueText ?? "";
            issue.Hidden = library.IssueText is null;
        });
        _bindings.Properties(library, [nameof(LibraryViewModel.IsEmpty), nameof(LibraryViewModel.EmptyTitle), nameof(LibraryViewModel.EmptyMessage),
            nameof(LibraryViewModel.EmptyActionText), nameof(LibraryViewModel.Layout), nameof(LibraryViewModel.ShowHits),
            nameof(LibraryViewModel.ShowGrid), nameof(LibraryViewModel.ShowList)], () =>
        {
            empty.Hidden = !library.IsEmpty;
            // A search's pages show in place of the covers or the list, while its Inside documents tab is chosen.
            scroll.Hidden = !library.ShowGrid;
            listScroll.Hidden = !library.ShowList;
            hits.Hidden = !library.ShowHits;
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
        ElsewhereSheet.Follow(_bindings, library, () => View.Window);
        // Cards dropped on a card that could mean two things: a menu where they were dropped asks which.
        _bindings.Property(library.Selection, nameof(LibrarySelectionViewModel.DropChoice), () =>
        {
            if (library.Selection.DropChoice is not { } choice) return;
            var menu = new NSMenu { AutoEnablesItems = false };
            menu.AddItem(new NSMenuItem(choice.GoesWithLabel, (_, _) => library.Selection.ChooseGoesWithCommand.Execute(null)));
            menu.AddItem(new NSMenuItem(choice.SameBookLabel, (_, _) => library.Selection.ChooseSameBookCommand.Execute(null)));
            NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
            {
                menu.PopUpMenu(null, _grid.ConvertPointFromView(DropLocation, null), _grid);
                // A menu closed without a choice does nothing, as Esc does on Windows.
                if (library.Selection.IsChoosingDrop) library.Selection.CancelDropChoiceCommand.Execute(null);
            });
        });
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
    /// <summary>Where the last cards were dropped, in window coordinates, for the menu that asks what the drop means.</summary>
    internal CGPoint DropLocation { get; set; }

    /// <summary>What dragging cards carries, which the Books panel's rows take too.</summary>
    internal const string CardType = BookGrid.CardType;

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
        var edit = Small("Edit Metadata…", selection.EditSelectedCommand);
        var toCollection = Small("Add to Collection", null);
        toCollection.Activated += (_, _) => Pop(toCollection, ((ICollectionChoices)library).MenuChoices.Select(choice =>
            new NSMenuItem(choice.Label, (_, _) => library.AddSelectedToCollectionCommand.Execute(new CollectionRequest(library, choice)))));
        var toBinder = Small("Add to Binder", null);
        toBinder.Activated += (_, _) => Pop(toBinder, ((ISessionChoices)library).MenuChoices.Select(choice =>
            new NSMenuItem(choice.Label, (_, _) => library.AddSelectedToSessionCommand.Execute(new SessionRequest(library, choice)))));
        var join = Small("Same Book…", selection.JoinSelectedCommand);
        var pack = Small("Pack Together…", selection.PackSelectedCommand);
        var removeParts = Small("Remove Adventures", selection.RemoveSelectedPartsCommand);
        var removeFromCollection = Small("Remove from Collection", library.RemoveSelectedFromCollectionCommand);
        var export = Small("Export These…", selection.ExportSelectedCommand);
        var clear = Small("Clear", selection.ClearSelectionCommand);
        var actions = NSStackView.FromViews([summary, edit, toCollection, toBinder, join, pack, removeParts, removeFromCollection, export, clear]);
        actions.Spacing = 8;
        actions.SetCustomSpacing(16, summary);

        var links = ChoicePopup.Create(_bindings, selection, nameof(LibrarySelectionViewModel.LinkChoice), () => selection.LinkChoices,
            () => selection.LinkChoice, c => selection.LinkChoice = c);
        var keep = Small("", selection.KeepShownCommand);
        // The Books panel: books to drop maps and handouts on, or give the ticked cards to with Goes With.
        var panel = Small("Books", selection.Panel.ToggleCommand);
        panel.Image = NSImage.GetSystemSymbol("books.vertical", null)!;
        panel.ImagePosition = NSCellImagePosition.ImageLeading;
        panel.ToolTip = "Books to drop maps and handouts on";
        _bindings.Property(selection.Panel, nameof(OrganizePanelViewModel.IsOpen), () =>
            panel.TintProminence = selection.Panel.IsOpen ? NSTintProminence.Secondary : NSTintProminence.Automatic);
        var organize = NSStackView.FromViews([NSTextField.CreateLabel("Show"), links, keep, panel]);
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
        var update = Small("Update Saved Search", views.UpdateViewCommand);
        var saveNew = Small("Save as New…", views.SaveViewAsNewCommand);
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

    NSPopover? _savedPopover;

    /// <summary>
    /// Saved Searches, as Windows' list under its button: each search, opened by clicking its name, with its pin, Rename
    /// and Delete beside it; then saving the search showing. A popover, since a menu can't hold buttons on each line.
    /// </summary>
    void ShowSavedSearches(NSButton under)
    {
        var views = library.SmartViews;
        views.ShowSavedSearchRows();
        _savedPopover?.Close();
        var list = new NSStackView { Orientation = NSUserInterfaceLayoutOrientation.Vertical, Alignment = NSLayoutAttribute.Leading, Spacing = 2 };
        list.EdgeInsets = new NSEdgeInsets(10, 10, 10, 10);
        var heading = NSTextField.CreateLabel("Saved Searches");
        heading.Font = NSFont.SystemFontOfSize(10, NSFontWeight.Semibold)!;
        heading.TextColor = NSColor.SecondaryLabel;
        list.AddArrangedSubview(heading);
        var popover = new NSPopover { Behavior = NSPopoverBehavior.Transient, Animates = false };
        _savedPopover = popover;
        NSButton Icon(string symbol, string tip, Action action)
        {
            var button = NSButton.CreateButton(NSImage.GetSystemSymbol(symbol, tip)!, () =>
            {
                popover.Close();
                action();
            });
            button.Bordered = false;
            button.ContentTintColor = NSColor.SecondaryLabel;
            button.ToolTip = tip;
            return button;
        }
        foreach (var row in views.SavedSearches)
        {
            var name = NSButton.CreateButton(row.Name, () =>
            {
                popover.Close();
                views.OpenSavedSearchCommand.Execute(row);
            });
            name.Bordered = false;
            name.Alignment = NSTextAlignment.Left;
            name.ContentTintColor = NSColor.Label;
            name.ToolTip = $"Open {row.Name}";
            name.LineBreakMode = NSLineBreakMode.TruncatingTail;
            name.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
            name.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
            var pin = Icon(row.IsPinned ? "pin.fill" : "pin", row.PinAction, () => views.TogglePinSavedSearchCommand.Execute(row));
            if (row.IsPinned) pin.ContentTintColor = NSColor.ControlAccent;
            var line = NSStackView.FromViews([name, pin, Icon("pencil", "Rename", () => views.RenameSavedSearchCommand.Execute(row)),
                Icon("trash", "Delete. Your books stay as they are, and Undo brings it back.", () => views.DeleteSavedSearchCommand.Execute(row))]);
            line.Spacing = 6;
            list.AddArrangedSubview(line);
            line.WidthAnchor.ConstraintEqualTo(300).Active = true;
        }
        if (views.SavedSearches.Count == 0) list.AddArrangedSubview(PageParts.Secondary("No saved searches yet."));
        if (views.CanSaveView)
        {
            var save = NSButton.CreateButton("Save Current Search…", () =>
            {
                popover.Close();
                views.SaveViewCommand.Execute(null);
            });
            save.ControlSize = NSControlSize.Small;
            list.SetCustomSpacing(10, list.ArrangedSubviews[^1]);
            list.AddArrangedSubview(save);
        }
        var controller = new NSViewController { View = list };
        list.SetFrameSize(list.FittingSize);
        popover.ContentViewController = controller;
        popover.ContentSize = list.FittingSize;
        popover.Show(under.Bounds, under, NSRectEdge.MaxYEdge);
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

    /// <summary>The orders the books can be in, under a "Sort By" heading, the one in use ticked.</summary>
    internal IEnumerable<NSMenuItem> SortItems() => SortMenu(library);

    internal static IEnumerable<NSMenuItem> SortMenu(LibraryViewModel library)
    {
        yield return NSMenuItem.CreateSectionHeader("Sort By");
        foreach (var choice in library.SortChoices)
            yield return new NSMenuItem(choice.Label, (_, _) => library.SortChoice = choice)
            {
                State = ReferenceEquals(choice, library.SortChoice) ? NSCellStateValue.On : NSCellStateValue.Off,
            };
    }

    /// <summary>A short upright line between the filters and how the books are shown.</summary>
    static NSBox Divider()
    {
        var line = new NSBox { BoxType = NSBoxType.NSBoxSeparator };
        line.HeightAnchor.ConstraintEqualTo(16).Active = true;
        line.WidthAnchor.ConstraintEqualTo(1).Active = true;
        return line;
    }

    static void Pop(NSButton under, IEnumerable<NSMenuItem> items)
    {
        var menu = new NSMenu();
        foreach (var item in items) menu.AddItem(item);
        if (menu.Count == 0) menu.AddItem(new NSMenuItem("None Yet") { Enabled = false });
        menu.PopUpMenu(null, new CGPoint(0, under.Bounds.Height + 4), under);
    }

    /// <summary>
    /// The filters most used (kind, game system, type, folder) and Clear, then apart from them how the books are shown:
    /// their order, grid or list, and packs. What the Windows Library's bar offers first, each the page's own choice.
    /// </summary>
    NSStackView FilterBar()
    {
        var filters = library.Filters;
        // The order isn't a filter, so it doesn't look like one: it stands with the layout, says "Sort:" and has the
        // sort symbol, and its menu ticks the order in use, as the sort buttons in Finder and Mail do.
        var sort = Small("", null);
        sort.Image = NSImage.GetSystemSymbol("arrow.up.arrow.down", "Sort")!;
        sort.ImagePosition = NSCellImagePosition.ImageLeading;
        sort.ToolTip = "The order the books are in";
        sort.Activated += (_, _) => Pop(sort, SortItems());
        _bindings.Properties(library, [nameof(LibraryViewModel.SortChoice), nameof(LibraryViewModel.SortChoices)], () =>
        {
            sort.Title = $"Sort: {library.SortChoice.Label}";
            _list?.ShowSort(library.SortChoice.Value);
        });
        var kind = ChoicePopup.Create(_bindings, filters, nameof(LibraryFilterState.KindChoice), () => filters.KindChoices, () => filters.KindChoice, c => filters.KindChoice = c);
        var system = ChoicePopup.Create(_bindings, filters, nameof(LibraryFilterState.SystemChoice), () => filters.SystemChoices, () => filters.SystemChoice, c => filters.SystemChoice = c);
        var type = ChoicePopup.Create(_bindings, filters, nameof(LibraryFilterState.TypeChoice), () => filters.TypeChoices, () => filters.TypeChoice, c => filters.TypeChoice = c);
        var folder = ChoicePopup.Create(_bindings, filters, nameof(LibraryFilterState.FolderChoice), () => filters.FolderChoices, () => filters.FolderChoice, c => filters.FolderChoice = c);
        var clear = NSButton.CreateButton("Clear Filters", () => filters.ClearFiltersCommand.Execute(null));
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
        // The page's own Saved Searches command, as the Windows button runs, opens the same list.
        _bindings.Property(views, nameof(SmartViewsViewModel.IsSavedSearchesOpen), () =>
        {
            if (!views.IsSavedSearchesOpen) return;
            views.IsSavedSearchesOpen = false;
            Bindings.OnMain(() => ShowSavedSearches(saved));
        });
        _bindings.Properties(views, [nameof(SmartViewsViewModel.CanSaveView), nameof(SmartViewsViewModel.HasSavedSearches)],
            () => saved.Hidden = !views.CanSaveView && !views.HasSavedSearches);
        // While searching: the books that match, or the pages inside them the words were found on.
        var results = NSSegmentedControl.FromLabels(["Documents", "Inside documents"], NSSegmentSwitchTracking.SelectOne, () => { });
        results.ControlSize = NSControlSize.Small;
        results.Activated += (_, _) =>
        {
            if (results.SelectedSegment == 0) library.IsDocumentsTab = true;
            else library.IsPagesTab = true;
        };
        _bindings.Properties(library, [nameof(LibraryViewModel.IsSearching), nameof(LibraryViewModel.Tab), nameof(LibraryViewModel.DocumentsTabLabel),
            nameof(LibraryViewModel.PagesTabLabel), nameof(LibraryViewModel.ShowLayoutChoice)], () =>
        {
            results.Hidden = !library.IsSearching;
            results.SetLabel(library.DocumentsTabLabel, 0);
            results.SetLabel(library.PagesTabLabel, 1);
            results.SelectedSegment = library.IsPagesTab ? 1 : 0;
            layout.Hidden = !library.ShowLayoutChoice;
        });
        var more = Small("", filters.ToggleMoreFiltersCommand);
        more.BezelStyle = NSBezelStyle.Inline;
        _bindings.Property(filters, nameof(LibraryFilterState.MoreFiltersLabel), () => more.Title = filters.MoreFiltersLabel);
        // Packs as one card each, or every file in them on a card of its own.
        var packs = NSSegmentedControl.FromImages(
            [NSImage.GetSystemSymbol("square.stack.3d.up", "Packs")!, NSImage.GetSystemSymbol("doc.on.doc", "Each file")!], NSSegmentSwitchTracking.SelectOne, () => { });
        packs.ControlSize = NSControlSize.Small;
        packs.SetToolTip("Packs as one card each", 0);
        packs.SetToolTip("Each file in a pack on a card of its own", 1);
        packs.Activated += (_, _) => library.ShowPackFiles = packs.SelectedSegment == 1;
        _bindings.Property(library, nameof(LibraryViewModel.ShowPackFiles), () => packs.SelectedSegment = library.ShowPackFiles ? 1 : 0);
        var bar = NSStackView.FromViews([results, kind, system, type, folder, more, clear, Divider(), sort, layout, packs, organize, saved]);
        bar.Spacing = 8;
        bar.SetCustomSpacing(16, results);
        bar.SetCustomSpacing(16, clear);
        bar.SetCustomSpacing(16, more);
        return bar;
    }

    /// <summary>
    /// The filters past the common ones, as Windows folds them under More filters: an adventure's level, players, style
    /// and length; where a book is owned, bought or found; whether the AI read it, its copies, source, collections,
    /// adventures and what goes with it; and its themes, settings and the rest, typed with suggestions. Each shows once
    /// some book has it.
    /// </summary>
    NSStackView MoreFilters()
    {
        var filters = library.Filters;
        // Rows of a few each, rather than a view that wraps itself, so the bar's height never chases the panel's.
        var panel = new NSStackView { Orientation = NSUserInterfaceLayoutOrientation.Vertical, Alignment = NSLayoutAttribute.Leading, Spacing = 6 };
        var views = new List<(NSView View, Func<bool> Shows)>();
        void Add<T>(string property, Func<IReadOnlyList<Choice<T>>> choices, Func<Choice<T>> chosen, Action<Choice<T>> choose, Func<bool> shows)
        {
            var popup = ChoicePopup.Create(_bindings, filters, property, choices, () => chosen(), choose);
            views.Add((popup, shows));
        }
        Add(nameof(LibraryFilterState.LevelChoice), () => filters.LevelChoices, () => filters.LevelChoice, c => filters.LevelChoice = c, () => true);
        var unknown = NSButton.CreateCheckbox("Include unknown levels", () => { });
        unknown.ControlSize = NSControlSize.Small;
        unknown.Font = NSFont.SystemFontOfSize(NSFont.SmallSystemFontSize)!;
        unknown.Activated += (_, _) => filters.IncludeUnknownLevels = unknown.State == NSCellStateValue.On;
        views.Add((unknown, () => filters.HasLevelChoice));
        Add(nameof(LibraryFilterState.PlayersChoice), () => filters.PlayersChoices, () => filters.PlayersChoice, c => filters.PlayersChoice = c, () => true);
        Add(nameof(LibraryFilterState.StyleChoice), () => filters.StyleChoices, () => filters.StyleChoice, c => filters.StyleChoice = c, () => filters.ShowStyleChoice);
        Add(nameof(LibraryFilterState.DurationChoice), () => filters.DurationChoices, () => filters.DurationChoice, c => filters.DurationChoice = c, () => filters.ShowDurationChoice);
        Add(nameof(LibraryFilterState.OwnChoice), () => filters.OwnChoices, () => filters.OwnChoice, c => filters.OwnChoice = c, () => filters.ShowOwnChoice);
        Add(nameof(LibraryFilterState.StoreChoice), () => filters.StoreChoices, () => filters.StoreChoice, c => filters.StoreChoice = c, () => filters.ShowStoreChoice);
        Add(nameof(LibraryFilterState.FoundChoice), () => filters.FoundChoices, () => filters.FoundChoice, c => filters.FoundChoice = c, () => filters.ShowFoundChoice);
        Add(nameof(LibraryFilterState.AiChoice), () => filters.AiChoices, () => filters.AiChoice, c => filters.AiChoice = c, () => filters.ShowAiChoice);
        Add(nameof(LibraryFilterState.CopiesChoice), () => filters.CopiesChoices, () => filters.CopiesChoice, c => filters.CopiesChoice = c, () => filters.ShowCopiesChoice);
        Add(nameof(LibraryFilterState.SourceChoice), () => filters.SourceChoices, () => filters.SourceChoice, c => filters.SourceChoice = c, () => true);
        Add(nameof(LibraryFilterState.CollectionChoice), () => filters.CollectionChoices, () => filters.CollectionChoice, c => filters.CollectionChoice = c, () => filters.ShowCollectionChoice);
        Add(nameof(LibraryFilterState.PartsChoice), () => filters.PartsChoices, () => filters.PartsChoice, c => filters.PartsChoice = c, () => filters.ShowPartsChoice);
        Add(nameof(LibraryFilterState.CompanionsChoice), () => filters.CompanionsChoices, () => filters.CompanionsChoice, c => filters.CompanionsChoice = c, () => filters.ShowCompanionsChoice);
        foreach (var chip in filters.ChipFilters) views.Add((ChipBox(chip), () => chip.IsOffered));

        void Show()
        {
            unknown.State = filters.IncludeUnknownLevels ? NSCellStateValue.On : NSCellStateValue.Off;
            foreach (var row in panel.ArrangedSubviews)
            {
                foreach (var view in ((NSStackView)row).ArrangedSubviews) ((NSStackView)row).RemoveArrangedSubview(view);
                row.RemoveFromSuperview();
            }
            foreach (var row in views.Where(v => v.Shows()).Select(v => v.View).Chunk(6))
            {
                var line = NSStackView.FromViews([.. row]);
                line.Spacing = 8;
                panel.AddArrangedSubview(line);
            }
            panel.Hidden = !filters.ShowMoreFilters;
        }
        _bindings.Properties(filters, [nameof(LibraryFilterState.ShowMoreFilters), nameof(LibraryFilterState.HasLevelChoice), nameof(LibraryFilterState.IncludeUnknownLevels),
            nameof(LibraryFilterState.ShowStyleChoice), nameof(LibraryFilterState.ShowDurationChoice), nameof(LibraryFilterState.ShowOwnChoice),
            nameof(LibraryFilterState.ShowStoreChoice), nameof(LibraryFilterState.ShowFoundChoice), nameof(LibraryFilterState.ShowAiChoice),
            nameof(LibraryFilterState.ShowCopiesChoice), nameof(LibraryFilterState.ShowCollectionChoice), nameof(LibraryFilterState.ShowPartsChoice),
            nameof(LibraryFilterState.ShowCompanionsChoice)], Show);
        foreach (var chip in filters.ChipFilters) _bindings.Property(chip, nameof(ChipFilterViewModel.IsOffered), Show);
        return panel;
    }

    /// <summary>A theme, setting or the like, typed with the values books have as suggestions; picking one makes a chip.</summary>
    NSComboBox ChipBox(ChipFilterViewModel chip)
    {
        var box = new NSComboBox { PlaceholderString = chip.Hint, Completes = false, ControlSize = NSControlSize.Small };
        box.Font = NSFont.SystemFontOfSize(NSFont.SmallSystemFontSize)!;
        box.ToolTip = chip.Label;
        box.WidthAnchor.ConstraintEqualTo(150).Active = true;
        // Filling the list in moves its selection, which mustn't pick anything.
        var filling = false;
        box.Changed += (_, _) =>
        {
            if (!filling) chip.Text = box.StringValue;
        };
        void Pick(int at)
        {
            if (filling || at < 0 || at >= chip.Suggestions.Count) return;
            var suggestion = chip.Suggestions[at];
            filling = true;
            box.StringValue = "";
            filling = false;
            chip.PickCommand.Execute(suggestion);
        }
        box.SelectionChanged += (_, _) =>
        {
            var at = (int)box.SelectedIndex;
            Bindings.OnMain(() => Pick(at));
        };
        box.Activated += (_, _) =>
        {
            if (filling) return;
            var typed = box.StringValue.Trim();
            var at = chip.Suggestions.ToList().FindIndex(s => s.Label.Equals(typed, StringComparison.CurrentCultureIgnoreCase));
            Pick(at >= 0 ? at : chip.Suggestions.Count > 0 ? 0 : -1);
        };
        _bindings.Collection(chip.Suggestions, _ =>
        {
            filling = true;
            box.RemoveAll();
            foreach (var suggestion in chip.Suggestions) box.Add(new NSString($"{suggestion.Label}  ({suggestion.Count})"));
            filling = false;
        });
        return box;
    }

    /// <summary>
    /// How much of the library the words were looked for in, while some books can't be searched inside yet, or what an
    /// Ask search took the words to mean, as Windows says beside its count.
    /// </summary>
    NSTextField CoverageLine()
    {
        var line = NSTextField.CreateWrappingLabel("");
        line.Font = NSFont.SystemFontOfSize(11)!;
        line.TextColor = NSColor.SecondaryLabel;
        _bindings.Property(library, nameof(LibraryViewModel.Coverage), () =>
        {
            line.StringValue = library.Coverage ?? "";
            line.Hidden = library.Coverage is null;
        });
        return line;
    }

    /// <summary>The part of the library shown, as Favorites or a collection, with a × that shows the whole library.</summary>
    NSStackView ScopeLine()
    {
        var label = NSTextField.CreateLabel("");
        label.Font = NSFont.SystemFontOfSize(11)!;
        var clear = NSButton.CreateButton(NSImage.GetSystemSymbol("xmark", "Show your whole library")!, () => library.ClearScopeCommand.Execute(null));
        clear.Bordered = false;
        clear.SymbolConfiguration = NSImageSymbolConfiguration.Create(8, NSFontWeight.Bold);
        clear.ToolTip = "Show your whole library";
        var row = NSStackView.FromViews([label, clear]);
        row.Spacing = 4;
        var chip = new Tinted(row, Palette.Soft, 9, new NSEdgeInsets(2, 8, 2, 6));
        var line = NSStackView.FromViews([chip]);
        _bindings.Property(library, nameof(LibraryViewModel.Scope), () =>
        {
            label.StringValue = library.ScopeChip;
            line.Hidden = !library.HasScope;
        });
        return line;
    }

    /// <summary>A collection's sub-collections as cards above its books, and the switch that leaves their books out.</summary>
    NSStackView SubCollections()
    {
        var cards = new NSStackView { Spacing = 16, Alignment = NSLayoutAttribute.Top };
        var only = NSButton.CreateCheckbox("Only books added here", () => { });
        only.ControlSize = NSControlSize.Small;
        only.ToolTip = "Leave out the books that are only in the collections inside this one";
        only.Activated += (_, _) => library.OnlyAddedHere = only.State == NSCellStateValue.On;
        var section = NSStackView.FromViews([cards, only]);
        section.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        section.Alignment = NSLayoutAttribute.Leading;
        section.Spacing = 8;
        void Show()
        {
            foreach (var view in cards.ArrangedSubviews) view.RemoveFromSuperview();
            foreach (var card in library.SubCollections)
                cards.AddArrangedSubview(new ShelfCard(card.Name, card.CountLabel, card.Covers, covers, () => library.OpenCollectionCommand.Execute(card), card.Badge));
            section.Hidden = !library.HasSubCollections;
            only.State = library.OnlyAddedHere ? NSCellStateValue.On : NSCellStateValue.Off;
        }
        _bindings.Collection(library.SubCollections, _ => Show());
        _bindings.Properties(library, [nameof(LibraryViewModel.HasSubCollections), nameof(LibraryViewModel.OnlyAddedHere)], Show);
        return section;
    }

    /// <summary>The filters that are on, as chips whose × turns one off, so they show with More filters folded away.</summary>
    NSStackView ActiveFilters()
    {
        var filters = library.Filters;
        var chips = new NSStackView { Spacing = 6 };
        _bindings.Collection(filters.ActiveFilters, _ => Show());
        void Show()
        {
            foreach (var view in chips.ArrangedSubviews) view.RemoveFromSuperview();
            foreach (var view in filters.ActiveFilters.Select(chip =>
            {
                var label = NSTextField.CreateLabel(chip.Label);
                label.Font = NSFont.SystemFontOfSize(11)!;
                var remove = NSButton.CreateButton(NSImage.GetSystemSymbol("xmark", chip.Name)!, () => chip.Clear.Execute(null));
                remove.Bordered = false;
                remove.SymbolConfiguration = NSImageSymbolConfiguration.Create(8, NSFontWeight.Bold);
                remove.ToolTip = chip.Name;
                var row = NSStackView.FromViews([label, remove]);
                row.Spacing = 4;
                return new Tinted(row, Palette.Soft, 9, new NSEdgeInsets(2, 8, 2, 6));
            })) chips.AddArrangedSubview(view);
            chips.Hidden = filters.ActiveFilters.Count == 0;
        }
        Show();
        return chips;
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
        void Add(string title, System.Windows.Input.ICommand command, object? parameter = null, string? tip = null)
        {
            if (!command.CanExecute(parameter ?? item)) return;
            var entry = new NSMenuItem(title, (_, _) => command.Execute(parameter ?? item));
            if (tip is not null) entry.ToolTip = tip;
            menu.AddItem(entry);
        }
        Add("Open", library.OpenFromCardCommand);
        Add("Open in New Window", library.OpenBookInNewWindowCommand);
        Add("Show Details", library.OpenDetailsCommand);
        // Ask this book opens it with its Ask panel showing, as on Windows; a book with no readable text has none.
        if (item.CanAsk) Add("Ask This Book…", library.AskBookCommand);
        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(Submenu("Add to Collection", ((ICollectionChoices)library).MenuChoices
            .Select(choice => new NSMenuItem(choice.Label, (_, _) => library.AddToCollection(new CollectionRequest(item, choice))))));
        menu.AddItem(Submenu("Add to Binder", ((ISessionChoices)library).MenuChoices
            .Select(choice => new NSMenuItem(choice.Label, (_, _) => library.AddToSession(new SessionRequest(item, choice))))));
        Add(item.FavoriteAction, library.ToggleFavoriteCommand);
        // Named as the menu opens, since the book may have been pinned or unpinned from the sidebar since.
        Add(MenuChoiceSource.PinLabel(library, item), library.TogglePinBookCommand, tip: "Pinned books show in the sidebar and on Home");
        Add("Cataloged Wrong…", library.CatalogedWrongCommand, tip: "Correct its details: the values Bibliotaph guessed are marked");
        menu.AddItem(NSMenuItem.SeparatorItem);
        if (item.ShowOpenOnline) Add(item.OnlineAction, library.OpenOnlineCommand);
        if (item.CanOpenElsewhere) Add("Open in Another App", library.OpenElsewhereCommand);
        // Each shows only for a book it can run on: one whose text was read, and one whose text was forgotten.
        Add("Forget Its Text", library.ForgetTextCommand,
            tip: "Removes its pages, search text, cover and AI results from Bibliotaph, and stops reading it. The file isn't touched.");
        Add("Read It Again", library.ReadTextAgainCommand);
        Add("Remove from Library…", library.RemoveFromLibraryCommand, tip: "Takes the book you added out of your library. Undo brings it back.");
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
            var item = SelectedRow >= 0 && SelectedRow < _owner.Library.Items.Count ? _owner.Library.Items[(int)SelectedRow] : null;
            if (!BookGrid.OrganizeKey(_owner, theEvent) && !BookGrid.OpenKey(_owner, theEvent, item)) base.KeyDown(theEvent);
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
    sealed class BookGrid(LibraryController owner) : NSCollectionView, INSDraggingSource
    {
        /// <summary>What dragging carries: only ever this app's own cards, within this window.</summary>
        public const string CardType = "online.bibliotaph.cards";

        NSEvent? _pressed;
        OrganizeDrag? _drag;

        /// <summary>The cards being dragged, for the Books panel's rows to take.</summary>
        public OrganizeDrag? Drag => _drag;
        LibraryItemViewModel? _over;

        // In Organize mode a card can be picked up and dropped on another: a map on its book, two copies of one book.
        public override void MouseDown(NSEvent theEvent)
        {
            _pressed = owner.Organizing ? theEvent : null;
            base.MouseDown(theEvent);
        }

        public override void MouseDragged(NSEvent theEvent)
        {
            if (_pressed is not { } pressed || _drag is not null || ItemAt(pressed) is not { } card)
            {
                base.MouseDragged(theEvent);
                return;
            }
            _pressed = null;
            if (owner.Library.Selection.Pick(card) is not { } drag) return;
            _drag = drag;
            var item = new NSPasteboardItem();
            item.SetStringForType(card.EntryId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), CardType);
            var dragging = new NSDraggingItem(item);
            var at = ConvertPointFromView(pressed.LocationInWindow, null);
            var image = GetIndexPath(at) is { } path && GetItem(path) is { View: { } view } ? Snapshot(view) : NSImage.GetSystemSymbol("book", null)!;
            dragging.SetDraggingFrame(new CGRect(at.X - 40, at.Y - 54, 80, 108), image);
            BeginDraggingSession([dragging], theEvent, this);
        }

        static NSImage Snapshot(NSView view)
        {
            var rep = view.BitmapImageRepForCachingDisplayInRect(view.Bounds)!;
            view.CacheDisplay(view.Bounds, rep);
            var image = new NSImage(view.Bounds.Size);
            image.AddRepresentation(rep);
            return image;
        }

        LibraryItemViewModel? ItemAt(NSEvent theEvent) => ItemAt(ConvertPointFromView(theEvent.LocationInWindow, null));

        LibraryItemViewModel? ItemAt(CGPoint point) =>
            GetIndexPath(point) is { } path && (int)path.Item < owner.Library.Items.Count ? owner.Library.Items[(int)path.Item] : null;

        [Export("draggingSession:sourceOperationMaskForDraggingContext:")]
        public NSDragOperation SourceOperationMaskForDraggingContext(NSDraggingSession session, NSDraggingContext context) =>
            context == NSDraggingContext.WithinApplication && owner.Organizing ? NSDragOperation.Generic : NSDragOperation.None;

        [Export("draggingSession:endedAtPoint:operation:")]
        public void DraggingSessionEnded(NSDraggingSession session, CGPoint screenPoint, NSDragOperation operation)
        {
            _drag = null;
            Mark(null);
        }

        // A card the cards can go on is outlined; one they can't takes nothing, and the tip says why.
        public override NSDragOperation DraggingEntered(INSDraggingInfo sender) => DraggingUpdated(sender);

        public override NSDragOperation DraggingUpdated(INSDraggingInfo sender)
        {
            if (_drag is not { } drag) return NSDragOperation.None;
            var target = ItemAt(ConvertPointFromView(sender.DraggingLocation, null));
            if (target is null || drag.EntryIds.Contains(target.EntryId))
            {
                Mark(null);
                return NSDragOperation.None;
            }
            var plan = LibrarySelectionViewModel.PlanOn(drag, target);
            Mark(plan.CanDrop ? target : null);
            ToolTip = plan.Label;
            return plan.CanDrop ? NSDragOperation.Generic : NSDragOperation.None;
        }

        public override void DraggingExited(INSDraggingInfo? sender) => Mark(null);

        public override bool PerformDragOperation(INSDraggingInfo sender)
        {
            if (_drag is not { } drag || ItemAt(ConvertPointFromView(sender.DraggingLocation, null)) is not { } target) return false;
            Mark(null);
            ToolTip = null!;
            owner.DropLocation = sender.DraggingLocation;
            owner.Library.Selection.DropOnAsync(drag, target).FireAndForget();
            return true;
        }

        /// <summary>The card the cards would go on, outlined in the accent.</summary>
        void Mark(LibraryItemViewModel? card)
        {
            if (ReferenceEquals(card, _over)) return;
            foreach (var item in VisibleItems)
                if (item is CoverItem cover) cover.Marked(card is not null && ReferenceEquals(card, cover.Book));
            _over = card;
        }

        public override void KeyDown(NSEvent theEvent)
        {
            var item = SelectionIndexPaths.AnyObject is NSIndexPath path && (int)path.Item < owner.Library.Items.Count ? owner.Library.Items[(int)path.Item] : null;
            if (!OrganizeKey(owner, theEvent) && !OpenKey(owner, theEvent, item)) base.KeyDown(theEvent);
        }

        /// <summary>
        /// Return opens the book in focus, as a double-click does, and Shift-Return in a window of its own, as Windows'
        /// Enter and Shift+Enter; in Organize mode Return ticks it instead. ⌘↓ opens it too, as in Finder.
        /// </summary>
        internal static bool OpenKey(LibraryController owner, NSEvent theEvent, LibraryItemViewModel? item)
        {
            if (item is null) return false;
            var mods = theEvent.ModifierFlags & (NSEventModifierMask.ShiftKeyMask | NSEventModifierMask.CommandKeyMask | NSEventModifierMask.AlternateKeyMask | NSEventModifierMask.ControlKeyMask);
            var library = owner.Library;
            var isReturn = theEvent.KeyCode is 36 or 76;
            if (isReturn && owner.Organizing && mods == 0)
            {
                library.ActivateCommand.Execute(item);
                return true;
            }
            if (isReturn && mods == NSEventModifierMask.ShiftKeyMask)
            {
                library.OpenBookInNewWindowCommand.Execute(item);
                return true;
            }
            if ((isReturn && mods == 0) || (theEvent.KeyCode == 125 && mods == NSEventModifierMask.CommandKeyMask))
            {
                if (library.OpenFromCardCommand.CanExecute(item)) library.OpenFromCardCommand.Execute(item);
                return true;
            }
            return false;
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
            // ⌘Z takes back the newest change made in Organize mode that still stands, as Ctrl+Z does on Windows.
            if (theEvent.ModifierFlags.HasFlag(NSEventModifierMask.CommandKeyMask) && theEvent.CharactersIgnoringModifiers == "z")
            {
                selection.UndoLastCommand.Execute(null);
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
    NSStackView _badges = null!;
    Tinted _away = null!;
    NSImageView _site = null!;
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
        // What the card holds or goes with, at the cover's foot, and whether its file is here, at its top.
        _badges = new NSStackView { Spacing = 4 };
        var away = NSTextField.CreateLabel("");
        away.Font = NSFont.SystemFontOfSize(10, NSFontWeight.Semibold)!;
        _away = new Tinted(away, NSColor.WindowBackground.ColorWithAlphaComponent(0.85f), 4, new NSEdgeInsets(1, 5, 1, 5));

        var root = new NSView();
        // A book owned online has its site's icon in the cover's corner, or a cloud with site icons off.
        _site = new NSImageView { ImageScaling = NSImageScale.ProportionallyUpOrDown, WantsLayer = true };
        _site.Layer!.CornerRadius = 4;
        _site.Layer.MasksToBounds = true;
        _site.Layer.BackgroundColor = NSColor.WindowBackground.ColorWithAlphaComponent(0.85f).CGColor;
        foreach (var view in new NSView[] { _frame, _initial, _cover, _tick, _badges, _away, _site, _title, _meta })
        {
            view.TranslatesAutoresizingMaskIntoConstraints = false;
            (view == _cover || view == _initial || view == _tick || view == _badges || view == _away || view == _site ? _frame : root).AddSubview(view);
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
            _badges.LeadingAnchor.ConstraintEqualTo(_frame.LeadingAnchor, 6),
            _badges.BottomAnchor.ConstraintEqualTo(_frame.BottomAnchor, -6),
            _badges.TrailingAnchor.ConstraintLessThanOrEqualTo(_frame.TrailingAnchor, -6),
            _site.TrailingAnchor.ConstraintEqualTo(_frame.TrailingAnchor, -6),
            _site.BottomAnchor.ConstraintEqualTo(_frame.BottomAnchor, -6),
            _site.WidthAnchor.ConstraintEqualTo(18),
            _site.HeightAnchor.ConstraintEqualTo(18),
            _away.TopAnchor.ConstraintEqualTo(_frame.TopAnchor, 8),
            _away.TrailingAnchor.ConstraintEqualTo(_frame.TrailingAnchor, -8),
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

    /// <summary>The book whose card this is now, as the grid reuses cards.</summary>
    public LibraryItemViewModel? Book => _book;

    /// <summary>
    /// Outlined while cards being dragged would go on it, more strongly than a tick's outline; as it was once they
    /// move on.
    /// </summary>
    public void Marked(bool on)
    {
        if (_frame?.Layer is not { } layer) return;
        if (on) layer.BorderWidth = 6;
        else ShowTick(true);
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
        _covers = covers;
        _coverName = book.CoverName;
        _title.StringValue = book.Title;
        _meta.StringValue = book.Meta;
        _initial.StringValue = book.Initial;
        _cover.AlphaValue = book.IsAway ? 0.45f : 1;
        _cover.Image = (_coverName is null ? null : covers.TryGet(_coverName))!;
        _initial.Hidden = _cover.Image is not null;
        if (_coverName is { } name && _cover.Image is null) LoadAsync(name, covers).FireAndForget();
        ShowBadges(book);
    }

    /// <summary>A pack's files, its adventures, its copies, what goes with it: a symbol and a count each, the tip saying more.</summary>
    void ShowBadges(LibraryItemViewModel book)
    {
        foreach (var view in _badges.ArrangedSubviews) view.RemoveFromSuperview();
        foreach (var badge in book.Badges)
        {
            var symbol = badge.Kind switch
            {
                Index.CardBadge.GoesWith => "link",
                Index.CardBadge.Pack => "square.stack.3d.up",
                Index.CardBadge.Adventures => "book",
                Index.CardBadge.Companions => "paperclip",
                Index.CardBadge.Copies => "doc.on.doc",
                _ => "circle",
            };
            var icon = new NSImageView { Image = NSImage.GetSystemSymbol(symbol, badge.Tip)!, ContentTintColor = NSColor.Label };
            icon.SymbolConfiguration = NSImageSymbolConfiguration.Create(9, NSFontWeight.Semibold);
            var count = NSTextField.CreateLabel(badge.Count);
            count.Font = NSFont.SystemFontOfSize(10, NSFontWeight.Semibold)!;
            count.Hidden = badge.Count.Length == 0;
            var content = NSStackView.FromViews([icon, count]);
            content.Spacing = 2;
            // A book the rules only suggested it goes with is marked fainter, as Windows dashes it.
            var pill = new Tinted(content, NSColor.WindowBackground.ColorWithAlphaComponent(badge.Suggested ? 0.55f : 0.85f), 4, new NSEdgeInsets(1, 4, 1, 5));
            pill.ToolTip = badge.Tip;
            _badges.AddArrangedSubview(pill);
        }
        ((NSTextField)_away.Subviews[0]).StringValue = book.AvailabilityLabel;
        _away.Hidden = book.AvailabilityLabel.Length == 0;
        _away.ToolTip = book.AvailabilityTip ?? "";
        ShowSite(book);
    }

    MacCovers? _sites;
    MacCovers? _covers;

    void ShowSite(LibraryItemViewModel book)
    {
        _site.Hidden = book.SiteIconLink is null;
        if (book.SiteIconLink is not { } link || _covers is not { } covers) return;
        _site.Image = NSImage.GetSystemSymbol("cloud", "Owned online")!;
        _site.ToolTip = "Owned online";
        if (!ReferenceEquals(_sites, covers))
        {
            // The setting turned on or off shows or hides the icons on cards already made.
            _sites = covers;
            covers.Sites.Changed += (_, _) => Bindings.OnMain(() =>
            {
                if (_book is { } shown) ShowSite(shown);
            });
        }
        LoadSiteAsync(book, link, covers).FireAndForget();
    }

    async Task LoadSiteAsync(LibraryItemViewModel book, string link, MacCovers covers)
    {
        var icon = await covers.Sites.GetAsync(link);
        Bindings.OnMain(() =>
        {
            if (icon is not null && ReferenceEquals(_book, book)) _site.Image = icon;
        });
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
