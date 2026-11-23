using AppKit;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.ViewModels;
using Bibliotaph.Presentation.Services;
using Foundation;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// Edit binder: its title and date, its notes, and its items in order by section, as on Windows. An item's label and
/// note are typed in its row; it's dragged by its grip to another place or under another heading, or moved with ⌥↑ and
/// ⌥↓; Return opens it, ⇧Return in a window of its own, Delete takes it out; its menu has the rest. Sections are added,
/// renamed and deleted, and what was last done shows with Undo. Done goes back to the binder at the table.
/// </summary>
public sealed class BinderEditController(SessionPackViewModel pack) : NSViewController, IWindowTitle
{
    readonly Bindings _bindings = new();
    Bindings _rows = new();
    NSStackView _list = null!;

    /// <summary>The binder's name, with when it's for and how many items under it.</summary>
    public void FollowTitle(Bindings bindings, Action<string, string> show) =>
        bindings.Property(pack, nameof(SessionPackViewModel.Pack), () => show(pack.Title, string.Join(" · ", new[] { pack.Eyebrow, pack.CountLabel }.Where(t => t.Length > 0))));

    public override void LoadView()
    {
        var (root, content) = PageParts.ScrollingPage();
        var actions = NSStackView.FromViews([
            Command("Done", pack.DoneCommand),
            Command("Rename or Date…", pack.EditCommand),
            Command("Add Section…", pack.AddSectionCommand),
            Suggested(),
            Command("Find Resources…", pack.FindResourcesCommand),
            Command("Show Its Books", pack.ShowBooksCommand),
            Command("Duplicate", pack.DuplicateCommand),
            Command("Delete Binder…", pack.DeleteCommand),
        ]);
        actions.Spacing = 8;
        var notesLabel = PageParts.Section("Notes for the session");
        var notes = new NSTextField { PlaceholderString = "What to remember at the table" };
        notes.Changed += (_, _) => pack.Notes = notes.StringValue;
        notes.HeightAnchor.ConstraintEqualTo(60).Active = true;
        _list = new NSStackView { Orientation = NSUserInterfaceLayoutOrientation.Vertical, Alignment = NSLayoutAttribute.Leading, Spacing = 6 };
        var undo = UndoBar.Create(_bindings, pack.Actions.Undo);
        foreach (var view in new NSView[] { actions, undo, notesLabel, notes, _list }) content.AddArrangedSubview(view);
        undo.WidthAnchor.ConstraintEqualTo(content.WidthAnchor, 1, -48).Active = true;
        notes.WidthAnchor.ConstraintEqualTo(content.WidthAnchor, 1, -48).Active = true;
        _list.WidthAnchor.ConstraintEqualTo(content.WidthAnchor, 1, -48).Active = true;
        View = root;

        _bindings.Property(pack, nameof(SessionPackViewModel.Notes), () =>
        {
            if (notes.CurrentEditor is null) notes.StringValue = pack.Notes;
        });
        _bindings.Collection(pack.Rows, _ => Rebuild());
        Rebuild();
        DialogSheet.Follow(_bindings, pack.Actions, () => View.Window);
        NornisSheet.Follow(_bindings, pack, nameof(SessionPackViewModel.NornisDialog), () => pack.NornisDialog, () => View.Window);
        // The item just moved keeps the keyboard, so ⌥↑ can be pressed again.
        pack.FocusItemRequested += OnFocusItem;
        _bindings.Add(() => pack.FocusItemRequested -= OnFocusItem);
    }

    void OnFocusItem(object? sender, long id) => Bindings.OnMain(() =>
    {
        foreach (var view in _list.ArrangedSubviews)
            if (view is ItemRowView row && row.Row.Id == id) View.Window?.MakeFirstResponder(row);
    });

    void Rebuild()
    {
        _rows.Dispose();
        _rows = new Bindings();
        foreach (var view in _list.ArrangedSubviews) view.RemoveFromSuperview();
        if (pack.Rows.Count == 0)
            _list.AddArrangedSubview(PageParts.Secondary("Nothing in this binder yet. Add pages from the reader, or books from the Library's menus."));
        foreach (var row in pack.Rows)
        {
            if (row is SessionSectionRow section)
            {
                var name = PageParts.Section(section.Name);
                var line = NSStackView.FromViews([name, new NSView(), Command("Rename…", pack.RenameSectionCommand, section), Command("Delete", pack.DeleteSectionCommand, section)]);
                // Dropping an item on a heading puts it first under it.
                var heading = new DropRow(line, section, pack);
                _list.AddArrangedSubview(heading);
                heading.WidthAnchor.ConstraintEqualTo(_list.WidthAnchor).Active = true;
            }
            else if (row is SessionItemRow item)
            {
                var view = new ItemRowView(item, pack, _rows);
                _list.AddArrangedSubview(view);
                view.WidthAnchor.ConstraintEqualTo(_list.WidthAnchor).Active = true;
            }
        }
    }

    /// <summary>The sections binders usually have, as Windows offers them: a menu of those, each added with a click.</summary>
    NSButton Suggested()
    {
        var button = NSButton.CreateButton("Suggested Sections", () => { });
        button.ControlSize = NSControlSize.Small;
        button.Image = NSImage.GetSystemSymbol("chevron.down", null)!;
        button.ImagePosition = NSCellImagePosition.ImageTrailing;
        button.Activated += (_, _) =>
        {
            var menu = new NSMenu();
            foreach (var name in SessionPackViewModel.SuggestedSections)
                menu.AddItem(new NSMenuItem(name, (_, _) => pack.AddSuggestedSectionCommand.Execute(name)));
            menu.PopUpMenu(null, new CoreGraphics.CGPoint(0, button.Bounds.Height + 4), button);
        };
        return button;
    }

    NSButton Command(string title, System.Windows.Input.ICommand command, object? parameter = null)
    {
        var button = NSButton.CreateButton(title, () => { });
        button.ControlSize = NSControlSize.Small;
        (parameter is null ? _bindings : _rows).Command(button, command, parameter is null ? null : () => parameter);
        return button;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _rows.Dispose();
            _bindings.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>What dragging an item carries: its id, within this window.</summary>
    const string ItemType = "online.bibliotaph.binder-item";

    /// <summary>A row an item can be dropped on, outlined while one is over it.</summary>
    class DropRow : NSView
    {
        readonly object _target;
        protected readonly SessionPackViewModel Pack;

        public DropRow(NSView content, object target, SessionPackViewModel pack)
        {
            _target = target;
            Pack = pack;
            WantsLayer = true;
            Layer!.CornerRadius = 6;
            content.TranslatesAutoresizingMaskIntoConstraints = false;
            AddSubview(content);
            NSLayoutConstraint.ActivateConstraints(
            [
                content.TopAnchor.ConstraintEqualTo(TopAnchor, 4),
                content.BottomAnchor.ConstraintEqualTo(BottomAnchor, -4),
                content.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 6),
                content.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -6),
            ]);
            RegisterForDraggedTypes([ItemType]);
        }

        SessionItemRow? Dragged(INSDraggingInfo info) =>
            info.DraggingPasteboard.GetStringForType(ItemType) is { } text && long.TryParse(text, out var id)
                ? Pack.Rows.OfType<SessionItemRow>().FirstOrDefault(r => r.Id == id) : null;

        public override NSDragOperation DraggingEntered(INSDraggingInfo sender)
        {
            var ok = Dragged(sender) is { } dragged && !ReferenceEquals(dragged, _target);
            Layer!.BorderWidth = ok ? 2 : 0;
            Layer.BorderColor = NSColor.ControlAccent.CGColor;
            return ok ? NSDragOperation.Move : NSDragOperation.None;
        }

        public override void DraggingExited(INSDraggingInfo? sender) => Layer!.BorderWidth = 0;

        public override bool PerformDragOperation(INSDraggingInfo sender)
        {
            Layer!.BorderWidth = 0;
            if (Dragged(sender) is not { } dragged) return false;
            Pack.DropAsync(dragged, _target).FireAndForget();
            return true;
        }
    }

    /// <summary>
    /// An item: its grip, number, label and note to type in, where it is and why it may not open, and its buttons. It
    /// takes the keyboard when clicked, for Return, ⇧Return, Delete and ⌥↑ ⌥↓.
    /// </summary>
    sealed class ItemRowView : DropRow, INSDraggingSource
    {
        public SessionItemRow Row { get; }

        public ItemRowView(SessionItemRow item, SessionPackViewModel pack, Bindings bindings) : base(Line(item, pack, bindings), item, pack)
        {
            Row = item;
            ToolTip = item.AccessibleName;
        }

        static NSStackView Line(SessionItemRow item, SessionPackViewModel pack, Bindings bindings)
        {
            var grip = new Grip { Image = NSImage.GetSystemSymbol("line.3.horizontal", "Drag to move")!, ContentTintColor = NSColor.TertiaryLabel, ToolTip = "Drag to move" };
            var number = NSTextField.CreateLabel(item.Number);
            number.Font = NSFont.MonospacedDigitSystemFontOfSize(12, NSFontWeight.Medium)!;
            number.TextColor = NSColor.SecondaryLabel;
            // The label as typed, its placeholder the book's title, which a blank label shows.
            var label = Inline(item.Label, item.BookTitle, NSFont.SystemFontOfSize(13, NSFontWeight.Semibold)!, text => item.Label = text);
            var note = Inline(item.Note, "Add a note", NSFont.SystemFontOfSize(11)!, text => item.Note = text);
            var where = NSTextField.CreateLabel(item.Where);
            where.Font = NSFont.SystemFontOfSize(11)!;
            where.TextColor = NSColor.SecondaryLabel;
            where.LineBreakMode = NSLineBreakMode.TruncatingTail;
            where.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
            var text = NSStackView.FromViews([label, where, note]);
            text.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
            text.Alignment = NSLayoutAttribute.Leading;
            text.Spacing = 1;
            if (item.Reason is { } reason)
            {
                var why = NSTextField.CreateWrappingLabel(reason);
                why.Font = NSFont.SystemFontOfSize(11)!;
                why.TextColor = NSColor.SystemOrange;
                var line = NSStackView.FromViews([why]);
                if (item.CanShowInFolder)
                {
                    var show = NSButton.CreateButton("Show in Finder", () => pack.ShowInFolderCommand.Execute(item));
                    show.ControlSize = NSControlSize.Small;
                    line.AddArrangedSubview(show);
                }
                text.AddArrangedSubview(line);
            }
            foreach (var view in new NSView[] { label, note }) view.WidthAnchor.ConstraintEqualTo(text.WidthAnchor).Active = true;
            text.SetContentHuggingPriorityForOrientation(200, NSLayoutConstraintOrientation.Horizontal);

            var open = Button("Open", () => pack.OpenItemCommand.Execute(item));
            open.ToolTip = "Open (Return)";
            var window = Icon("macwindow.badge.plus", "Open in a window of its own (⇧Return)", () => pack.OpenItemInNewWindowCommand.Execute(item));
            var more = Icon("ellipsis.circle", "More", null);
            more.Activated += (_, _) => ItemMenu(item, pack).PopUpMenu(null, new CoreGraphics.CGPoint(0, more.Bounds.Height + 4), more);
            var row = NSStackView.FromViews([grip, number, text, open, window, more]);
            row.Alignment = NSLayoutAttribute.CenterY;
            row.Spacing = 10;
            if (item.IsDimmed) text.AlphaValue = 0.6f;
            return row;
        }

        /// <summary>Everything the row can do, as its menu and its Control-click.</summary>
        static NSMenu ItemMenu(SessionItemRow item, SessionPackViewModel pack)
        {
            var menu = new NSMenu { AutoEnablesItems = false };
            NSMenuItem Item(string title, System.Windows.Input.ICommand command, object parameter, string key = "", NSEventModifierMask mask = 0)
            {
                var entry = new NSMenuItem(title, (_, _) => command.Execute(parameter)) { KeyEquivalent = key, Enabled = command.CanExecute(parameter) };
                if (key.Length > 0) entry.KeyEquivalentModifierMask = mask;
                menu.AddItem(entry);
                return entry;
            }
            Item("Open", pack.OpenItemCommand, item, "\r", 0);
            Item("Open in a Window of Its Own", pack.OpenItemInNewWindowCommand, item, "\r", NSEventModifierMask.ShiftKeyMask);
            menu.AddItem(NSMenuItem.SeparatorItem);
            Item("Move Up", pack.MoveUpCommand, item, "\uF700", NSEventModifierMask.AlternateKeyMask);
            Item("Move Down", pack.MoveDownCommand, item, "\uF701", NSEventModifierMask.AlternateKeyMask);
            foreach (var move in item.MoveTargets) Item(move.Label, pack.MoveToSectionCommand, move);
            if (pack.HasNornisWorlds && SessionPackViewModel.CanSendToNornis(item))
            {
                menu.AddItem(NSMenuItem.SeparatorItem);
                Item("Send to Nornis…", pack.SendToNornisCommand, item);
            }
            if (item.CanShowInFolder) Item("Show in Finder", pack.ShowInFolderCommand, item);
            menu.AddItem(NSMenuItem.SeparatorItem);
            Item("Remove from Binder", pack.RemoveItemCommand, item, "\b", 0);
            return menu;
        }

        /// <summary>Text typed in place: plain until clicked, saved when Return is pressed or the keyboard moves on.</summary>
        static NSTextField Inline(string text, string placeholder, NSFont font, Action<string> save)
        {
            var field = new NSTextField
            {
                StringValue = text,
                PlaceholderString = placeholder,
                Font = font,
                Bordered = false,
                DrawsBackground = false,
                FocusRingType = NSFocusRingType.Exterior,
                LineBreakMode = NSLineBreakMode.TruncatingTail,
            };
            field.Cell.Scrollable = true;
            field.EditingEnded += (_, _) =>
            {
                if (field.StringValue != text) save(field.StringValue);
            };
            return field;
        }

        static NSButton Button(string title, Action action)
        {
            var button = NSButton.CreateButton(title, action);
            button.ControlSize = NSControlSize.Small;
            return button;
        }

        static NSButton Icon(string symbol, string tip, Action? action)
        {
            var button = NSButton.CreateButton(NSImage.GetSystemSymbol(symbol, tip)!, action ?? (() => { }));
            button.Bordered = false;
            button.ContentTintColor = NSColor.SecondaryLabel;
            button.ToolTip = tip;
            return button;
        }

        public override NSMenu MenuForEvent(NSEvent theEvent) => ItemMenu(Row, Pack);

        public override bool AcceptsFirstResponder() => true;

        public override bool BecomeFirstResponder()
        {
            Layer!.BackgroundColor = NSColor.SelectedContentBackground.ColorWithAlphaComponent(0.25f).CGColor;
            return true;
        }

        public override bool ResignFirstResponder()
        {
            Layer!.BackgroundColor = null;
            return true;
        }

        public override void MouseDown(NSEvent theEvent)
        {
            Window?.MakeFirstResponder(this);
            if (theEvent.ClickCount == 2) Pack.OpenItemCommand.Execute(Row);
        }

        public override void KeyDown(NSEvent theEvent)
        {
            var option = theEvent.ModifierFlags.HasFlag(NSEventModifierMask.AlternateKeyMask);
            var shift = theEvent.ModifierFlags.HasFlag(NSEventModifierMask.ShiftKeyMask);
            switch (theEvent.KeyCode)
            {
                case 126 when option:
                    Pack.MoveUpCommand.Execute(Row);
                    return;
                case 125 when option:
                    Pack.MoveDownCommand.Execute(Row);
                    return;
                case 36 or 76:
                    (shift ? Pack.OpenItemInNewWindowCommand : Pack.OpenItemCommand).Execute(Row);
                    return;
                case 51 or 117:
                    Pack.RemoveItemCommand.Execute(Row);
                    return;
                case 126 or 125:
                    // Plain ↑ and ↓ go to the item above or below.
                    if (Superview is NSStackView list && Array.IndexOf(list.ArrangedSubviews, this) is var at and >= 0)
                    {
                        var step = theEvent.KeyCode == 125 ? 1 : -1;
                        for (var i = at + step; i >= 0 && i < list.ArrangedSubviews.Length; i += step)
                            if (list.ArrangedSubviews[i] is ItemRowView next)
                            {
                                Window?.MakeFirstResponder(next);
                                next.ScrollRectToVisible(next.Bounds);
                                break;
                            }
                    }
                    return;
                default:
                    base.KeyDown(theEvent);
                    return;
            }
        }

        // Dragging by the grip: the row's picture under the pointer, dropped on another row or a heading.
        internal void BeginDrag(NSEvent theEvent)
        {
            var item = new NSPasteboardItem();
            item.SetStringForType(Row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), ItemType);
            var dragging = new NSDraggingItem(item);
            var rep = BitmapImageRepForCachingDisplayInRect(Bounds)!;
            CacheDisplay(Bounds, rep);
            var image = new NSImage(Bounds.Size);
            image.AddRepresentation(rep);
            dragging.SetDraggingFrame(Bounds, image);
            BeginDraggingSession([dragging], theEvent, this);
        }

        [Export("draggingSession:sourceOperationMaskForDraggingContext:")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Objective-C calls it on the dragging source.")]
        public NSDragOperation SourceOperationMaskForDraggingContext(NSDraggingSession session, NSDraggingContext context) =>
            context == NSDraggingContext.WithinApplication ? NSDragOperation.Move : NSDragOperation.None;
    }

    /// <summary>The grip an item is dragged by.</summary>
    sealed class Grip : NSImageView
    {
        public override void MouseDown(NSEvent theEvent)
        {
        }

        public override void MouseDragged(NSEvent theEvent)
        {
            for (var view = Superview; view is not null; view = view.Superview)
                if (view is ItemRowView row)
                {
                    row.BeginDrag(theEvent);
                    return;
                }
        }

        public override void ResetCursorRects() => AddCursorRect(Bounds, NSCursor.OpenHandCursor);
    }
}
